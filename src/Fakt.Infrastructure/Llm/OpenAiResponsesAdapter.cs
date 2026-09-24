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
/// Протокол Responses (OpenAI и OpenAI Compatible с protocol=responses): instructions + input,
/// max_output_tokens, text.format; store=false — запросы не сохраняются на стороне провайдера.
/// Текст собирается из output[].content[] с type=output_text (поле output_text есть только в SDK).
/// </summary>
public sealed class OpenAiResponsesAdapter : LlmAdapterBase
{
    private readonly OpenAiChatAdapter _chat;

    public OpenAiResponsesAdapter(LlmHttp http, LlmProviderDescriptor descriptor, OpenAiChatAdapter chat) : base(http, descriptor)
    {
        _chat = chat;
    }

    public override Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken) =>
        _chat.ListModelsAsync(config, cancellationToken);

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

        var headers = BaseHeaders(config);
        var path = "responses";
        if (Descriptor.Id == ProviderCatalog.OpenAiCompatible)
        {
            path = profile.GetOption(ProviderCatalog.OptResponsesPath, "responses");
            var auth = profile.GetOption(ProviderCatalog.OptAuthStyle, "bearer").ToLowerInvariant();
            if (auth == "header" && !string.IsNullOrEmpty(config.ApiKey))
            {
                headers[profile.GetOption(ProviderCatalog.OptAuthHeader, "api-key")] = config.ApiKey;
            }
            else if (auth == "bearer" && !string.IsNullOrEmpty(config.ApiKey))
            {
                headers["Authorization"] = "Bearer " + config.ApiKey;
            }
        }
        else if (!string.IsNullOrEmpty(config.ApiKey))
        {
            headers["Authorization"] = "Bearer " + config.ApiKey;
            if (!string.IsNullOrWhiteSpace(profile.GetOption(ProviderCatalog.OptOrganization)))
            {
                headers["OpenAI-Organization"] = profile.GetOption(ProviderCatalog.OptOrganization);
            }

            if (!string.IsNullOrWhiteSpace(profile.GetOption(ProviderCatalog.OptProject)))
            {
                headers["OpenAI-Project"] = profile.GetOption(ProviderCatalog.OptProject);
            }
        }

        var body = new JObject
        {
            ["model"] = profile.ModelId,
            ["instructions"] = request.SystemPrompt,
            ["input"] = UserContentFor(request),
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["store"] = false,
        };
        if (request.Temperature.HasValue)
        {
            body["temperature"] = request.Temperature.Value;
        }

        switch (request.Mode)
        {
            case StructuredOutputMode.JsonSchema:
                body["text"] = new JObject
                {
                    ["format"] = new JObject { ["type"] = "json_schema", ["name"] = request.SchemaName, ["schema"] = request.Schema, ["strict"] = true },
                };
                break;
            case StructuredOutputMode.JsonObject:
                body["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_object" } };
                break;
            case StructuredOutputMode.ToolCall:
                body["tools"] = new JArray
                {
                    new JObject { ["type"] = "function", ["name"] = request.SchemaName, ["parameters"] = request.Schema, ["strict"] = true },
                };
                body["tool_choice"] = new JObject { ["type"] = "function", ["name"] = request.SchemaName };
                break;
        }

        var result = await SendAsync(HttpMethod.Post, UrlBuilder.Combine(profile.BaseUrl, path), body, headers, config, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw LlmHttp.Classify(result);
        }

        if (!(result.Json() is JObject root))
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "Ответ провайдера не является JSON.", result.StatusCode, result.RequestId);
        }

        // usage, incomplete_details и error в ответе бывают JSON null: обращение к полю JValue бросает исключение.
        var usage = root["usage"] as JObject;
        var response = new LlmResponse
        {
            RequestId = result.RequestId ?? (string)root["id"],
            Latency = result.Elapsed,
            ModelReported = (string)root["model"],
            InputTokens = Int(usage?["input_tokens"]),
            OutputTokens = Int(usage?["output_tokens"]),
        };

        var status = (string)root["status"];
        var incompleteReason = (string)(root["incomplete_details"] as JObject)?["reason"];
        response.RawFinishReason = incompleteReason ?? status;
        if (status == "failed")
        {
            throw new LlmException(LlmErrorKind.ServerError, "Провайдер завершил ответ с ошибкой: " + (string)(root["error"] as JObject)?["message"], result.StatusCode, response.RequestId);
        }

        response.FinishReason = status == "incomplete"
            ? incompleteReason == "content_filter" ? LlmFinishReason.ContentFilter : LlmFinishReason.Length
            : LlmFinishReason.Stop;

        var text = new StringBuilder();
        foreach (var item in (root["output"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var type = (string)item["type"];
            if (type == "function_call" && request.Mode == StructuredOutputMode.ToolCall)
            {
                response.Text = (string)item["arguments"];
                return response;
            }

            if (type != "message")
            {
                continue;
            }

            foreach (var part in (item["content"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var partType = (string)part["type"];
                if (partType == "output_text")
                {
                    text.Append((string)part["text"]);
                }
                else if (partType == "refusal")
                {
                    response.FinishReason = LlmFinishReason.Refusal;
                    response.Text = null;
                    return response;
                }
            }
        }

        response.Text = text.ToString();
        return response;
    }
}

/// <summary>Выбор протокола профиля (chat / responses) для OpenAI и OpenAI Compatible.</summary>
public sealed class OpenAiProtocolRouter : ILlmAdapter
{
    private readonly OpenAiChatAdapter _chat;
    private readonly OpenAiResponsesAdapter _responses;

    public OpenAiProtocolRouter(LlmHttp http, LlmProviderDescriptor descriptor)
    {
        _chat = new OpenAiChatAdapter(http, descriptor);
        _responses = new OpenAiResponsesAdapter(http, descriptor, _chat);
        Descriptor = descriptor;
    }

    public LlmProviderDescriptor Descriptor { get; }

    public Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken) =>
        _chat.ListModelsAsync(config, cancellationToken);

    public Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken) =>
        string.Equals(config.Profile.GetOption(ProviderCatalog.OptProtocol, "chat"), "responses", StringComparison.OrdinalIgnoreCase)
            ? _responses.CompleteJsonAsync(config, request, cancellationToken)
            : _chat.CompleteJsonAsync(config, request, cancellationToken);
}
