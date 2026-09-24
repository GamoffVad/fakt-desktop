using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fakt.Core.Extraction;
using Fakt.Core.Records;
using Fakt.Core.Storage;

namespace Fakt.IntegrationTests;

/// <summary>Построение синтетических результатов извлечения для тестов записи и поиска.</summary>
internal static class TestData
{
    public static readonly ExtractionContext Context = new()
    {
        ProviderId = "test-provider",
        ModelId = "test-model",
        ExtractionVersion = "facts-v1-test",
        ProcessedAtUtc = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc),
    };

    public static SourceRecord Record(long ordinal, params (string Column, string Value)[] fields)
    {
        var list = fields.Select(f => new KeyValuePair<string, string>(f.Column, f.Value)).ToList();
        var canonical = string.Join("|", list.Select(f => f.Key + "=" + f.Value));
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).ToLowerInvariant();
        return new SourceRecord(ordinal, ordinal + 1, ordinal + 1, list, hash, null, null);
    }

    public static ObservationDraft Person(string surname, string name, string patronymic, DateTime? birthDate, string birthPlace, params FactItem[] facts)
    {
        var draft = new ObservationDraft
        {
            Surname = surname,
            Name = name,
            Patronymic = patronymic,
            BirthDate = birthDate,
            BirthPlace = birthPlace,
        };
        foreach (var fact in facts)
        {
            fact.NormalizedValue ??= FactNormalizer.NormalizeForStorage(fact.Type, fact.Value);
            fact.SearchKey ??= FactNormalizer.SearchKey(fact.Type, fact.Value);
            draft.Facts.Add(fact);
        }

        draft.IdentityStatus = ExtractionValidator.ComputeIdentityStatus(draft);
        return draft;
    }

    public static FactItem Fact(string type, string value, string column = null) => new() { Type = type, Value = value, SourceColumn = column };

    public static RowOutcome Extracted(SourceRecord record, params ObservationDraft[] persons)
    {
        var outcome = new RowOutcome
        {
            Ordinal = record.Ordinal,
            SourceRecordKey = record.SourceRecordKey,
            Line = record.Line,
            RowHash = record.Hash,
            Kind = RowOutcomeKind.Extracted,
        };
        var index = 0;
        foreach (var person in persons)
        {
            person.PersonIndex = index++;
            person.Ordinal = record.Ordinal;
            person.SourceRecordKey = record.SourceRecordKey;
            person.AllJson = AllJsonBuilder.Build(person, record, Context);
            outcome.Observations.Add(person);
        }

        return outcome;
    }

    public static RowOutcome NoFacts(SourceRecord record) => new()
    {
        Ordinal = record.Ordinal,
        SourceRecordKey = record.SourceRecordKey,
        Line = record.Line,
        RowHash = record.Hash,
        Kind = RowOutcomeKind.NoFacts,
    };

    public static CommitUnit Unit(long jobId, long jobFileId, long sourceFileId, string fileName, string fileCode, long toOrdinal, params RowOutcome[] rows) => new()
    {
        JobId = jobId,
        JobFileId = jobFileId,
        SourceFileId = sourceFileId,
        ExtractionVersion = Context.ExtractionVersion,
        FileName = fileName,
        FileCode = fileCode,
        Rows = rows,
        ToOrdinal = toOrdinal,
        Usage = new UsageDelta { Requests = 1, InputTokens = 100, OutputTokens = 50, RecordsRead = toOrdinal },
    };

    public static byte[] Sha(string text)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
    }
}
