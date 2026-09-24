using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Fakt.Core.Extraction;

public enum RowOutcomeKind
{
    Extracted = 1,
    NoFacts = 2,
    Error = 3,
}

public static class ObservationKinds
{
    public const string Person = "person";

    /// <summary>Факты строки, принадлежность которых модель не смогла установить (PersonIndex = -1).</summary>
    public const string UnassignedFacts = "unassigned_facts";

    public const int UnassignedPersonIndex = -1;
}

public sealed class FactItem
{
    public string Type { get; set; }
    public string Label { get; set; }
    public string Value { get; set; }
    public string NormalizedValue { get; set; }
    public string SourceColumn { get; set; }
    public string Evidence { get; set; }

    /// <summary>Ключ индекса точного поиска; null — значение не индексируется.</summary>
    public string SearchKey { get; set; }
}

public sealed class FieldProvenance
{
    public string SourceColumn { get; set; }
    public string Evidence { get; set; }
}

public sealed class UnresolvedField
{
    public string Field { get; set; }
    public string RawValue { get; set; }
    public string Reason { get; set; }
    public string SourceColumn { get; set; }
}

/// <summary>Кандидат модели, отклонённый проверкой (например, значение не найдено в записи).</summary>
public sealed class RejectedCandidate
{
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }
    public string Reason { get; set; }
}

/// <summary>Проверенное наблюдение об одном лице (или неразрешённом субъекте) в одной исходной записи.</summary>
public sealed class ObservationDraft
{
    public long Ordinal { get; set; }
    public string SourceRecordKey { get; set; }
    public int PersonIndex { get; set; }
    public string Kind { get; set; } = ObservationKinds.Person;
    public string IdentityStatus { get; set; }
    public string Surname { get; set; }
    public string Name { get; set; }
    public string Patronymic { get; set; }
    public DateTime? BirthDate { get; set; }
    public string BirthPlace { get; set; }
    public List<FactItem> Facts { get; set; } = new();
    public Dictionary<string, FieldProvenance> FieldProvenance { get; set; } = new();
    public List<UnresolvedField> Unresolved { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<RejectedCandidate> Rejected { get; set; } = new();

    /// <summary>Содержимое [ALL]: формируется <see cref="AllJsonBuilder"/> после проверки.</summary>
    public string AllJson { get; set; }

    public IEnumerable<KeyValuePair<string, string>> SearchValues()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in Facts)
        {
            if (fact.SearchKey != null && seen.Add(fact.Type + "\u0001" + fact.SearchKey))
            {
                yield return new KeyValuePair<string, string>(fact.Type, fact.SearchKey);
            }
        }
    }

    /// <summary>Текст наблюдения для поисковой проекции (без данных файла — их добавляет слой SQL).</summary>
    public string BuildSearchText()
    {
        var parts = new List<string>();
        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        Add(Surname);
        Add(Name);
        Add(Patronymic);
        if (BirthDate.HasValue)
        {
            Add(BirthDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(BirthDate.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        }

        Add(BirthPlace);
        foreach (var fact in Facts)
        {
            Add(fact.Value);
            if (fact.NormalizedValue != null && fact.NormalizedValue != fact.Value)
            {
                Add(fact.NormalizedValue);
            }

            Add(fact.Label);
        }

        foreach (var unresolved in Unresolved)
        {
            Add(unresolved.RawValue);
        }

        return string.Join(" ", parts);
    }
}

public sealed class RowOutcome
{
    public long Ordinal { get; set; }
    public string SourceRecordKey { get; set; }
    public long? Line { get; set; }
    public string RowHash { get; set; }
    public RowOutcomeKind Kind { get; set; }
    public List<ObservationDraft> Observations { get; set; } = new();
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<RejectedCandidate> Rejected { get; set; } = new();

    public static RowOutcome Error(long ordinal, long? line, string hash, string code, string message) => new()
    {
        Ordinal = ordinal,
        SourceRecordKey = ordinal.ToString(CultureInfo.InvariantCulture),
        Line = line,
        RowHash = hash,
        Kind = RowOutcomeKind.Error,
        ErrorCode = code,
        ErrorMessage = message,
    };
}

public sealed class FieldLimits
{
    public int Surname { get; set; } = 200;
    public int Name { get; set; } = 200;
    public int Patronymic { get; set; } = 200;
    public int BirthPlace { get; set; } = 1000;
    public int FactValue { get; set; } = 4000;

    public int For(string field)
    {
        switch (field)
        {
            case MainFields.Surname: return Surname;
            case MainFields.Name: return Name;
            case MainFields.Patronymic: return Patronymic;
            case MainFields.BirthPlace: return BirthPlace;
            default: return 4000;
        }
    }
}

/// <summary>Контекст происхождения, записываемый в [ALL].provenance.</summary>
public sealed class ExtractionContext
{
    public string ProviderId { get; set; }
    public string ModelId { get; set; }
    public string PromptVersion { get; set; } = Prompts.FactsPromptVersion;
    public string ExtractionVersion { get; set; }
    public long? JobId { get; set; }
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime Today { get; set; } = DateTime.Today;
    public FieldLimits Limits { get; set; } = new();
}

/// <summary>
/// Версия извлечения — часть ключа идемпотентности (ID_FileName, SourceRecordKey, PersonIndex, ExtractionVersion).
/// Повторная обработка той же версии файла той же конфигурацией не создаёт дубликатов; другая модель
/// или промпт образуют новую версию наблюдений.
/// </summary>
public static class ExtractionVersionCalculator
{
    public static string Compute(string providerId, string modelId, string promptVersion, string structureJson)
    {
        var canonical = string.Join("\n", providerId ?? string.Empty, modelId ?? string.Empty, promptVersion ?? string.Empty, structureJson ?? string.Empty);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var hex = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return (promptVersion ?? "facts") + "-" + hex;
    }
}
