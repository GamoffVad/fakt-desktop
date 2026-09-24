using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Llm;

/// <summary>
/// Google Gemini API (generativelanguage.googleapis.com): заголовок x-goog-api-key, models.list с pageToken,
/// generateContent с generationConfig.responseMimeType и responseJsonSchema (либо устаревшим responseSchema
/// в формате OpenAPI). Неверный ключ приходит как 400 INVALID_ARGUMENT/API_KEY_INVALID.
/// </summary>
public sealed class GeminiAdapter : LlmAdapterBase
{
    public GeminiAdapter(LlmHttp http, LlmProviderDescriptor descriptor) : base(http, descriptor)
    {
    }

    private static Dictionary<string, string> Headers(LlmRuntimeConfig config)
    {
        var headers = BaseHeaders(config);
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            headers["x-goog-api-key"] = config.ApiKey;
        }

        return headers;
    }

    private static string VersionOf(LlmRuntimeConfig config) => config.Profile.GetOption(ProviderCatalog.OptApiVersion, "v1beta");

    public override async Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var models = new List<ModelInfo>();
            string pageToken = null;
            string requestId = null;
            var pages = 0;
            do
            {
                var query = new List<KeyValuePair<string, string>> { new("pageSize", "1000") };
                if (pageToken != null)
                {
                    query.Add(new KeyValuePair<string, string>("pageToken", pageToken));
                }

                var url = UrlBuilder.Combine(config.Profile.BaseUrl, VersionOf(config) + "/models", query);
                var result = await SendAsync(HttpMethod.Get, url, null, Headers(config), config, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return ListFailure(result);
                }

                requestId ??= result.RequestId;
                pages++;
                var root = result.Json() as JObject;
                foreach (var item in (root?["models"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var methods = (item["supportedGenerationMethods"] as JArray)?.Select(m => (string)m).ToList() ?? new List<string>();
                    if (methods.Count > 0 && !methods.Contains("generateContent"))
                    {
                        continue;
                    }

                    var name = (string)item["name"] ?? string.Empty;
                    models.Add(new ModelInfo
                    {
                        Id = name.StartsWith("models/", StringComparison.Ordinal) ? name.Substring("models/".Length) : name,
                        DisplayName = (string)item["displayName"],
                        Description = (string)item["description"],
                        ContextLength = (long?)item["inputTokenLimit"],
                    });
                }

                pageToken = (string)root?["nextPageToken"];
            }
            while (!string.IsNullOrEmpty(pageToken) && pages < MaxModelPages);

            return ListSuccess(models, pages, requestId);
        }
        catch (LlmException ex)
        {
            return ListFailure(ex);
        }
    }

    public override async Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken)
    {
        var profile = config.Profile;
        if (!UrlBuilder.IsValidHttpUrl(profile.BaseUrl, out var urlError))
        {
            throw new LlmException(LlmErrorKind.Configuration, "Base URL: " + urlError);
        }

        var model = profile.ModelId?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            throw new LlmException(LlmErrorKind.Configuration, "Не выбрана модель.");
        }

        if (model.StartsWith("models/", StringComparison.Ordinal))
        {
            model = model.Substring("models/".Length);
        }

        var generation = new JObject { ["maxOutputTokens"] = request.MaxOutputTokens };
        if (request.Temperature.HasValue)
        {
            generation["temperature"] = request.Temperature.Value;
        }

        switch (request.Mode)
        {
            case StructuredOutputMode.JsonSchema:
                generation["responseMimeType"] = "application/json";
                if (string.Equals(profile.GetOption(ProviderCatalog.OptGeminiSchemaField, "responseJsonSchema"), "responseSchema", StringComparison.Ordinal))
                {
                    generation["responseSchema"] = ToOpenApiSchema(request.Schema);
                }
                else
                {
                    generation["responseJsonSchema"] = request.Schema;
                }

                break;
            case StructuredOutputMode.JsonObject:
                generation["responseMimeType"] = "application/json";
                break;
            case StructuredOutputMode.ToolCall:
                throw new LlmException(LlmErrorKind.UnsupportedResponseFormat, "Для Gemini используйте JSON Schema или JSON-режим.");
        }

        var body = new JObject
        {
            ["systemInstruction"] = new JObject { ["parts"] = new JArray { new JObject { ["text"] = request.SystemPrompt } } },
            ["contents"] = new JArray
            {
                new JObject { ["role"] = "user", ["parts"] = new JArray { new JObject { ["text"] = UserContentFor(request) } } },
            },
            ["generationConfig"] = generation,
        };

        var url = UrlBuilder.Combine(profile.BaseUrl, VersionOf(config) + "/models/" + Uri.EscapeDataString(model) + ":generateContent");
        var result = await SendAsync(HttpMethod.Post, url, body, Headers(config), config, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw LlmHttp.Classify(result);
        }

        if (!(result.Json() is JObject root))
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "Ответ провайдера не является JSON.", result.StatusCode, result.RequestId);
        }

        var usage = root["usageMetadata"] as JObject;
        var output = Int(usage?["candidatesTokenCount"]);
        var thoughts = Int(usage?["thoughtsTokenCount"]);
        var response = new LlmResponse
        {
            RequestId = result.RequestId ?? (string)root["responseId"],
            Latency = result.Elapsed,
            ModelReported = (string)root["modelVersion"],
            InputTokens = Int(usage?["promptTokenCount"]),
            OutputTokens = output.HasValue || thoughts.HasValue ? (output ?? 0) + (thoughts ?? 0) : (int?)null,
        };

        var blockReason = (string)root["promptFeedback"]?["blockReason"];
        var candidate = (root["candidates"] as JArray)?.FirstOrDefault() as JObject;
        if (candidate == null)
        {
            response.RawFinishReason = blockReason ?? "NO_CANDIDATES";
            response.FinishReason = blockReason != null ? LlmFinishReason.ContentFilter : LlmFinishReason.Other;
            return response;
        }

        var finish = (string)candidate["finishReason"];
        response.RawFinishReason = finish;
        response.FinishReason = finish switch
        {
            "STOP" => LlmFinishReason.Stop,
            "MAX_TOKENS" => LlmFinishReason.Length,
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" => LlmFinishReason.ContentFilter,
            null => LlmFinishReason.Unknown,
            _ => LlmFinishReason.Other,
        };

        var text = new StringBuilder();
        foreach (var part in (candidate["content"]?["parts"] as JArray ?? new JArray()).OfType<JObject>())
        {
            if ((bool?)part["thought"] == true)
            {
                continue;
            }

            text.Append((string)part["text"]);
        }

        response.Text = text.ToString();
        return response;
    }

    /// <summary>JSON Schema → подмножество OpenAPI для responseSchema: nullable вместо ["T","null"], без additionalProperties.</summary>
    internal static JObject ToOpenApiSchema(JObject schema)
    {
        var result = new JObject();
        var type = schema["type"];
        var nullable = false;
        string typeName = null;
        if (type is JArray types)
        {
            foreach (var t in types.Select(x => (string)x))
            {
                if (t == "null")
                {
                    nullable = true;
                }
                else
                {
                    typeName = t;
                }
            }
        }
        else
        {
            typeName = (string)type;
        }

        if (typeName != null)
        {
            result["type"] = typeName.ToUpperInvariant();
        }

        if (nullable)
        {
            result["nullable"] = true;
        }

        if (schema["enum"] is JArray values)
        {
            result["enum"] = new JArray(values.Where(v => v.Type != JTokenType.Null));
        }

        if (schema["properties"] is JObject properties)
        {
            var converted = new JObject();
            foreach (var property in properties.Properties())
            {
                converted[property.Name] = ToOpenApiSchema((JObject)property.Value);
            }

            result["properties"] = converted;
            result["propertyOrdering"] = new JArray(properties.Properties().Select(p => p.Name));
        }

        if (schema["required"] is JArray required)
        {
            result["required"] = required.DeepClone();
        }

        if (schema["items"] is JObject items)
        {
            result["items"] = ToOpenApiSchema(items);
        }

        foreach (var limit in new[] { "minItems", "maxItems" })
        {
            if (schema[limit] != null)
            {
                result[limit] = schema[limit].DeepClone();
            }
        }

        return result;
    }
}
