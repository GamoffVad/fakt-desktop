using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Fakt.Core.Worker;

namespace Fakt.Core.Records;

/// <summary>
/// Логическая запись исходного файла. Устойчивый идентификатор строится из порядкового номера
/// (ordinal), а не из хеша содержимого: две одинаковые строки в разных позициях остаются разными.
/// </summary>
public sealed class SourceRecord
{
    public SourceRecord(long ordinal, long? line, long? endLine, IReadOnlyList<KeyValuePair<string, string>> fields, string hash, string errorCode, string errorMessage)
    {
        Ordinal = ordinal;
        Line = line;
        EndLine = endLine;
        Fields = fields ?? Array.Empty<KeyValuePair<string, string>>();
        Hash = hash;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public long Ordinal { get; }

    public long? Line { get; }

    public long? EndLine { get; }

    /// <summary>Пары «колонка — значение»; значение null означает отсутствие в источнике.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

    /// <summary>SHA-256 канонического представления записи (hex), вычисленный worker.</summary>
    public string Hash { get; }

    public string ErrorCode { get; }

    public string ErrorMessage { get; }

    public bool HasError => ErrorCode != null;

    /// <summary>source_row_id, передаваемый модели: «r» + порядковый номер.</summary>
    public string SourceRowId => ToSourceRowId(Ordinal);

    /// <summary>Ключ записи для SQL (SourceRecordKey): десятичный порядковый номер.</summary>
    public string SourceRecordKey => Ordinal.ToString(CultureInfo.InvariantCulture);

    public static string ToSourceRowId(long ordinal) => "r" + ordinal.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseSourceRowId(string id, out long ordinal)
    {
        ordinal = 0;
        return id != null && id.Length > 1 && (id[0] == 'r' || id[0] == 'R') &&
               long.TryParse(id.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0;
    }

    /// <summary>Поля с непустыми значениями — только они отправляются модели.</summary>
    public IEnumerable<KeyValuePair<string, string>> NonEmptyFields()
    {
        foreach (var field in Fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Value))
            {
                yield return field;
            }
        }
    }

    /// <summary>Текст записи вместе с заголовками — основа проверки фрагментов evidence.</summary>
    public string ToEvidenceText()
    {
        var builder = new StringBuilder();
        foreach (var field in Fields)
        {
            if (field.Value == null)
            {
                continue;
            }

            builder.Append(field.Key).Append(": ").Append(field.Value).Append('\n');
        }

        return builder.ToString();
    }

    public int TotalValueLength()
    {
        var total = 0;
        foreach (var field in Fields)
        {
            total += (field.Key?.Length ?? 0) + (field.Value?.Length ?? 0);
        }

        return total;
    }

    public static List<SourceRecord> FromChunk(RecordChunk chunk)
    {
        var result = new List<SourceRecord>(chunk.Records.Count);
        foreach (var wire in chunk.Records)
        {
            if (wire.Error != null || wire.Values == null)
            {
                result.Add(new SourceRecord(wire.Ordinal, wire.Line, wire.EndLine, null, wire.Hash,
                    wire.Error?.Code ?? "missing_values", wire.Error?.Message ?? "Worker не вернул значения записи"));
                continue;
            }

            var fields = new List<KeyValuePair<string, string>>(wire.Values.Count);
            for (var i = 0; i < wire.Values.Count; i++)
            {
                var column = i < chunk.Columns.Count ? chunk.Columns[i] : "Колонка " + (i + 1);
                fields.Add(new KeyValuePair<string, string>(column, wire.Values[i]));
            }

            result.Add(new SourceRecord(wire.Ordinal, wire.Line, wire.EndLine, fields, wire.Hash, null, null));
        }

        return result;
    }
}
