using System;
using System.Collections.Generic;

namespace Fakt.Core.Llm;

public enum ProviderFieldKind
{
    Text,
    Url,
    Choice,
    Number,
    Bool,
}

/// <summary>Дополнительное поле настроек конкретного API (регион, версия API, deployment и т. п.).</summary>
public sealed class ProviderField
{
    public ProviderField(string key, string label, ProviderFieldKind kind, bool required = false, string defaultValue = null,
        IReadOnlyList<string> choices = null, string help = null)
    {
        Key = key;
        Label = label;
        Kind = kind;
        Required = required;
        DefaultValue = defaultValue;
        Choices = choices ?? Array.Empty<string>();
        Help = help;
    }

    public string Key { get; }

    public string Label { get; }

    public ProviderFieldKind Kind { get; }

    public bool Required { get; }

    public string DefaultValue { get; }

    public IReadOnlyList<string> Choices { get; }

    public string Help { get; }
}

public enum ApiKeyRequirement
{
    Required,
    Optional,
    NotUsed,
}

/// <summary>Публичные значения по умолчанию и набор полей провайдера. Не является каталогом всех провайдеров.</summary>
public sealed class LlmProviderDescriptor
{
    public string Id { get; set; }

    public string DisplayName { get; set; }

    public string DefaultBaseUrl { get; set; }

    public ApiKeyRequirement ApiKey { get; set; } = ApiKeyRequirement.Required;

    /// <summary>Сервис обычно работает в локальной или внутренней сети (Ollama, LM Studio).</summary>
    public bool IsLocalByDefault { get; set; }

    public IReadOnlyList<ProviderField> Fields { get; set; } = Array.Empty<ProviderField>();

    public bool SupportsModelListing { get; set; } = true;

    /// <summary>Пояснение к списку моделей (например, Azure: каталог моделей ≠ развёртывания).</summary>
    public string ModelListingNote { get; set; }

    /// <summary>Подпись поля модели: «Модель» или «Развёртывание (deployment)».</summary>
    public string ModelFieldLabel { get; set; } = "Модель";

    /// <summary>Режимы, которые API документирует. Фактическая поддержка моделью проверяется тестовым запросом.</summary>
    public IReadOnlyList<StructuredOutputMode> DocumentedModes { get; set; } = Array.Empty<StructuredOutputMode>();

    public string DocumentationUrl { get; set; }

    public string Notes { get; set; }

    public override string ToString() => DisplayName;
}
