using System;
using System.Collections.Generic;
using System.Linq;
using Fakt.Core.Records;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Core.Extraction;

/// <summary>Ответ модели не является корректным JSON ожидаемой формы (пакет нужно повторить или разделить).</summary>
public sealed class ExtractionFormatException : Exception
{
    public ExtractionFormatException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

public sealed class BatchValidationResult
{
    /// <summary>Проверенные результаты по порядковому номеру записи.</summary>
    public Dictionary<long, RowOutcome> Accepted { get; } = new();

    /// <summary>Записи без результата (пропущены моделью или получены повторно) — повторить отдельно.</summary>
    public List<long> RetryOrdinals { get; } = new();

    public List<string> UnknownIds { get; } = new();

    public List<string> DuplicateIds { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Проверка ответа модели на пакет записей: состав source_row_id (без неизвестных, повторов и пропусков),
/// типы, длины, даты и соответствие значений исходной записи. Модель предлагает кандидатов — решение
/// о сохранении принимает приложение.
/// </summary>
public sealed class ExtractionValidator
{
    private const int MaxWarningLength = 500;
    private const int MaxRawValueLength = 2000;

    public BatchValidationResult Validate(string responseText, IReadOnlyList<SourceRecord> batch, ExtractionContext context)
    {
        var root = ParseRoot(responseText);
        if (!(root["rows"] is JArray rows))
        {
            throw new ExtractionFormatException("В ответе нет массива rows.");
        }

        var expected = batch.ToDictionary(r => r.SourceRowId, StringComparer.OrdinalIgnoreCase);
        var result = new BatchValidationResult();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in rows)
        {
            RowWire row;
            try
            {
                row = token.ToObject<RowWire>(Serializer);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException)
            {
                var brokenId = (token as JObject)?["source_row_id"]?.ToString();
                result.Warnings.Add($"Результат {brokenId ?? "без идентификатора"} имеет некорректную форму и будет запрошен повторно.");
                continue;
            }

            var id = row?.SourceRowId?.Trim();
            if (string.IsNullOrEmpty(id) || !expected.TryGetValue(id, out var record))
            {
                result.UnknownIds.Add(id ?? "(пусто)");
                continue;
            }

            seen.TryGetValue(id, out var count);
            seen[id] = count + 1;
            if (count >= 1)
            {
                if (count == 1)
                {
                    result.DuplicateIds.Add(id);
                    result.Accepted.Remove(record.Ordinal);
                }

                continue;
            }

            result.Accepted[record.Ordinal] = ValidateRow(row, record, context);
        }

        foreach (var record in batch)
        {
            if (!result.Accepted.ContainsKey(record.Ordinal))
            {
                result.RetryOrdinals.Add(record.Ordinal);
            }
        }

        if (result.UnknownIds.Count > 0)
        {
            result.Warnings.Add($"Модель вернула {result.UnknownIds.Count} результат(ов) с неизвестными source_row_id; они отброшены.");
        }

        if (result.DuplicateIds.Count > 0)
        {
            result.Warnings.Add($"Повторяющиеся source_row_id ({string.Join(", ", result.DuplicateIds.Take(10))}) будут запрошены повторно.");
        }

        return result;
    }

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Include,
    });

    /// <summary>Разбор текста ответа. Допускается обёртка ```json … ``` у моделей без строгого JSON-режима.</summary>
    public static JObject ParseRoot(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExtractionFormatException("Пустой ответ модели.");
        }

        var trimmed = StripCodeFence(text.Trim());
        try
        {
            var token = JToken.Parse(trimmed);
            if (token is JObject obj)
            {
                return obj;
            }

            if (token is JArray array)
            {
                return new JObject { ["rows"] = array };
            }
        }
        catch (JsonReaderException ex)
        {
            throw new ExtractionFormatException("Ответ модели не является корректным JSON (возможно, усечён): " + ex.Message, ex);
        }

        throw new ExtractionFormatException("Ответ модели не является JSON-объектом.");
    }

    public static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline > 0 && lastFence > firstNewline)
        {
            return text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
        }

        return text;
    }

    public RowOutcome ValidateRow(RowWire row, SourceRecord record, ExtractionContext context)
    {
        var outcome = new RowOutcome
        {
            Ordinal = record.Ordinal,
            SourceRecordKey = record.SourceRecordKey,
            Line = record.Line,
            RowHash = record.Hash,
        };

        var scope = new RecordScope(record);
        var index = 0;
        foreach (var person in row.Persons ?? new List<PersonWire>())
        {
            if (person == null)
            {
                continue;
            }

            var draft = ValidatePerson(person, scope, context, outcome);
            if (draft == null)
            {
                continue;
            }

            if (person.PersonIndex.HasValue && person.PersonIndex.Value != index)
            {
                draft.Warnings.Add($"Номер лица {person.PersonIndex} от модели заменён порядковым {index}.");
            }

            draft.PersonIndex = index++;
            draft.Ordinal = record.Ordinal;
            draft.SourceRecordKey = record.SourceRecordKey;
            outcome.Observations.Add(draft);
        }

        var unassigned = new ObservationDraft
        {
            Kind = ObservationKinds.UnassignedFacts,
            PersonIndex = ObservationKinds.UnassignedPersonIndex,
            IdentityStatus = IdentityStatuses.Unresolved,
            Ordinal = record.Ordinal,
            SourceRecordKey = record.SourceRecordKey,
        };
        ValidateFacts(row.UnassignedFacts, scope, unassigned, null);
        if (unassigned.Facts.Count > 0)
        {
            unassigned.Warnings.Add("Принадлежность фактов конкретному лицу не установлена.");
            outcome.Observations.Add(unassigned);
        }

        outcome.Rejected.AddRange(unassigned.Rejected);
        unassigned.Rejected.Clear();

        var declaredNoFacts = string.Equals(row.Status, "no_facts", StringComparison.OrdinalIgnoreCase);
        if (outcome.Observations.Count > 0)
        {
            outcome.Kind = RowOutcomeKind.Extracted;
            if (declaredNoFacts)
            {
                outcome.Warnings.Add("Модель указала no_facts, но вернула факты; сохранены проверенные факты.");
            }
        }
        else
        {
            outcome.Kind = RowOutcomeKind.NoFacts;
        }

        foreach (var observation in outcome.Observations)
        {
            observation.AllJson = AllJsonBuilder.Build(observation, record, context);
        }

        return outcome;
    }

    private ObservationDraft ValidatePerson(PersonWire person, RecordScope scope, ExtractionContext context, RowOutcome outcome)
    {
        var draft = new ObservationDraft { Kind = ObservationKinds.Person };
        var sources = new Dictionary<string, FieldSourceWire>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in person.FieldSources ?? new List<FieldSourceWire>())
        {
            if (source?.Field != null && !sources.ContainsKey(source.Field.Trim()))
            {
                sources[source.Field.Trim()] = source;
            }
        }

        foreach (var unresolved in person.UnresolvedFields ?? new List<UnresolvedWire>())
        {
            if (unresolved == null || !MainFields.All.Contains(unresolved.Field) || string.IsNullOrWhiteSpace(unresolved.RawValue))
            {
                continue;
            }

            var raw = Cap(unresolved.RawValue.Trim(), MaxRawValueLength);
            if (!scope.Contains(raw))
            {
                draft.Rejected.Add(new RejectedCandidate { Kind = "unresolved_field", Name = unresolved.Field, Value = raw, Reason = "Исходное значение не найдено в записи" });
                continue;
            }

            draft.Unresolved.Add(new UnresolvedField
            {
                Field = unresolved.Field,
                RawValue = raw,
                Reason = Cap(unresolved.Reason?.Trim(), MaxWarningLength) ?? "Неоднозначное значение",
                SourceColumn = scope.ValidColumn(unresolved.SourceColumn),
            });
        }

        draft.Surname = AcceptText(MainFields.Surname, person.Surname, sources, scope, context, draft);
        draft.Name = AcceptText(MainFields.Name, person.Name, sources, scope, context, draft);
        draft.Patronymic = AcceptText(MainFields.Patronymic, person.Patronymic, sources, scope, context, draft);
        draft.BirthPlace = AcceptText(MainFields.BirthPlace, person.BirthPlace, sources, scope, context, draft);
        AcceptBirthDate(person.BirthDate, sources, scope, context, draft);

        ValidateFacts(person.Facts, scope, draft, draft);

        foreach (var warning in person.Warnings ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                draft.Warnings.Add(Cap(warning.Trim(), MaxWarningLength));
            }
        }

        draft.IdentityStatus = ComputeIdentityStatus(draft);
        if (!string.IsNullOrEmpty(person.IdentityStatus) && !string.Equals(person.IdentityStatus, draft.IdentityStatus, StringComparison.OrdinalIgnoreCase))
        {
            draft.Warnings.Add($"Статус личности пересчитан: модель указала «{person.IdentityStatus}», по проверенным полям — «{draft.IdentityStatus}».");
        }

        var hasMain = draft.Surname != null || draft.Name != null || draft.Patronymic != null || draft.BirthDate.HasValue || draft.BirthPlace != null;
        if (!hasMain && draft.Facts.Count == 0)
        {
            if (draft.Unresolved.Count > 0 || draft.Rejected.Count > 0)
            {
                // Сохранить нечего, но причины не теряются: они остаются в журнале результата строки.
                outcome.Rejected.AddRange(draft.Rejected);
                outcome.Warnings.AddRange(draft.Unresolved.Select(u => $"{MainFields.Title(u.Field)}: «{u.RawValue}» — {u.Reason}"));
            }

            return null;
        }

        return draft;
    }

    public static string ComputeIdentityStatus(ObservationDraft draft)
    {
        var present = (draft.Surname != null ? 1 : 0) + (draft.Name != null ? 1 : 0) + (draft.Patronymic != null ? 1 : 0);
        if (present == 3)
        {
            return IdentityStatuses.Identified;
        }

        return present == 0 ? IdentityStatuses.Unresolved : IdentityStatuses.Partial;
    }

    private static string AcceptText(string field, string value, Dictionary<string, FieldSourceWire> sources, RecordScope scope,
        ExtractionContext context, ObservationDraft draft)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = FactNormalizer.CollapseWhitespace(value);
        sources.TryGetValue(field, out var source);
        var evidence = source?.Evidence?.Trim();
        var title = MainFields.Title(field);

        if (!scope.Contains(clean))
        {
            if (!string.IsNullOrEmpty(evidence) && scope.Contains(evidence) && TextMatch.IsPlausibleVariant(clean, evidence))
            {
                draft.Warnings.Add($"{title}: модель привела «{Cap(evidence, 200)}» к форме «{Cap(clean, 200)}».");
            }
            else
            {
                draft.Rejected.Add(new RejectedCandidate { Kind = "field", Name = field, Value = Cap(clean, MaxRawValueLength), Reason = "Значение не найдено в исходной записи" });
                return null;
            }
        }

        var limit = context.Limits.For(field);
        if (clean.Length > limit)
        {
            draft.Unresolved.Add(new UnresolvedField
            {
                Field = field,
                RawValue = Cap(clean, 20000),
                Reason = $"Длина {clean.Length} символов превышает предел поля ({limit}); значение не усечено и не записано в основное поле",
                SourceColumn = scope.ValidColumn(source?.SourceColumn),
            });
            return null;
        }

        draft.FieldProvenance[field] = new FieldProvenance
        {
            SourceColumn = scope.ValidColumn(source?.SourceColumn),
            Evidence = Cap(!string.IsNullOrEmpty(evidence) && scope.Contains(evidence) ? evidence : clean, MaxRawValueLength),
        };
        return clean;
    }

    private static void AcceptBirthDate(string value, Dictionary<string, FieldSourceWire> sources, RecordScope scope,
        ExtractionContext context, ObservationDraft draft)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sources.TryGetValue(MainFields.BirthDate, out var source);
        var evidence = source?.Evidence?.Trim();
        var column = scope.ValidColumn(source?.SourceColumn);
        if (string.IsNullOrEmpty(evidence) || !scope.Contains(evidence))
        {
            // Фрагмент не указан или не найден: проверяем по значению колонки-источника либо по всей записи.
            evidence = column != null ? scope.ValueOf(column) : scope.EvidenceText;
        }

        var check = BirthDateChecker.Check(value.Trim(), evidence, context.Today);
        if (check.Date.HasValue)
        {
            draft.BirthDate = check.Date;
            draft.FieldProvenance[MainFields.BirthDate] = new FieldProvenance { SourceColumn = column, Evidence = Cap(evidence, 200) };
            return;
        }

        if (draft.Unresolved.All(u => u.Field != MainFields.BirthDate))
        {
            draft.Unresolved.Add(new UnresolvedField
            {
                Field = MainFields.BirthDate,
                RawValue = Cap(source?.Evidence?.Trim() ?? value.Trim(), MaxRawValueLength),
                Reason = check.Reason ?? "Дата не подтверждена исходной записью",
                SourceColumn = column,
            });
        }
    }

    private static void ValidateFacts(List<FactWire> facts, RecordScope scope, ObservationDraft target, ObservationDraft person)
    {
        if (facts == null)
        {
            return;
        }

        var mainValues = new HashSet<string>(StringComparer.Ordinal);
        if (person != null)
        {
            foreach (var main in new[] { person.Surname, person.Name, person.Patronymic, person.BirthPlace })
            {
                if (main != null)
                {
                    mainValues.Add(TextMatch.Normalize(main));
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.Value))
            {
                continue;
            }

            var type = fact.Type?.Trim().ToLowerInvariant();
            var label = string.IsNullOrWhiteSpace(fact.Label) ? null : Cap(fact.Label.Trim(), 200);
            if (!FactTypes.IsKnown(type))
            {
                label ??= type;
                type = FactTypes.Other;
            }

            var value = fact.Value.Trim();
            if (value.Length > 4000)
            {
                target.Rejected.Add(new RejectedCandidate { Kind = "fact", Name = type, Value = Cap(value, 200), Reason = "Значение факта длиннее 4000 символов" });
                continue;
            }

            var evidence = fact.Evidence?.Trim();
            var confirmed = scope.Contains(value) ||
                            (IsCodeLike(type) && scope.ContainsAlnum(value)) ||
                            (!string.IsNullOrEmpty(evidence) && scope.Contains(evidence) && TextMatch.IsPlausibleVariant(value, evidence));
            var composed = false;
            if (!confirmed && scope.AllWordsPresent(value))
            {
                confirmed = true;
                composed = true;
            }

            if (!confirmed)
            {
                target.Rejected.Add(new RejectedCandidate { Kind = "fact", Name = type, Value = Cap(value, MaxRawValueLength), Reason = "Значение не найдено в исходной записи" });
                continue;
            }

            if (mainValues.Contains(TextMatch.Normalize(value)))
            {
                target.Warnings.Add($"Факт «{FactTypes.Title(type)}» повторяет основное поле и не сохранён отдельно.");
                continue;
            }

            var normalized = FactNormalizer.NormalizeForStorage(type, value);
            var dedupeKey = type + "\u0001" + (normalized ?? TextMatch.Normalize(value));
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            var item = new FactItem
            {
                Type = type,
                Label = label,
                Value = value,
                NormalizedValue = normalized,
                SourceColumn = scope.ValidColumn(fact.SourceColumn),
                Evidence = !string.IsNullOrEmpty(evidence) && evidence != value && scope.Contains(evidence) ? Cap(evidence, MaxRawValueLength) : null,
                SearchKey = FactNormalizer.SearchKey(type, value),
            };
            target.Facts.Add(item);
            if (composed)
            {
                target.Warnings.Add($"Факт «{FactTypes.Title(type)}» составлен моделью из нескольких фрагментов записи.");
            }

            if (type == FactTypes.Phone && fact.NormalizedValue != null && normalized != null &&
                FactNormalizer.DigitsOnly(fact.NormalizedValue) != FactNormalizer.DigitsOnly(normalized))
            {
                target.Warnings.Add("Нормализованный телефон модели отличался от исходного значения; использована нормализация приложения без изменения кода страны.");
            }
        }
    }

    private static bool IsCodeLike(string type) =>
        type == FactTypes.Phone || type == FactTypes.BankAccount || type == FactTypes.BankCard || type == FactTypes.Inn ||
        type == FactTypes.Snils || type == FactTypes.Document || type == FactTypes.Vin || type == FactTypes.LicensePlate;

    private static string Cap(string value, int max)
    {
        if (value == null)
        {
            return null;
        }

        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }

    /// <summary>Нормализованное представление одной исходной записи для проверки фрагментов.</summary>
    private sealed class RecordScope
    {
        private readonly SourceRecord _record;
        private readonly string _normalized;
        private readonly string _alnum;
        private HashSet<string> _words;

        public RecordScope(SourceRecord record)
        {
            _record = record;
            EvidenceText = record.ToEvidenceText();
            _normalized = TextMatch.Normalize(EvidenceText);
            _alnum = TextMatch.AlnumLower(EvidenceText);
        }

        public string EvidenceText { get; }

        public bool Contains(string fragment) => TextMatch.Contains(_normalized, fragment);

        public bool ContainsAlnum(string fragment) => TextMatch.ContainsAlnum(_alnum, fragment, 5);

        public bool AllWordsPresent(string value)
        {
            _words ??= new HashSet<string>(TextMatch.Words(_normalized), StringComparer.Ordinal);
            var words = TextMatch.Words(TextMatch.Normalize(value)).Where(w => w.Length >= 2).ToList();
            return words.Count >= 2 && words.All(_words.Contains);
        }

        public string ValidColumn(string column)
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                return null;
            }

            var trimmed = column.Trim();
            foreach (var field in _record.Fields)
            {
                if (string.Equals(field.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return field.Key;
                }
            }

            return null;
        }

        public string ValueOf(string column)
        {
            foreach (var field in _record.Fields)
            {
                if (string.Equals(field.Key, column, StringComparison.Ordinal))
                {
                    return field.Value;
                }
            }

            return null;
        }
    }
}
