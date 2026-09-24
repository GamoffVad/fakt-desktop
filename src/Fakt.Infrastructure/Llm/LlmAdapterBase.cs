using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Llm;

/// <summary>Общие части адаптеров: вызов HTTP, разбор списков моделей, встраивание схемы в промпт.</summary>
public abstract class LlmAdapterBase : ILlmAdapter
{
    protected const int MaxModelPages = 50;

    protected LlmAdapterBase(LlmHttp http, LlmProviderDescriptor descriptor)
    {
        Http = http;
        Descriptor = descriptor;
    }

    public LlmProviderDescriptor Descriptor { get; }

    protected LlmHttp Http { get; }

    public abstract Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken);

    public abstract Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken);

    protected static TimeSpan TimeoutOf(LlmRuntimeConfig config) => TimeSpan.FromSeconds(Math.Max(5, config.Profile.TimeoutSeconds));

    protected static Dictionary<string, string> BaseHeaders(LlmRuntimeConfig config)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in config.Headers)
        {
            headers[header.Key] = header.Value;
        }

        return headers;
    }

    protected async Task<HttpResult> SendAsync(HttpMethod method, string url, JObject body, IReadOnlyDictionary<string, string> headers,
        LlmRuntimeConfig config, CancellationToken cancellationToken)
    {
        var result = await Http.SendAsync(method, url, body, headers, TimeoutOf(config), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // Сервис может повторить присланный ключ в тексте ошибки («Incorrect API key provided: …»). Маскирование
            // по шаблонам знает не все форматы ключей, поэтому ключ профиля вырезается из тела ошибки явно.
            result.Body = RedactKey(result.Body, config.ApiKey);
        }

        return result;
    }

    /// <summary>Замена ключа API в тексте на «***» (ключи короче 6 символов не заменяются, как и в <see cref="Fakt.Core.Logging.LogSanitizer"/>).</summary>
    internal static string RedactKey(string text, string apiKey)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(apiKey) || apiKey.Length < 6)
        {
            return text;
        }

        return text.Replace(apiKey, "***");
    }

    /// <summary>Преобразование ошибки списка моделей в состояние интерфейса.</summary>
    protected static ModelListResult ListFailure(HttpResult result)
    {
        var error = LlmHttp.Classify(result, "Список моделей");
        var status = error.Kind switch
        {
            LlmErrorKind.Authentication or LlmErrorKind.PermissionDenied => ModelListStatus.Unauthorized,
            LlmErrorKind.NotSupported => ModelListStatus.NotSupported,
            LlmErrorKind.ServerError or LlmErrorKind.Overloaded or LlmErrorKind.Network or LlmErrorKind.Timeout => ModelListStatus.ServiceUnavailable,
            _ => ModelListStatus.Error,
        };
        if (result.StatusCode == 404 || result.StatusCode == 405)
        {
            status = ModelListStatus.NotSupported;
        }

        return new ModelListResult
        {
            Status = status,
            Message = status == ModelListStatus.NotSupported
                ? "Сервис не предоставляет список моделей по этому адресу. Введите точный model ID вручную."
                : error.Message,
            RequestId = result.RequestId,
        };
    }

    protected static ModelListResult ListFailure(LlmException exception)
    {
        var status = exception.Kind switch
        {
            LlmErrorKind.Authentication or LlmErrorKind.PermissionDenied => ModelListStatus.Unauthorized,
            LlmErrorKind.Network or LlmErrorKind.Timeout or LlmErrorKind.ServerError or LlmErrorKind.Overloaded => ModelListStatus.ServiceUnavailable,
            LlmErrorKind.NotSupported => ModelListStatus.NotSupported,
            _ => ModelListStatus.Error,
        };
        return new ModelListResult { Status = status, Message = exception.Message, RequestId = exception.RequestId };
    }

    protected static ModelListResult ListSuccess(List<ModelInfo> models, int pages, string requestId, string note = null)
    {
        var distinct = models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModelListResult
        {
            Status = distinct.Count == 0 ? ModelListStatus.Empty : ModelListStatus.Loaded,
            Models = distinct,
            PagesFetched = pages,
            RequestId = requestId,
            Message = distinct.Count == 0 ? "API вернул пустой список: учётной записи не доступна ни одна модель." : note,
        };
    }

    /// <summary>Разбор элементов списка моделей разных форматов (data[], models[], голый массив).</summary>
    protected static IEnumerable<JObject> ModelItems(JToken root)
    {
        switch (root)
        {
            case JArray array:
                return array.OfType<JObject>();
            case JObject obj when obj["data"] is JArray data:
                return data.OfType<JObject>();
            case JObject obj when obj["models"] is JArray models:
                return models.OfType<JObject>();
            default:
                return Enumerable.Empty<JObject>();
        }
    }

    protected static ModelInfo ToModelInfo(JObject item)
    {
        string S(params string[] names) => names.Select(n => item[n]).Where(t => t != null && t.Type == JTokenType.String).Select(t => (string)t).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        long? L(params string[] names)
        {
            foreach (var name in names)
            {
                var token = item[name];
                if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
                {
                    return (long)token;
                }
            }

            return null;
        }

        bool? loaded = null;
        var state = S("state");
        if (state != null)
        {
            loaded = string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase);
        }
        else if (item["loaded_instances"] is JArray instances)
        {
            loaded = instances.Count > 0;
        }

        return new ModelInfo
        {
            Id = S("id", "key", "model", "name"),
            DisplayName = S("display_name", "displayName", "name"),
            OwnedBy = S("owned_by", "publisher", "organization"),
            ContextLength = L("context_length", "contextLength", "max_context_length", "context_window", "max_input_tokens", "inputTokenLimit"),
            Description = S("description"),
            IsLoaded = loaded,
            Kind = S("type"),
        };
    }

    /// <summary>
    /// Для режимов без серверной схемы (json_object, только промпт) схема добавляется в текст запроса;
    /// соответствие ответа всё равно проверяется приложением.
    /// </summary>
    protected static string UserContentFor(LlmJsonRequest request)
    {
        if (request.Mode == StructuredOutputMode.JsonSchema || request.Mode == StructuredOutputMode.ToolCall || request.Schema == null)
        {
            return request.UserContent;
        }

        return request.UserContent + "\n\nRESPONSE FORMAT: return one JSON object (JSON only, no markdown) that is valid against this JSON Schema:\n" +
               request.Schema.ToString(Formatting.None);
    }

    protected static LlmFinishReason MapOpenAiFinish(string reason)
    {
        switch (reason?.ToLowerInvariant())
        {
            case null:
                return LlmFinishReason.Unknown;
            case "stop":
            case "eos":
            case "end_turn":
                return LlmFinishReason.Stop;
            case "length":
            case "model_length":
            case "max_tokens":
                return LlmFinishReason.Length;
            case "content_filter":
                return LlmFinishReason.ContentFilter;
            case "tool_calls":
            case "function_call":
                return LlmFinishReason.ToolCall;
            default:
                return LlmFinishReason.Other;
        }
    }

    protected static int? Int(JToken token)
    {
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
        {
            return null;
        }

        return (int)token;
    }

    /// <summary>Модели, не предназначенные для генерации текста (эмбеддинги, изображения), в список не включаются.</summary>
    protected static bool IsTextModel(ModelInfo model)
    {
        var kind = model.Kind?.ToLowerInvariant();
        if (kind != null && (kind.Contains("embed") || kind == "image" || kind == "audio" || kind == "rerank" || kind == "moderation"))
        {
            return false;
        }

        var id = model.Id?.ToLowerInvariant() ?? string.Empty;
        return !(id.Contains("embed") || id.StartsWith("whisper", StringComparison.Ordinal) || id.StartsWith("tts", StringComparison.Ordinal) ||
                 id.StartsWith("dall-e", StringComparison.Ordinal) || id.Contains("moderation") || id.Contains("rerank"));
    }
}
