using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Llm;

/// <summary>
/// Собственный API Ollama: /api/tags (список моделей), /api/chat с format = JSON Schema или "json",
/// options.num_predict / num_ctx, stream=false. Сервер Ollama обычно работает на другой машине:
/// установка Ollama на Windows 7 не гарантируется.
/// </summary>
public sealed class OllamaAdapter : LlmAdapterBase
{
    public OllamaAdapter(LlmHttp http, LlmProviderDescriptor descriptor) : base(http, descriptor)
    {
    }

    private static Dictionary<string, string> Headers(LlmRuntimeConfig config)
    {
        var headers = BaseHeaders(config);
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            headers["Authorization"] = "Bearer " + config.ApiKey;
        }

        return headers;
    }

    public override async Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAsync(HttpMethod.Get, UrlBuilder.Combine(config.Profile.BaseUrl, "api/tags"), null, Headers(config), config, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return ListFailure(result);
            }

            var models = new List<ModelInfo>();
            foreach (var item in ModelItems(result.Json()))
            {
                var details = item["details"] as JObject;
                models.Add(new ModelInfo
                {
                    Id = (string)item["name"] ?? (string)item["model"],
                    DisplayName = (string)item["name"],
                    Description = details == null ? null : string.Join(", ", new[] { (string)details["family"], (string)details["parameter_size"], (string)details["quantization_level"] }.Where(s => !string.IsNullOrEmpty(s))),
                });
            }

            return ListSuccess(models.Where(IsTextModel).ToList(), 1, result.RequestId);
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

        if (string.IsNullOrWhiteSpace(profile.ModelId))
        {
            throw new LlmException(LlmErrorKind.Configuration, "Не выбрана модель.");
        }

        var options = new JObject { ["num_predict"] = request.MaxOutputTokens };
        if (request.Temperature.HasValue)
        {
            options["temperature"] = request.Temperature.Value;
        }

        if (int.TryParse(profile.GetOption(ProviderCatalog.OptNumCtx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numCtx) && numCtx > 0)
        {
            options["num_ctx"] = numCtx;
        }

        var body = new JObject
        {
            ["model"] = profile.ModelId,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JObject { ["role"] = "user", ["content"] = UserContentFor(request) },
            },
            ["stream"] = false,
            ["options"] = options,
        };

        var keepAlive = profile.GetOption(ProviderCatalog.OptKeepAlive);
        if (!string.IsNullOrWhiteSpace(keepAlive))
        {
            body["keep_alive"] = keepAlive;
        }

        switch (request.Mode)
        {
            case StructuredOutputMode.JsonSchema:
                body["format"] = request.Schema;
                break;
            case StructuredOutputMode.JsonObject:
                body["format"] = "json";
                break;
            case StructuredOutputMode.ToolCall:
                throw new LlmException(LlmErrorKind.UnsupportedResponseFormat, "Для Ollama используйте JSON Schema или JSON-режим.");
        }

        var result = await SendAsync(HttpMethod.Post, UrlBuilder.Combine(profile.BaseUrl, "api/chat"), body, Headers(config), config, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw LlmHttp.Classify(result);
        }

        if (!(result.Json() is JObject root))
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "Ответ Ollama не является JSON.", result.StatusCode, result.RequestId);
        }

        var reason = (string)root["done_reason"];
        return new LlmResponse
        {
            Text = OpenAiChatAdapter.StripReasoning((string)root["message"]?["content"]),
            RawFinishReason = reason,
            FinishReason = reason == "length" ? LlmFinishReason.Length : reason == "stop" ? LlmFinishReason.Stop : LlmFinishReason.Unknown,
            InputTokens = Int(root["prompt_eval_count"]),
            OutputTokens = Int(root["eval_count"]),
            Latency = result.Elapsed,
            ModelReported = (string)root["model"],
            RequestId = result.RequestId,
        };
    }
}
