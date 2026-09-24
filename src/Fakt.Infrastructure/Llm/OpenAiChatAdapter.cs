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
/// Протокол Chat Completions и его диалекты: OpenAI, OpenAI Compatible, Azure OpenAI (v1 и classic),
/// Mistral, DeepSeek, Qwen, xAI, Groq, OpenRouter, Together AI, Fireworks AI, LM Studio.
/// Диалекты различаются адресами, авторизацией, именем параметра предела ответа, ролью системной
/// инструкции и форматом списка моделей. Совместимость не предполагается по имени провайдера:
/// режим структурированного ответа подтверждается тестовым запросом.
/// </summary>
public sealed class OpenAiChatAdapter : LlmAdapterBase
{
    public OpenAiChatAdapter(LlmHttp http, LlmProviderDescriptor descriptor) : base(http, descriptor)
    {
    }

    private enum ListKind
    {
        OpenAi,
        OpenRouter,
        Fireworks,
        LmStudio,
        Azure,
    }

    private sealed class Dialect
    {
        public string ChatUrl { get; set; }
        public string ModelsUrl { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string MaxTokensParam { get; set; } = "max_tokens";
        public string SystemRole { get; set; } = "system";
        public bool SendModel { get; set; } = true;
        public bool Strict { get; set; } = true;
        public ListKind List { get; set; } = ListKind.OpenAi;
        public Action<JObject> Customize { get; set; }
    }

    private Dialect Resolve(LlmRuntimeConfig config)
    {
        var profile = config.Profile;
        var baseUrl = profile.BaseUrl?.Trim();
        if (!UrlBuilder.IsValidHttpUrl(baseUrl, out var urlError))
        {
            throw new LlmException(LlmErrorKind.Configuration, "Base URL: " + urlError);
        }

        var headers = BaseHeaders(config);
        var dialect = new Dialect { Headers = headers };
        void Bearer()
        {
            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                headers["Authorization"] = "Bearer " + config.ApiKey;
            }
        }

        switch (Descriptor.Id)
        {
            case ProviderCatalog.OpenAi:
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "models");
                dialect.MaxTokensParam = "max_completion_tokens";
                dialect.SystemRole = "developer";
                AddIfSet(headers, "OpenAI-Organization", profile.GetOption(ProviderCatalog.OptOrganization));
                AddIfSet(headers, "OpenAI-Project", profile.GetOption(ProviderCatalog.OptProject));
                break;

            case ProviderCatalog.AzureOpenAi:
            {
                if (baseUrl.Contains("<resource>"))
                {
                    throw new LlmException(LlmErrorKind.Configuration, "Укажите адрес ресурса Azure OpenAI вместо шаблона <resource>.");
                }

                if (string.Equals(profile.GetOption(ProviderCatalog.OptAuthStyle, "api-key"), "bearer", StringComparison.OrdinalIgnoreCase))
                {
                    Bearer();
                }
                else if (!string.IsNullOrEmpty(config.ApiKey))
                {
                    headers["api-key"] = config.ApiKey;
                }

                dialect.MaxTokensParam = "max_completion_tokens";
                dialect.List = ListKind.Azure;
                if (string.Equals(profile.GetOption(ProviderCatalog.OptApiMode, "v1"), "classic", StringComparison.OrdinalIgnoreCase))
                {
                    var version = profile.GetOption(ProviderCatalog.OptApiVersion, "2024-10-21");
                    var query = new[] { new KeyValuePair<string, string>("api-version", version) };
                    if (string.IsNullOrWhiteSpace(profile.ModelId))
                    {
                        throw new LlmException(LlmErrorKind.Configuration, "Для Azure (classic) укажите имя развёртывания (deployment).");
                    }

                    dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "openai/deployments/" + Uri.EscapeDataString(profile.ModelId) + "/chat/completions", query);
                    dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "openai/models", query);
                    dialect.SendModel = false;
                }
                else
                {
                    dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "openai/v1/chat/completions");
                    dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "openai/v1/models");
                }

                break;
            }

            case ProviderCatalog.OpenAiCompatible:
            {
                var auth = profile.GetOption(ProviderCatalog.OptAuthStyle, "bearer").ToLowerInvariant();
                if (auth == "bearer")
                {
                    Bearer();
                }
                else if (auth == "header" && !string.IsNullOrEmpty(config.ApiKey))
                {
                    headers[profile.GetOption(ProviderCatalog.OptAuthHeader, "api-key")] = config.ApiKey;
                }

                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, profile.GetOption(ProviderCatalog.OptChatPath, "chat/completions"));
                var modelsPath = profile.Options != null && profile.Options.TryGetValue(ProviderCatalog.OptModelsPath, out var path) ? path : "models";
                dialect.ModelsUrl = string.IsNullOrWhiteSpace(modelsPath) ? null : UrlBuilder.Combine(baseUrl, modelsPath);
                dialect.MaxTokensParam = profile.GetOption(ProviderCatalog.OptMaxTokensParam, "max_tokens");
                dialect.SystemRole = profile.GetOption(ProviderCatalog.OptSystemRole, "system");
                dialect.Strict = !string.Equals(profile.GetOption(ProviderCatalog.OptStrict, "true"), "false", StringComparison.OrdinalIgnoreCase);
                break;
            }

            case ProviderCatalog.Qwen:
            {
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "models");
                var thinking = profile.GetOption(ProviderCatalog.OptEnableThinking, "false");
                if (thinking == "false" || thinking == "true")
                {
                    dialect.Customize = body => body["enable_thinking"] = thinking == "true";
                }

                break;
            }

            case ProviderCatalog.XAi:
            case ProviderCatalog.Groq:
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "models");
                dialect.MaxTokensParam = "max_completion_tokens";
                break;

            case ProviderCatalog.OpenRouter:
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, string.IsNullOrEmpty(config.ApiKey) ? "models" : "models/user");
                dialect.List = ListKind.OpenRouter;
                break;

            case ProviderCatalog.Fireworks:
            {
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
                var account = profile.GetOption(ProviderCatalog.OptAccountId, "fireworks");
                dialect.ModelsUrl = UrlBuilder.Combine(origin, "v1/accounts/" + Uri.EscapeDataString(account) + "/models");
                dialect.List = ListKind.Fireworks;
                dialect.Strict = false;
                break;
            }

            case ProviderCatalog.LmStudio:
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "models");
                dialect.List = ListKind.LmStudio;
                break;

            default:
                // Mistral, DeepSeek, Together и другие OpenAI-подобные API с max_tokens и ролью system.
                Bearer();
                dialect.ChatUrl = UrlBuilder.Combine(baseUrl, "chat/completions");
                dialect.ModelsUrl = UrlBuilder.Combine(baseUrl, "models");
                break;
        }

        return dialect;
    }

    private static void AddIfSet(Dictionary<string, string> headers, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers[name] = value;
        }
    }

    public override async Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken)
    {
        Dialect dialect;
        try
        {
            dialect = Resolve(config);
        }
        catch (LlmException ex)
        {
            return new ModelListResult { Status = ModelListStatus.Error, Message = ex.Message };
        }

        if (dialect.ModelsUrl == null)
        {
            return new ModelListResult
            {
                Status = ModelListStatus.NotSupported,
                Message = "Путь списка моделей не задан: сервис не предоставляет список. Введите точный model ID вручную.",
            };
        }

        try
        {
            switch (dialect.List)
            {
                case ListKind.LmStudio:
                    return await ListLmStudioAsync(config, dialect, cancellationToken).ConfigureAwait(false);
                case ListKind.Fireworks:
                    return await ListTokenPagedAsync(config, dialect, "pageSize", "200", "pageToken", "nextPageToken", cancellationToken).ConfigureAwait(false);
                case ListKind.OpenRouter:
                    return await ListOpenRouterAsync(config, dialect, cancellationToken).ConfigureAwait(false);
                default:
                    var result = await ListOpenAiStyleAsync(config, dialect.ModelsUrl, dialect.Headers, cancellationToken).ConfigureAwait(false);
                    if (dialect.List == ListKind.Azure && result.Status == ModelListStatus.Loaded)
                    {
                        result.Message = Descriptor.ModelListingNote;
                    }

                    return result;
            }
        }
        catch (LlmException ex)
        {
            return ListFailure(ex);
        }
    }

    private async Task<ModelListResult> ListOpenAiStyleAsync(LlmRuntimeConfig config, string url, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var models = new List<ModelInfo>();
        var pages = 0;
        string requestId = null;
        string after = null;
        do
        {
            var pageUrl = after == null ? url : UrlBuilder.Combine(url, string.Empty, new[] { new KeyValuePair<string, string>("after", after) });
            var result = await SendAsync(HttpMethod.Get, pageUrl, null, headers, config, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return ListFailure(result);
            }

            requestId ??= result.RequestId;
            pages++;
            var root = result.Json();
            models.AddRange(ModelItems(root).Select(ToModelInfo).Where(IsTextModel));
            after = root is JObject obj && (bool?)obj["has_more"] == true ? (string)obj["last_id"] : null;
        }
        while (after != null && pages < MaxModelPages);

        return ListSuccess(models, pages, requestId);
    }

    private async Task<ModelListResult> ListTokenPagedAsync(LlmRuntimeConfig config, Dialect dialect, string sizeParam, string size, string tokenParam,
        string nextField, CancellationToken cancellationToken)
    {
        var models = new List<ModelInfo>();
        var pages = 0;
        string requestId = null;
        string token = null;
        do
        {
            var query = new List<KeyValuePair<string, string>> { new(sizeParam, size) };
            if (token != null)
            {
                query.Add(new KeyValuePair<string, string>(tokenParam, token));
            }

            var result = await SendAsync(HttpMethod.Get, UrlBuilder.Combine(dialect.ModelsUrl, string.Empty, query), null, dialect.Headers, config, cancellationToken).ConfigureAwait(false);
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
                info.Id = (string)item["name"] ?? info.Id;
                info.DisplayName = (string)item["displayName"] ?? info.DisplayName;
                if (IsTextModel(info))
                {
                    models.Add(info);
                }
            }

            token = (string)root?[nextField];
        }
        while (!string.IsNullOrEmpty(token) && pages < MaxModelPages);

        return ListSuccess(models, pages, requestId);
    }

    private async Task<ModelListResult> ListOpenRouterAsync(LlmRuntimeConfig config, Dialect dialect, CancellationToken cancellationToken)
    {
        var first = await SendAsync(HttpMethod.Get, dialect.ModelsUrl, null, dialect.Headers, config, cancellationToken).ConfigureAwait(false);
        if (!first.IsSuccess && dialect.ModelsUrl.EndsWith("/models/user", StringComparison.Ordinal) && (first.StatusCode == 404 || first.StatusCode == 405))
        {
            dialect.ModelsUrl = dialect.ModelsUrl.Substring(0, dialect.ModelsUrl.Length - "/user".Length);
            first = await SendAsync(HttpMethod.Get, dialect.ModelsUrl, null, dialect.Headers, config, cancellationToken).ConfigureAwait(false);
        }

        if (!first.IsSuccess)
        {
            return ListFailure(first);
        }

        var models = ModelItems(first.Json()).Select(ToModelInfo).Where(IsTextModel).ToList();
        var pages = 1;
        var next = (first.Json() as JObject)?["links"]?["next"]?.ToString();
        while (!string.IsNullOrEmpty(next) && pages < MaxModelPages)
        {
            var url = next.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? next : UrlBuilder.Combine(new Uri(dialect.ModelsUrl).GetLeftPart(UriPartial.Authority), next);
            var page = await SendAsync(HttpMethod.Get, url, null, dialect.Headers, config, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                break;
            }

            pages++;
            models.AddRange(ModelItems(page.Json()).Select(ToModelInfo).Where(IsTextModel));
            next = (page.Json() as JObject)?["links"]?["next"]?.ToString();
        }

        return ListSuccess(models, pages, first.RequestId);
    }

    /// <summary>LM Studio: собственный REST API (/api/v1/models, затем /api/v0/models) показывает и незагруженные модели; иначе /v1/models.</summary>
    private async Task<ModelListResult> ListLmStudioAsync(LlmRuntimeConfig config, Dialect dialect, CancellationToken cancellationToken)
    {
        var origin = new Uri(config.Profile.BaseUrl.Trim()).GetLeftPart(UriPartial.Authority);
        foreach (var nativePath in new[] { "api/v1/models", "api/v0/models" })
        {
            var native = await SendAsync(HttpMethod.Get, UrlBuilder.Combine(origin, nativePath), null, dialect.Headers, config, cancellationToken).ConfigureAwait(false);
            if (!native.IsSuccess)
            {
                if (native.StatusCode == 401 || native.StatusCode == 403)
                {
                    return ListFailure(native);
                }

                continue;
            }

            var models = ModelItems(native.Json()).Select(ToModelInfo).Where(IsTextModel).ToList();
            if (models.Count > 0)
            {
                return ListSuccess(models, 1, native.RequestId,
                    "Список получен из LM Studio. Незагруженная модель загрузится при первом запросе (JIT); это может занять время.");
            }
        }

        return await ListOpenAiStyleAsync(config, dialect.ModelsUrl, dialect.Headers, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken)
    {
        var dialect = Resolve(config);
        var profile = config.Profile;
        if (dialect.SendModel && string.IsNullOrWhiteSpace(profile.ModelId))
        {
            throw new LlmException(LlmErrorKind.Configuration, "Не выбрана модель.");
        }

        var body = new JObject();
        if (dialect.SendModel)
        {
            body["model"] = profile.ModelId;
        }

        body["messages"] = new JArray
        {
            new JObject { ["role"] = dialect.SystemRole, ["content"] = request.SystemPrompt },
            new JObject { ["role"] = "user", ["content"] = UserContentFor(request) },
        };
        body[dialect.MaxTokensParam] = request.MaxOutputTokens;
        if (request.Temperature.HasValue)
        {
            body["temperature"] = request.Temperature.Value;
        }

        body["stream"] = false;
        switch (request.Mode)
        {
            case StructuredOutputMode.JsonSchema:
            {
                var jsonSchema = new JObject { ["name"] = request.SchemaName, ["schema"] = request.Schema };
                if (dialect.Strict)
                {
                    jsonSchema["strict"] = true;
                }

                body["response_format"] = new JObject { ["type"] = "json_schema", ["json_schema"] = jsonSchema };
                break;
            }

            case StructuredOutputMode.JsonObject:
                body["response_format"] = new JObject { ["type"] = "json_object" };
                break;

            case StructuredOutputMode.ToolCall:
            {
                var function = new JObject
                {
                    ["name"] = request.SchemaName,
                    ["description"] = "Return the result as the arguments of this function.",
                    ["parameters"] = request.Schema,
                };
                if (dialect.Strict)
                {
                    function["strict"] = true;
                }

                body["tools"] = new JArray { new JObject { ["type"] = "function", ["function"] = function } };
                body["tool_choice"] = new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = request.SchemaName } };
                break;
            }
        }

        dialect.Customize?.Invoke(body);
        var result = await SendAsync(HttpMethod.Post, dialect.ChatUrl, body, dialect.Headers, config, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw LlmHttp.Classify(result);
        }

        return Parse(result, request.Mode);
    }

    private static LlmResponse Parse(HttpResult result, StructuredOutputMode mode)
    {
        if (!(result.Json() is JObject root))
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "Ответ провайдера не является JSON.", result.StatusCode, result.RequestId);
        }

        if (root["error"] != null && root["choices"] == null)
        {
            // OpenRouter может вернуть HTTP 200 с ошибкой в теле.
            var status = root["error"]?["code"]?.Type == JTokenType.Integer ? (int)root["error"]["code"] : 502;
            throw LlmHttp.Classify(new HttpResult { StatusCode = status, Body = result.Body, Headers = result.Headers, RequestId = result.RequestId });
        }

        var choice = (root["choices"] as JArray)?.FirstOrDefault() as JObject;
        if (choice == null)
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "В ответе нет choices.", result.StatusCode, result.RequestId);
        }

        var message = choice["message"] as JObject;
        var response = new LlmResponse
        {
            RawFinishReason = (string)choice["finish_reason"],
            RequestId = result.RequestId ?? (string)root["id"],
            Latency = result.Elapsed,
            ModelReported = (string)root["model"],
            InputTokens = Int(root["usage"]?["prompt_tokens"]),
            OutputTokens = Int(root["usage"]?["completion_tokens"]),
        };
        response.FinishReason = MapOpenAiFinish(response.RawFinishReason);

        var refusal = (string)message?["refusal"];
        if (!string.IsNullOrWhiteSpace(refusal))
        {
            response.FinishReason = LlmFinishReason.Refusal;
            response.Text = null;
            return response;
        }

        if (mode == StructuredOutputMode.ToolCall && message?["tool_calls"] is JArray calls && calls.Count > 0)
        {
            response.Text = (string)calls[0]?["function"]?["arguments"];
            if (response.FinishReason == LlmFinishReason.ToolCall)
            {
                response.FinishReason = LlmFinishReason.Stop;
            }

            return response;
        }

        response.Text = StripReasoning(ContentText(message?["content"]));
        return response;
    }

    /// <summary>content бывает строкой или массивом фрагментов (Mistral: text/thinking).</summary>
    private static string ContentText(JToken content)
    {
        if (content == null || content.Type == JTokenType.Null)
        {
            return null;
        }

        if (content.Type == JTokenType.String)
        {
            return (string)content;
        }

        if (content is JArray parts)
        {
            var builder = new StringBuilder();
            foreach (var part in parts.OfType<JObject>())
            {
                var type = (string)part["type"];
                if (type == null || type == "text" || type == "output_text")
                {
                    builder.Append((string)part["text"]);
                }
            }

            return builder.ToString();
        }

        return content.ToString(Formatting.None);
    }

    /// <summary>Модели с рассуждениями иногда возвращают «&lt;think&gt;…&lt;/think&gt;» перед JSON.</summary>
    internal static string StripReasoning(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var end = trimmed.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            return end >= 0 ? trimmed.Substring(end + "</think>".Length).Trim() : string.Empty;
        }

        return text;
    }
}
