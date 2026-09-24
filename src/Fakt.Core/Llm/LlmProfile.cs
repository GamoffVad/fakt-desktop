using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Fakt.Core.Llm;

/// <summary>
/// Сохраняемые настройки LLM-профиля. Секреты (API-ключ, секретные заголовки) здесь не хранятся —
/// только в защищённом хранилище по ключам <see cref="SecretKeys"/>.
/// </summary>
public sealed class LlmProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; }

    public string ProviderId { get; set; }

    public string BaseUrl { get; set; }

    /// <summary>Точный идентификатор модели (для Azure — имя развёртывания).</summary>
    public string ModelId { get; set; }

    /// <summary>Модель введена вручную, а не выбрана из полученного от API списка.</summary>
    public bool ManualModelId { get; set; }

    /// <summary>Дополнительные поля конкретного API: api_version, deployment, region, account_id, protocol, пути и т. п.</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<HeaderSetting> ExtraHeaders { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 120;

    public int MaxOutputTokens { get; set; } = 8192;

    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>Ограничение запросов в минуту; 0 — не ограничено на стороне клиента.</summary>
    public int RequestsPerMinute { get; set; }

    /// <summary>Ограничение токенов в минуту (оценка входа + выход); 0 — не ограничено.</summary>
    public int TokensPerMinute { get; set; }

    /// <summary>Записей в одном запросе извлечения.</summary>
    public int BatchRows { get; set; } = 10;

    /// <summary>Предел оценки входных токенов на запрос извлечения.</summary>
    public int MaxInputTokensPerRequest { get; set; } = 6000;

    /// <summary>null — параметр не отправляется. Отправляется только если поддержка подтверждена.</summary>
    public double? Temperature { get; set; }

    public StructuredOutputMode OutputMode { get; set; } = StructuredOutputMode.Auto;

    public CapabilityState Capabilities { get; set; } = new();

    public PricingInfo Pricing { get; set; } = new();

    /// <summary>Хост, для которого сохранён API-ключ. Ключ не отправляется на другой хост.</summary>
    public string KeyBoundHost { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string GetOption(string key, string fallback = null)
    {
        return Options != null && Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
    }

    public LlmProfile Clone()
    {
        var copy = JsonConvert.DeserializeObject<LlmProfile>(JsonConvert.SerializeObject(this));
        copy.Options = new Dictionary<string, string>(copy.Options ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}

public sealed class HeaderSetting
{
    public string Name { get; set; }

    /// <summary>Значение несекретного заголовка. Для секретного — пусто, значение в хранилище секретов.</summary>
    public string Value { get; set; }

    public bool IsSecret { get; set; }
}

/// <summary>Возможности модели, подтверждённые тестовым запросом, а не выведенные из имени провайдера.</summary>
public sealed class CapabilityState
{
    public bool? JsonSchema { get; set; }

    public bool? JsonObject { get; set; }

    public bool? ToolCall { get; set; }

    public bool? Temperature { get; set; }

    public bool? Streaming { get; set; }

    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>Модель, для которой выполнена проверка; при смене модели флаги считаются непроверенными.</summary>
    public string VerifiedModelId { get; set; }

    public string Notes { get; set; }

    public bool IsVerifiedFor(string modelId) =>
        VerifiedAtUtc.HasValue && string.Equals(VerifiedModelId, modelId, StringComparison.Ordinal);
}

/// <summary>Тарифы, заданные пользователем. Без них стоимость не рассчитывается.</summary>
public sealed class PricingInfo
{
    public decimal? InputPerMillionTokens { get; set; }

    public decimal? OutputPerMillionTokens { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>Дата, на которую тариф проверен пользователем.</summary>
    public DateTime? AsOfDate { get; set; }

    public string Source { get; set; }

    public bool IsComplete => InputPerMillionTokens.HasValue && OutputPerMillionTokens.HasValue && AsOfDate.HasValue;
}

public static class SecretKeys
{
    public static string ApiKey(Guid profileId) => $"llm/{profileId:N}/api-key";

    public static string Header(Guid profileId, string headerName) => $"llm/{profileId:N}/header/{headerName.ToLowerInvariant()}";

    public const string SqlPassword = "db/sql-password";
}
