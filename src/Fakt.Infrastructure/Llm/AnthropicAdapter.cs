using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Llm;

/// <summary>
/// Anthropic Messages API: POST /v1/messages, anthropic-version, system — отдельное поле, max_tokens обязателен.
/// Структурированный ответ — output_config.format (json_schema, GA) либо принудительный вызов инструмента
/// (недоступен на части новых моделей — определяется тестом). Список моделей — курсорная пагинация after_id.
/// </summary>
public sealed class AnthropicAdapter : LlmAdapterBase
{
    public AnthropicAdapter(LlmHttp http, LlmProviderDescriptor descriptor) : base(http, descriptor)
    {
    }

    private Dictionary<string, string> Headers(LlmRuntimeConfig config)
    {
        var profile = config.Profile;
        var headers = BaseHeaders(config);
        headers["anthropic-version"] = profile.GetOption(ProviderCatalog.OptAnthropicVersion, "2023-06-01");
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            if (string.Equals(profile.GetOption(ProviderCatalog.OptAuthStyle, "x-api-key"), "bearer", StringComparison.OrdinalIgnoreCase))
            {
                headers["Authorization"] = "Bearer " + config.ApiKey;
            }
            else
            {
                headers["x-api-key"] = config.ApiKey;
            }
        }

        var workspace = profile.GetOption(ProviderCatalog.OptWorkspaceId);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            headers["anthropic-workspace-id"] = workspace;
        }

        return headers;
    }

    public override async Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var headers = Headers(config);
            var models = new List<ModelInfo>();
            string afterId = null;
            string requestId = null;
            var pages = 0;
            do
            {
                var query = new List<KeyValuePair<string, string>> { new("limit", "1000") };
                if (afterId != null)
                {
                    query.Add(new KeyValuePair<string, string>("after_id", afterId));
                }

                var result = await SendAsync(HttpMethod.Get, UrlBuilder.Combine(config.Profile.BaseUrl, "v1/models", query), null, headers, config, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return ListFailure(result);
                }

                requestId ??= result.RequestId;
                pages++;
                var root = result.Json() as JObject;
                foreach (var item in ModelItems(root))
                {
                    var info = ToModelInfo(item);
                    info.ContextLength ??= (long?)item["max_input_tokens"];
                    models.Add(info);
                }

                afterId = (bool?)root?["has_more"] == true ? (string)root["last_id"] : null;
            }
            while (afterId != null && pages < MaxModelPages);

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

        if (string.IsNullOrWhiteSpace(profile.ModelId))
        {
            throw new LlmException(LlmErrorKind.Configuration, "Не выбрана модель.");
        }

        var body = new JObject
        {
            ["model"] = profile.ModelId,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = request.SystemPrompt,
            ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = UserContentFor(request) } },
        };
        if (request.Temperature.HasValue)
        {
            body["temperature"] = request.Temperature.Value;
        }

        switch (request.Mode)
        {
            case StructuredOutputMode.JsonSchema:
                body["output_config"] = new JObject
                {
                    ["format"] = new JObject { ["type"] = "json_schema", ["schema"] = request.Schema },
                };
                break;
            case StructuredOutputMode.ToolCall:
                body["tools"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = request.SchemaName,
                        ["description"] = "Return the result as the input of this tool.",
                        ["input_schema"] = request.Schema,
                        ["strict"] = true,
                    },
                };
                body["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = request.SchemaName };
                break;
            case StructuredOutputMode.JsonObject:
                throw new LlmException(LlmErrorKind.UnsupportedResponseFormat, "Режим json_object в Anthropic Messages API отсутствует; используйте JSON Schema или схему в промпте.");
        }

        var result = await SendAsync(HttpMethod.Post, UrlBuilder.Combine(profile.BaseUrl, "v1/messages"), body, Headers(config), config, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw LlmHttp.Classify(result);
        }

        if (!(result.Json() is JObject root))
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "Ответ провайдера не является JSON.", result.StatusCode, result.RequestId);
        }

        var usage = root["usage"] as JObject;
        var input = Int(usage?["input_tokens"]);
        if (input.HasValue)
        {
            input += Int(usage["cache_creation_input_tokens"]) ?? 0;
            input += Int(usage["cache_read_input_tokens"]) ?? 0;
        }

        var stop = (string)root["stop_reason"];
        var response = new LlmResponse
        {
            RawFinishReason = stop,
            RequestId = result.RequestId ?? (string)root["id"],
            Latency = result.Elapsed,
            ModelReported = (string)root["model"],
            InputTokens = input,
            OutputTokens = Int(usage?["output_tokens"]),
            FinishReason = stop switch
            {
                "end_turn" or "stop_sequence" or "tool_use" => LlmFinishReason.Stop,
                "max_tokens" or "model_context_window_exceeded" => LlmFinishReason.Length,
                "refusal" => LlmFinishReason.Refusal,
                null => LlmFinishReason.Unknown,
                _ => LlmFinishReason.Other,
            },
        };

        var text = new StringBuilder();
        foreach (var block in (root["content"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var type = (string)block["type"];
            if (type == "tool_use" && request.Mode == StructuredOutputMode.ToolCall)
            {
                response.Text = block["input"]?.ToString(Formatting.None);
                return response;
            }

            if (type == "text")
            {
                text.Append((string)block["text"]);
            }
        }

        response.Text = text.ToString();
        return response;
    }
}
