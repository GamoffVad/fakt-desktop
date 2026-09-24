using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Sql;

/// <summary>
/// Атомарная запись результатов. Одна короткая транзакция на единицу фиксации:
/// реестр результатов строк → наблюдения PersonFacts (только для впервые зарегистрированных строк,
/// в порядке source_row_id) → поисковая проекция и индекс идентификаторов → ошибки строк → checkpoint.
/// Повтор той же фиксации распознаётся по CommitId; повтор тех же строк другой фиксацией не создаёт
/// дубликатов (реестр и уникальный индекс наблюдений). Данные передаются через SqlBulkCopy во временные таблицы.
/// </summary>
public sealed class SqlFactWriter : IFactWriter
{
    private readonly SqlConnectionFactory _factory;
    private readonly FieldLimits _limits;
    private SqlConnection _connection;

    /// <summary>Тестовая точка: исключение после COMMIT до возврата результата (имитация потери подтверждения).</summary>
    internal Action AfterCommitHook { get; set; }

    public SqlFactWriter(SqlConnectionFactory factory, FieldLimits limits)
    {
        _factory = factory;
        _limits = limits ?? new FieldLimits();
    }

    private const string TempTables = @"
CREATE TABLE #fakt_rows (
    [Ordinal] BIGINT NOT NULL PRIMARY KEY,
    [Outcome] TINYINT NOT NULL,
    [ObservationCount] INT NOT NULL,
    [SourceLine] BIGINT NULL,
    [RowHash] CHAR(64) NULL,
    [DetailsJson] NVARCHAR(MAX) NULL,
    [ErrorCode] VARCHAR(64) NULL,
    [ErrorMessage] NVARCHAR(2000) NULL,
    [Stage] VARCHAR(32) NULL);
CREATE TABLE #fakt_obs (
    [Ordinal] BIGINT NOT NULL,
    [RecordKey] VARCHAR(64) NOT NULL,
    [PersonIndex] INT NOT NULL,
    [Surname] NVARCHAR(MAX) NULL,
    [Name] NVARCHAR(MAX) NULL,
    [Patronymic] NVARCHAR(MAX) NULL,
    [BirthDate] DATE NULL,
    [BirthPlace] NVARCHAR(MAX) NULL,
    [AllJson] NVARCHAR(MAX) NOT NULL,
    [AllText] NVARCHAR(MAX) NOT NULL,
    [NameText] NVARCHAR(1000) NULL,
    [BirthPlaceText] NVARCHAR(2000) NULL,
    [FactsText] NVARCHAR(MAX) NULL,
    PRIMARY KEY ([Ordinal], [PersonIndex]));
CREATE TABLE #fakt_vals (
    [Ordinal] BIGINT NOT NULL,
    [PersonIndex] INT NOT NULL,
    [FactType] VARCHAR(32) NOT NULL,
    [NormalizedValue] NVARCHAR(400) NOT NULL);
CREATE TABLE #fakt_ins ([ID] BIGINT NOT NULL, [RecordKey] VARCHAR(64) NOT NULL, [PersonIndex] INT NOT NULL);
CREATE TABLE #fakt_new ([Ordinal] BIGINT NOT NULL PRIMARY KEY, [Outcome] TINYINT NOT NULL);";

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection != null && _connection.State == ConnectionState.Open)
        {
            return;
        }

        _connection?.Dispose();
        _connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(_connection, TempTables);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommitResult> CommitAsync(CommitUnit unit, CancellationToken cancellationToken)
    {
        if (unit?.Rows == null)
        {
            throw new ArgumentNullException(nameof(unit));
        }

        await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        var fileText = SearchDocumentBuilder.FileText(unit.FileName, unit.FileCode);
        var rows = RowsTable();
        var observations = ObservationsTable();
        var values = new DataTable();
        values.Columns.Add("Ordinal", typeof(long));
        values.Columns.Add("PersonIndex", typeof(int));
        values.Columns.Add("FactType", typeof(string));
        values.Columns.Add("NormalizedValue", typeof(string));

        foreach (var row in unit.Rows.OrderBy(r => r.Ordinal))
        {
            rows.Rows.Add(row.Ordinal, (byte)row.Kind, row.Observations.Count, (object)row.Line ?? DBNull.Value, (object)row.RowHash ?? DBNull.Value,
                (object)DetailsJson(row) ?? DBNull.Value, (object)Truncate(row.ErrorCode, 64) ?? DBNull.Value,
                (object)Truncate(row.ErrorMessage, 2000) ?? DBNull.Value, row.Kind == RowOutcomeKind.Error ? (object)StageOf(row.ErrorCode) : DBNull.Value);
            foreach (var observation in row.Observations.OrderBy(o => o.PersonIndex))
            {
                var document = SearchDocumentBuilder.FromDraft(observation, fileText);
                observations.Rows.Add(row.Ordinal, row.SourceRecordKey, observation.PersonIndex,
                    (object)observation.Surname ?? DBNull.Value, (object)observation.Name ?? DBNull.Value, (object)observation.Patronymic ?? DBNull.Value,
                    (object)observation.BirthDate ?? DBNull.Value, (object)observation.BirthPlace ?? DBNull.Value, observation.AllJson,
                    document.AllText ?? string.Empty, (object)document.NameText ?? DBNull.Value, (object)document.BirthPlaceText ?? DBNull.Value,
                    (object)document.FactsText ?? DBNull.Value);
                foreach (var value in document.Values.Distinct())
                {
                    values.Rows.Add(row.Ordinal, observation.PersonIndex, value.Key, value.Value);
                }
            }
        }

        var names = _factory.Names;
        var c = names.Columns;
        var sql = $@"
SET XACT_ABORT ON;
IF EXISTS (SELECT 1 FROM {names.Aux("FaktCommitLog")} WHERE [CommitId] = @commitId)
BEGIN
    SELECT CAST(1 AS BIT), CAST(0 AS BIGINT), CAST(0 AS BIGINT), CAST(0 AS BIGINT), CAST(0 AS BIGINT), CAST(0 AS BIGINT),
           ISNULL((SELECT [ConfirmedOrdinal] FROM {names.Aux("FaktFileProgress")} WHERE [SourceFileId] = @file AND [ExtractionVersion] = @ver), 0);
    RETURN;
END;
INSERT INTO {names.Aux("FaktCommitLog")} ([CommitId], [JobFileId], [ToOrdinal]) VALUES (@commitId, @jobFile, @to);

DELETE FROM #fakt_new;
DELETE FROM #fakt_ins;

INSERT INTO {names.Aux("FaktRowOutcomes")} ([SourceFileId], [ExtractionVersion], [RecordOrdinal], [Outcome], [ObservationCount], [SourceLine], [RowHash], [JobId], [DetailsJson], [ProcessedAtUtc])
OUTPUT inserted.[RecordOrdinal], inserted.[Outcome] INTO #fakt_new ([Ordinal], [Outcome])
SELECT @file, @ver, r.[Ordinal], r.[Outcome], r.[ObservationCount], r.[SourceLine], r.[RowHash], @job, r.[DetailsJson], SYSUTCDATETIME()
FROM #fakt_rows AS r
WHERE NOT EXISTS (
    SELECT 1 FROM {names.Aux("FaktRowOutcomes")} AS x WITH (UPDLOCK, HOLDLOCK)
    WHERE x.[SourceFileId] = @file AND x.[ExtractionVersion] = @ver AND x.[RecordOrdinal] = r.[Ordinal]);

INSERT INTO {names.PF} ({names.Col(c.Surname)}, {names.Col(c.Name)}, {names.Col(c.Patronymic)}, {names.Col(c.BirthDate)}, {names.Col(c.BirthPlace)},
    {names.Col(c.All)}, {names.Col(c.FileId)}, [SourceRecordKey], [PersonIndex], [ExtractionVersion], [JobId], [CreatedAtUtc])
OUTPUT inserted.{names.Col(c.PersonFactsId)}, inserted.[SourceRecordKey], inserted.[PersonIndex] INTO #fakt_ins ([ID], [RecordKey], [PersonIndex])
SELECT o.[Surname], o.[Name], o.[Patronymic], o.[BirthDate], o.[BirthPlace], o.[AllJson], @file, o.[RecordKey], o.[PersonIndex], @ver, @job, SYSUTCDATETIME()
FROM #fakt_obs AS o
INNER JOIN #fakt_new AS n ON n.[Ordinal] = o.[Ordinal]
WHERE NOT EXISTS (
    SELECT 1 FROM {names.PF} AS p
    WHERE p.{names.Col(c.FileId)} = @file AND p.[SourceRecordKey] = o.[RecordKey] AND p.[PersonIndex] = o.[PersonIndex] AND p.[ExtractionVersion] = @ver)
ORDER BY o.[Ordinal], o.[PersonIndex];

INSERT INTO {names.Aux("FaktSearchDocs")} ([PersonFactId], [SourceFileId], [AllText], [NameText], [BirthPlaceText], [FactsText], [FileText], [UpdatedAtUtc])
SELECT i.[ID], @file, o.[AllText], o.[NameText], o.[BirthPlaceText], o.[FactsText], @fileText, SYSUTCDATETIME()
FROM #fakt_ins AS i
INNER JOIN #fakt_obs AS o ON o.[RecordKey] = i.[RecordKey] AND o.[PersonIndex] = i.[PersonIndex];

INSERT INTO {names.Aux("FaktFactValues")} ([FactType], [NormalizedValue], [PersonFactId])
SELECT DISTINCT v.[FactType], v.[NormalizedValue], i.[ID]
FROM #fakt_vals AS v
INNER JOIN #fakt_obs AS o ON o.[Ordinal] = v.[Ordinal] AND o.[PersonIndex] = v.[PersonIndex]
INNER JOIN #fakt_ins AS i ON i.[RecordKey] = o.[RecordKey] AND i.[PersonIndex] = o.[PersonIndex];

INSERT INTO {names.Aux("FaktRowErrors")} ([JobFileId], [SourceFileId], [ExtractionVersion], [RecordOrdinal], [SourceLine], [Stage], [ErrorCode], [Message], [CreatedAtUtc])
SELECT @jobFile, @file, @ver, r.[Ordinal], r.[SourceLine], ISNULL(r.[Stage], 'extraction'), ISNULL(r.[ErrorCode], 'unknown'), r.[ErrorMessage], SYSUTCDATETIME()
FROM #fakt_rows AS r
INNER JOIN #fakt_new AS n ON n.[Ordinal] = r.[Ordinal]
WHERE r.[Outcome] = 3
  AND NOT EXISTS (
    SELECT 1 FROM {names.Aux("FaktRowErrors")} AS e
    WHERE e.[SourceFileId] = @file AND e.[ExtractionVersion] = @ver AND e.[RecordOrdinal] = r.[Ordinal] AND e.[Stage] = ISNULL(r.[Stage], 'extraction'));

DECLARE @ext BIGINT = (SELECT COUNT_BIG(*) FROM #fakt_new WHERE [Outcome] = 1);
DECLARE @nof BIGINT = (SELECT COUNT_BIG(*) FROM #fakt_new WHERE [Outcome] = 2);
DECLARE @err BIGINT = (SELECT COUNT_BIG(*) FROM #fakt_new WHERE [Outcome] = 3);
DECLARE @obs BIGINT = (SELECT COUNT_BIG(*) FROM #fakt_ins);
DECLARE @total BIGINT = (SELECT COUNT_BIG(*) FROM #fakt_rows);

UPDATE {names.Aux("FaktFileProgress")}
SET [ConfirmedOrdinal] = CASE WHEN [ConfirmedOrdinal] < @to THEN @to ELSE [ConfirmedOrdinal] END,
    [Extracted] += @ext, [NoFacts] += @nof, [Errors] += @err, [Observations] += @obs, [UpdatedAtUtc] = SYSUTCDATETIME()
WHERE [SourceFileId] = @file AND [ExtractionVersion] = @ver;
IF @@ROWCOUNT = 0
    INSERT INTO {names.Aux("FaktFileProgress")} ([SourceFileId], [ExtractionVersion], [ConfirmedOrdinal], [Extracted], [NoFacts], [Errors], [Observations], [Completed], [UpdatedAtUtc])
    VALUES (@file, @ver, @to, @ext, @nof, @err, @obs, 0, SYSUTCDATETIME());

UPDATE {names.Aux("FaktJobFiles")}
SET [RecordsExtracted] += @ext, [RecordsNoFacts] += @nof, [RecordsError] += @err, [ObservationsSaved] += @obs,
    [Requests] += @req, [InputTokens] += @in, [OutputTokens] += @out,
    [RecordsRead] = CASE WHEN [RecordsRead] < @read THEN @read ELSE [RecordsRead] END,
    [UpdatedAtUtc] = SYSUTCDATETIME()
WHERE [JobFileId] = @jobFile;

SELECT CAST(0 AS BIT), @ext, @nof, @err, @obs, @total - (@ext + @nof + @err),
       (SELECT [ConfirmedOrdinal] FROM {names.Aux("FaktFileProgress")} WHERE [SourceFileId] = @file AND [ExtractionVersion] = @ver);";

        using var transaction = _connection.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            using (var clear = _factory.Command(_connection, "DELETE FROM #fakt_rows; DELETE FROM #fakt_obs; DELETE FROM #fakt_vals;", transaction))
            {
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await BulkCopyAsync(_connection, transaction, "#fakt_rows", rows, cancellationToken).ConfigureAwait(false);
            await BulkCopyAsync(_connection, transaction, "#fakt_obs", observations, cancellationToken).ConfigureAwait(false);
            await BulkCopyAsync(_connection, transaction, "#fakt_vals", values, cancellationToken).ConfigureAwait(false);

            var result = new CommitResult();
            using (var command = _factory.Command(_connection, sql, transaction))
            {
                command.Parameters.Add("@commitId", SqlDbType.UniqueIdentifier).Value = unit.CommitId;
                command.Parameters.Add("@file", SqlDbType.BigInt).Value = unit.SourceFileId;
                command.Parameters.Add("@ver", SqlDbType.VarChar, 64).Value = unit.ExtractionVersion;
                command.Parameters.Add("@job", SqlDbType.BigInt).Value = unit.JobId;
                command.Parameters.Add("@jobFile", SqlDbType.BigInt).Value = unit.JobFileId;
                command.Parameters.Add("@to", SqlDbType.BigInt).Value = unit.ToOrdinal;
                command.Parameters.Add("@fileText", SqlDbType.NVarChar, 2000).Value = (object)fileText ?? DBNull.Value;
                command.Parameters.Add("@req", SqlDbType.BigInt).Value = unit.Usage?.Requests ?? 0;
                command.Parameters.Add("@in", SqlDbType.BigInt).Value = unit.Usage?.InputTokens ?? 0;
                command.Parameters.Add("@out", SqlDbType.BigInt).Value = unit.Usage?.OutputTokens ?? 0;
                command.Parameters.Add("@read", SqlDbType.BigInt).Value = unit.Usage?.RecordsRead ?? 0;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result.Duplicate = reader.GetBoolean(0);
                    result.ExtractedRows = (int)reader.GetInt64(1);
                    result.NoFactsRows = (int)reader.GetInt64(2);
                    result.ErrorRows = (int)reader.GetInt64(3);
                    result.ObservationsInserted = (int)reader.GetInt64(4);
                    result.RowsAlreadyPresent = (int)reader.GetInt64(5);
                    result.ConfirmedOrdinal = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                    result.RowsRegistered = result.ExtractedRows + result.NoFactsRows + result.ErrorRows;
                }
            }

            transaction.Commit();
            AfterCommitHook?.Invoke();
            return result;
        }
        catch
        {
            try
            {
                if (transaction.Connection != null)
                {
                    transaction.Rollback();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (SqlException)
            {
            }

            if (_connection.State != ConnectionState.Open)
            {
                _connection.Dispose();
                _connection = null;
            }

            throw;
        }
    }

    private static string StageOf(string errorCode)
    {
        switch (errorCode)
        {
            case "field_count_mismatch":
            case "short_record":
            case "invalid_json":
            case "not_json_object":
            case "decode_error":
            case "structure_changed":
            case "missing_values":
                return "parse";
            default:
                return "extraction";
        }
    }

    private static string DetailsJson(RowOutcome row)
    {
        if (row.Warnings.Count == 0 && row.Rejected.Count == 0)
        {
            return null;
        }

        var details = new JObject();
        if (row.Warnings.Count > 0)
        {
            details["warnings"] = new JArray(row.Warnings.Distinct().Take(50));
        }

        if (row.Rejected.Count > 0)
        {
            details["rejected_candidates"] = new JArray(row.Rejected.Take(50).Select(r => new JObject
            {
                ["kind"] = r.Kind,
                ["name"] = r.Name,
                ["value"] = r.Value,
                ["reason"] = r.Reason,
            }));
        }

        return details.ToString(Formatting.None);
    }

    private static string Truncate(string value, int max) => value == null ? null : value.Length <= max ? value : value.Substring(0, max);

    private static DataTable RowsTable()
    {
        var table = new DataTable();
        table.Columns.Add("Ordinal", typeof(long));
        table.Columns.Add("Outcome", typeof(byte));
        table.Columns.Add("ObservationCount", typeof(int));
        table.Columns.Add("SourceLine", typeof(long));
        table.Columns.Add("RowHash", typeof(string));
        table.Columns.Add("DetailsJson", typeof(string));
        table.Columns.Add("ErrorCode", typeof(string));
        table.Columns.Add("ErrorMessage", typeof(string));
        table.Columns.Add("Stage", typeof(string));
        return table;
    }

    private static DataTable ObservationsTable()
    {
        var table = new DataTable();
        table.Columns.Add("Ordinal", typeof(long));
        table.Columns.Add("RecordKey", typeof(string));
        table.Columns.Add("PersonIndex", typeof(int));
        table.Columns.Add("Surname", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Patronymic", typeof(string));
        table.Columns.Add("BirthDate", typeof(DateTime));
        table.Columns.Add("BirthPlace", typeof(string));
        table.Columns.Add("AllJson", typeof(string));
        table.Columns.Add("AllText", typeof(string));
        table.Columns.Add("NameText", typeof(string));
        table.Columns.Add("BirthPlaceText", typeof(string));
        table.Columns.Add("FactsText", typeof(string));
        return table;
    }

    internal static DataTable DocsTable()
    {
        var table = new DataTable();
        table.Columns.Add("PersonFactId", typeof(long));
        table.Columns.Add("SourceFileId", typeof(long));
        table.Columns.Add("AllText", typeof(string));
        table.Columns.Add("NameText", typeof(string));
        table.Columns.Add("BirthPlaceText", typeof(string));
        table.Columns.Add("FactsText", typeof(string));
        table.Columns.Add("FileText", typeof(string));
        table.Columns.Add("UpdatedAtUtc", typeof(DateTime));
        return table;
    }

    internal static DataTable ValuesTable()
    {
        var table = new DataTable();
        table.Columns.Add("FactType", typeof(string));
        table.Columns.Add("NormalizedValue", typeof(string));
        table.Columns.Add("PersonFactId", typeof(long));
        return table;
    }

    internal static async Task BulkCopyAsync(SqlConnection connection, SqlTransaction transaction, string destination, DataTable table, CancellationToken cancellationToken)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.FireTriggers, transaction)
        {
            DestinationTableName = destination,
            BatchSize = 0,
            BulkCopyTimeout = 600,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
