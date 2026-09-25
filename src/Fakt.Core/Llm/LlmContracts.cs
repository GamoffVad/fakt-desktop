using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fakt.Core.Llm;

/// <summary>Адаптер конкретного API. Выполняет одну попытку; повторы, лимиты и параллельность — в прикладном слое.</summary>
public interface ILlmAdapter
{
    LlmProviderDescriptor Descriptor { get; }

    Task<ModelListResult> ListModelsAsync(LlmRuntimeConfig config, CancellationToken cancellationToken);

    Task<LlmResponse> CompleteJsonAsync(LlmRuntimeConfig config, LlmJsonRequest request, CancellationToken cancellationToken);
}

public interface ILlmAdapterRegistry
{
    IReadOnlyList<LlmProviderDescriptor> Providers { get; }

    ILlmAdapter Get(string providerId);
}

/// <summary>Режим структурированного ответа. Auto выбирает лучший проверенный режим для модели.</summary>
public enum StructuredOutputMode
{
    Auto,
    JsonSchema,
    JsonObject,
    ToolCall,
    PromptOnly,
}

public enum LlmFinishReason
{
    Unknown,
    Stop,
    Length,
    ContentFilter,
    Refusal,
    ToolCall,
    Other,
}

public sealed class LlmJsonRequest
{
    public string SystemPrompt { get; set; }

    public string UserContent { get; set; }

    /// <summary>Имя схемы для API, требующих его (OpenAI json_schema.name, имя инструмента Anthropic).</summary>
    public string SchemaName { get; set; }

    /// <summary>JSON Schema ответа без $ref (совместимая со strict-режимами).</summary>
    public JObject Schema { get; set; }

    public int MaxOutputTokens { get; set; }

    /// <summary>Отправляется только если задана и поддерживается моделью.</summary>
    public double? Temperature { get; set; }

    /// <summary>Конкретный режим (не Auto) — решение принимает прикладной слой по проверенным возможностям.</summary>
    public StructuredOutputMode Mode { get; set; } = StructuredOutputMode.JsonSchema;

    /// <summary>Назначение запроса для журнала: structure, extraction, test.</summary>
    public string Purpose { get; set; }
}

public sealed class LlmResponse
{
    public string Text { get; set; }

    public LlmFinishReason FinishReason { get; set; }

    public string RawFinishReason { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public string RequestId { get; set; }

    public TimeSpan Latency { get; set; }

    public string ModelReported { get; set; }

    public bool IsTruncated => FinishReason == LlmFinishReason.Length;
}

public enum LlmErrorKind
{
    Authentication,
    PermissionDenied,
    ModelNotFound,
    RateLimited,
    QuotaExceeded,
    ServerError,
    Overloaded,
    Network,
    Timeout,
    ContextLengthExceeded,
    BadRequest,
    UnsupportedParameter,
    UnsupportedResponseFormat,
    ContentFiltered,
    InvalidResponse,
    NotSupported,
    Configuration,
    Cancelled,
}

/// <summary>Классифицированная ошибка провайдера. Сообщение очищено от секретов и содержимого записей.</summary>
public sealed class LlmException : Exception
{
    public LlmException(LlmErrorKind kind, string message, int? httpStatus = null, string requestId = null,
        TimeSpan? retryAfter = null, string providerCode = null, string parameter = null, Exception inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
        RequestId = requestId;
        RetryAfter = retryAfter;
        ProviderCode = providerCode;
        Parameter = parameter;
    }

    public LlmErrorKind Kind { get; }

    public int? HttpStatus { get; }

    public string RequestId { get; }

    public TimeSpan? RetryAfter { get; }

    public string ProviderCode { get; }

    /// <summary>Имя неподдерживаемого параметра (например, temperature), если провайдер его сообщил.</summary>
    public string Parameter { get; }

    /// <summary>Временная ошибка: повтор с экспоненциальной задержкой допустим.</summary>
    public bool IsTransient =>
        Kind == LlmErrorKind.RateLimited || Kind == LlmErrorKind.ServerError || Kind == LlmErrorKind.Overloaded ||
        Kind == LlmErrorKind.Network || Kind == LlmErrorKind.Timeout;

    /// <summary>Ошибка конфигурации или доступа: задание останавливается с понятной причиной.</summary>
    public bool IsFatalForJob =>
        Kind == LlmErrorKind.Authentication || Kind == LlmErrorKind.PermissionDenied || Kind == LlmErrorKind.ModelNotFound ||
        Kind == LlmErrorKind.Configuration || Kind == LlmErrorKind.QuotaExceeded || Kind == LlmErrorKind.NotSupported;

    public string KindText => LlmErrorText.Describe(Kind);

    /// <summary>Текст для интерфейса: вид ошибки и подробности без повтора, если сообщение уже начинается с вида.</summary>
    public string UserMessage =>
        string.IsNullOrWhiteSpace(Message) ? KindText :
        Message.StartsWith(KindText, StringComparison.OrdinalIgnoreCase) ? Message : KindText + ": " + Message;
}

public static class LlmErrorText
{
    public static string Describe(LlmErrorKind kind)
    {
        switch (kind)
        {
            case LlmErrorKind.Authentication: return "Нет авторизации: ключ API не принят";
            case LlmErrorKind.PermissionDenied: return "Доступ запрещён для этой учётной записи";
            case LlmErrorKind.ModelNotFound: return "Модель не найдена или недоступна";
            case LlmErrorKind.RateLimited: return "Превышен лимит запросов провайдера";
            case LlmErrorKind.QuotaExceeded: return "Исчерпана квота или баланс учётной записи";
            case LlmErrorKind.ServerError: return "Ошибка сервера провайдера";
            case LlmErrorKind.Overloaded: return "Сервис провайдера перегружен";
            case LlmErrorKind.Network: return "Сервис недоступен: сетевая ошибка";
            case LlmErrorKind.Timeout: return "Превышено время ожидания ответа";
            case LlmErrorKind.ContextLengthExceeded: return "Запрос превышает контекст модели";
            case LlmErrorKind.BadRequest: return "Провайдер отклонил запрос";
            case LlmErrorKind.UnsupportedParameter: return "Параметр не поддерживается моделью";
            case LlmErrorKind.UnsupportedResponseFormat: return "Режим структурированного ответа не поддерживается";
            case LlmErrorKind.ContentFiltered: return "Ответ заблокирован фильтром содержимого";
            case LlmErrorKind.InvalidResponse: return "Некорректный ответ модели";
            case LlmErrorKind.NotSupported: return "Операция не поддерживается API";
            case LlmErrorKind.Configuration: return "Ошибка настройки профиля";
            case LlmErrorKind.Cancelled: return "Операция отменена";
            default: return kind.ToString();
        }
    }
}

public sealed class ModelInfo
{
    public string Id { get; set; }

    public string DisplayName { get; set; }

    public string OwnedBy { get; set; }

    public long? ContextLength { get; set; }

    public string Description { get; set; }

    /// <summary>Для локальных серверов: загружена ли модель в память (null — неизвестно).</summary>
    public bool? IsLoaded { get; set; }

    /// <summary>llm, embedding, image и т. п., если API сообщает тип.</summary>
    public string Kind { get; set; }

    public override string ToString() => string.IsNullOrEmpty(DisplayName) || DisplayName == Id ? Id : $"{DisplayName} ({Id})";
}

public enum ModelListStatus
{
    Loaded,
    Empty,
    Unauthorized,
    ServiceUnavailable,
    NotSupported,
    Error,
}

public sealed class ModelListResult
{
    public ModelListStatus Status { get; set; }

    public IReadOnlyList<ModelInfo> Models { get; set; } = Array.Empty<ModelInfo>();

    /// <summary>Пояснение для пользователя: причина пустого списка, отсутствия endpoint и т. п.</summary>
    public string Message { get; set; }

    public int PagesFetched { get; set; }

    public string RequestId { get; set; }

    public static string StatusText(ModelListStatus status)
    {
        switch (status)
        {
            case ModelListStatus.Loaded: return "Список загружен";
            case ModelListStatus.Empty: return "Нет доступных моделей";
            case ModelListStatus.Unauthorized: return "Нет авторизации";
            case ModelListStatus.ServiceUnavailable: return "Сервис недоступен";
            case ModelListStatus.NotSupported: return "Список не поддерживается";
            default: return "Ошибка загрузки списка";
        }
    }
}

/// <summary>Параметры вызова с раскрытыми секретами. Существует только в памяти и никогда не сериализуется.</summary>
public sealed class LlmRuntimeConfig
{
    public LlmRuntimeConfig(LlmProfile profile, string apiKey, IReadOnlyDictionary<string, string> headers)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ApiKey = apiKey;
        Headers = headers ?? new Dictionary<string, string>();
    }

    public LlmProfile Profile { get; }

    public string ApiKey { get; }

    /// <summary>Дополнительные заголовки с уже подставленными секретными значениями.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public override string ToString() => $"{Profile.ProviderId}:{Profile.ModelId}";
}
