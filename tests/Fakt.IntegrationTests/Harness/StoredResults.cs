using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Sql;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Testing;

/// <summary>
/// Сохранённые результаты одной версии файла (SourceFileId + ExtractionVersion), прочитанные прямо из SQL:
/// реестр строк, наблюдения, поисковая проекция, индекс идентификаторов, ошибки, граница обработки.
/// Для сравнения прогонов вычисляется хеш содержимого без полей, зависящих от задания (job_id, время обработки).
/// </summary>
public sealed class StoredFileResults
{
    public long SourceFileId { get; set; }
    public string ExtractionVersion { get; set; }

    public long RowOutcomes { get; set; }
    public long DistinctOrdinals { get; set; }
    public long MinOrdinal { get; set; }
    public long MaxOrdinal { get; set; }
    public long OutcomeExtracted { get; set; }
    public long OutcomeNoFacts { get; set; }
    public long OutcomeErrors { get; set; }

    public long PersonFacts { get; set; }

    /// <summary>Наблюдений с повторяющимся ключом (запись, лицо) — должно быть 0.</summary>
    public long DuplicateObservationKeys { get; set; }

    public long SearchDocs { get; set; }
    public long FactValues { get; set; }
    public long RowErrors { get; set; }

    /// <summary>Пар соседних наблюдений, у которых порядок ID не совпадает с порядком source_row_id.</summary>
    public long IdOrderInversions { get; set; }

    public long ConfirmedOrdinal { get; set; }
    public bool Completed { get; set; }
    public long ProgressExtracted { get; set; }
    public long ProgressNoFacts { get; set; }
    public long ProgressErrors { get; set; }
    public long ProgressObservations { get; set; }

    public string ContentHash { get; set; }

    /// <summary>Реестр строк непрерывен: номера 1..ConfirmedOrdinal без пропусков и повторов.</summary>
    public bool IsContiguousUpToCheckpoint =>
        RowOutcomes == ConfirmedOrdinal && DistinctOrdinals == ConfirmedOrdinal && (ConfirmedOrdinal == 0 || (MinOrdinal == 1 && MaxOrdinal == ConfirmedOrdinal));

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture,
            "outcomes={0} (distinct {1}, {2}..{3}; extracted {4}, no_facts {5}, errors {6}), person_facts={7}, dup_keys={8}, search_docs={9}, fact_values={10}, row_errors={11}, confirmed={12}, completed={13}, id_inversions={14}, hash={15}",
            RowOutcomes, DistinctOrdinals, MinOrdinal, MaxOrdinal, OutcomeExtracted, OutcomeNoFacts, OutcomeErrors, PersonFacts, DuplicateObservationKeys, SearchDocs,
            FactValues, RowErrors, ConfirmedOrdinal, Completed, IdOrderInversions, ContentHash?.Substring(0, 16));

    public static async Task<StoredFileResults> ReadAsync(DatabaseSettings settings, long sourceFileId, string extractionVersion, bool computeContentHash,
        CancellationToken cancellationToken, int commandTimeoutSeconds = 600)
    {
        var names = new SqlNames(settings);
        var c = names.Columns;
        var pf = names.PF;
        var id = names.Col(c.PersonFactsId);
        var fileId = names.Col(c.FileId);
        var result = new StoredFileResults { SourceFileId = sourceFileId, ExtractionVersion = extractionVersion };
        using var connection = new SqlConnection(SqlConnectionFactory.Build(settings, null));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        SqlCommand Command(string sql)
        {
            var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
            command.Parameters.Add("@f", SqlDbType.BigInt).Value = sourceFileId;
            command.Parameters.Add("@v", SqlDbType.VarChar, 64).Value = extractionVersion;
            return command;
        }

        using (var command = Command($@"
SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT [RecordOrdinal]), ISNULL(MIN([RecordOrdinal]), 0), ISNULL(MAX([RecordOrdinal]), 0),
       ISNULL(SUM(CASE WHEN [Outcome] = 1 THEN 1 ELSE 0 END), 0), ISNULL(SUM(CASE WHEN [Outcome] = 2 THEN 1 ELSE 0 END), 0),
       ISNULL(SUM(CASE WHEN [Outcome] = 3 THEN 1 ELSE 0 END), 0)
FROM {names.Aux("FaktRowOutcomes")} WHERE [SourceFileId] = @f AND [ExtractionVersion] = @v;
SELECT COUNT_BIG(*) FROM {pf} WHERE {fileId} = @f AND [ExtractionVersion] = @v;
SELECT COUNT_BIG(*) FROM (SELECT [SourceRecordKey], [PersonIndex] FROM {pf} WHERE {fileId} = @f AND [ExtractionVersion] = @v
                          GROUP BY [SourceRecordKey], [PersonIndex] HAVING COUNT_BIG(*) > 1) AS d;
SELECT COUNT_BIG(*) FROM {names.Aux("FaktSearchDocs")} AS d INNER JOIN {pf} AS p ON p.{id} = d.[PersonFactId] WHERE p.{fileId} = @f AND p.[ExtractionVersion] = @v;
SELECT COUNT_BIG(*) FROM {names.Aux("FaktFactValues")} AS v INNER JOIN {pf} AS p ON p.{id} = v.[PersonFactId] WHERE p.{fileId} = @f AND p.[ExtractionVersion] = @v;
SELECT COUNT_BIG(*) FROM {names.Aux("FaktRowErrors")} WHERE [SourceFileId] = @f AND [ExtractionVersion] = @v;
SELECT COUNT_BIG(*) FROM (
    SELECT {id} AS [Id], LAG({id}) OVER (ORDER BY CAST([SourceRecordKey] AS BIGINT), [PersonIndex]) AS [PrevId]
    FROM {pf} WHERE {fileId} = @f AND [ExtractionVersion] = @v) AS x
WHERE x.[PrevId] > x.[Id];
SELECT [ConfirmedOrdinal], [Extracted], [NoFacts], [Errors], [Observations], [Completed]
FROM {names.Aux("FaktFileProgress")} WHERE [SourceFileId] = @f AND [ExtractionVersion] = @v;"))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result.RowOutcomes = reader.GetInt64(0);
            result.DistinctOrdinals = reader.GetInt64(1);
            result.MinOrdinal = reader.GetInt64(2);
            result.MaxOrdinal = reader.GetInt64(3);
            result.OutcomeExtracted = Convert.ToInt64(reader.GetValue(4));
            result.OutcomeNoFacts = Convert.ToInt64(reader.GetValue(5));
            result.OutcomeErrors = Convert.ToInt64(reader.GetValue(6));
            result.PersonFacts = await ScalarAsync(reader, cancellationToken).ConfigureAwait(false);
            result.DuplicateObservationKeys = await ScalarAsync(reader, cancellationToken).ConfigureAwait(false);
            result.SearchDocs = await ScalarAsync(reader, cancellationToken).ConfigureAwait(false);
            result.FactValues = await ScalarAsync(reader, cancellationToken).ConfigureAwait(false);
            result.RowErrors = await ScalarAsync(reader, cancellationToken).ConfigureAwait(false);
            result.IdOrderInversions = await ScalarAsync(reader, cancellationToken).ConfigureAwait(false);
            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.ConfirmedOrdinal = reader.GetInt64(0);
                result.ProgressExtracted = reader.GetInt64(1);
                result.ProgressNoFacts = reader.GetInt64(2);
                result.ProgressErrors = reader.GetInt64(3);
                result.ProgressObservations = reader.GetInt64(4);
                result.Completed = reader.GetBoolean(5);
            }
        }

        if (computeContentHash)
        {
            result.ContentHash = await ContentHashAsync(connection, names, sourceFileId, extractionVersion, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<long> ScalarAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.GetInt64(0);
    }

    /// <summary>SHA-256 канонического содержимого: реестр строк, ошибки, наблюдения (основные поля, [ALL] без job_id и времени) и индекс идентификаторов.</summary>
    private static async Task<string> ContentHashAsync(SqlConnection connection, SqlNames names, long sourceFileId, string version, int timeout, CancellationToken cancellationToken)
    {
        var c = names.Columns;
        string fileText;
        using (var file = new SqlCommand($"SELECT {names.Col(c.FileName)}, {names.Col(c.FileCode)} FROM {names.SF} WHERE {names.Col(c.SourceFilesId)} = @f;", connection))
        {
            file.Parameters.Add("@f", SqlDbType.BigInt).Value = sourceFileId;
            using var fileReader = await file.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            fileText = await fileReader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? SearchDocumentBuilder.FileText(Convert.ToString(fileReader.GetValue(0)), Convert.ToString(fileReader.GetValue(1)))
                : string.Empty;
        }

        using var sha = SHA256.Create();
        var buffer = new StringBuilder();

        void Flush(bool final = false)
        {
            if (buffer.Length == 0 && !final)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(buffer.ToString());
            if (final)
            {
                sha.TransformFinalBlock(bytes, 0, bytes.Length);
            }
            else
            {
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }

            buffer.Clear();
        }

        var sql = $@"
SELECT [RecordOrdinal], [Outcome], [ObservationCount], [SourceLine], [RowHash], [DetailsJson]
FROM {names.Aux("FaktRowOutcomes")} WHERE [SourceFileId] = @f AND [ExtractionVersion] = @v ORDER BY [RecordOrdinal];
SELECT [RecordOrdinal], [Stage], [ErrorCode], [Message]
FROM {names.Aux("FaktRowErrors")} WHERE [SourceFileId] = @f AND [ExtractionVersion] = @v ORDER BY [RecordOrdinal], [Stage];
SELECT p.[SourceRecordKey], p.[PersonIndex], p.{names.Col(c.Surname)}, p.{names.Col(c.Name)}, p.{names.Col(c.Patronymic)},
       p.{names.Col(c.BirthDate)}, p.{names.Col(c.BirthPlace)}, p.{names.Col(c.All)},
       (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), v.[FactType] + N':' + v.[NormalizedValue]), N'|') WITHIN GROUP (ORDER BY v.[FactType], v.[NormalizedValue])
        FROM {names.Aux("FaktFactValues")} AS v WHERE v.[PersonFactId] = p.{names.Col(c.PersonFactsId)}),
       d.[AllText]
FROM {names.PF} AS p
LEFT JOIN {names.Aux("FaktSearchDocs")} AS d ON d.[PersonFactId] = p.{names.Col(c.PersonFactsId)}
WHERE p.{names.Col(c.FileId)} = @f AND p.[ExtractionVersion] = @v
ORDER BY CAST(p.[SourceRecordKey] AS BIGINT), p.[PersonIndex];";
        using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        command.Parameters.Add("@f", SqlDbType.BigInt).Value = sourceFileId;
        command.Parameters.Add("@v", SqlDbType.VarChar, 64).Value = version;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        string S(int i) => reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

        buffer.Append("outcomes\n");
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            buffer.Append(S(0)).Append('|').Append(S(1)).Append('|').Append(S(2)).Append('|').Append(S(3)).Append('|').Append(S(4)).Append('|').Append(S(5)).Append('\n');
            if (buffer.Length > 1 << 20)
            {
                Flush();
            }
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        buffer.Append("errors\n");
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            buffer.Append(S(0)).Append('|').Append(S(1)).Append('|').Append(S(2)).Append('|').Append(S(3)).Append('\n');
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        buffer.Append("observations\n");
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var birth = reader.IsDBNull(5) ? "∅" : reader.GetDateTime(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            buffer.Append(S(0)).Append('|').Append(S(1)).Append('|').Append(S(2)).Append('|').Append(S(3)).Append('|').Append(S(4)).Append('|')
                .Append(birth).Append('|').Append(S(6)).Append('|').Append(CanonicalAll(reader.IsDBNull(7) ? null : reader.GetString(7))).Append('|')
                .Append(S(8)).Append('|').Append(StripFileText(S(9), fileText)).Append('\n');
            if (buffer.Length > 1 << 20)
            {
                Flush();
            }
        }

        Flush(final: true);
        return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>[ALL] без полей, зависящих от конкретного задания: provenance.job_id, provenance.processed_at_utc.</summary>
    private static string CanonicalAll(string json)
    {
        if (json == null)
        {
            return "∅";
        }

        var root = JObject.Parse(json);
        if (root["provenance"] is JObject provenance)
        {
            provenance.Remove("job_id");
            provenance.Remove("processed_at_utc");
        }

        return root.ToString(Formatting.None);
    }

    /// <summary>
    /// В AllText в конце добавлены имя и код файла (SearchDocumentBuilder.FileText) — у копий файла они разные,
    /// поэтому сравнивается часть наблюдения; если окончание не совпало, текст остаётся целиком (хеш различится).
    /// </summary>
    private static string StripFileText(string allText, string fileText)
    {
        if (string.IsNullOrEmpty(fileText))
        {
            return allText;
        }

        if (allText == fileText)
        {
            return string.Empty;
        }

        return allText.EndsWith(" " + fileText, StringComparison.Ordinal) ? allText.Substring(0, allText.Length - fileText.Length - 1) : allText;
    }
}
