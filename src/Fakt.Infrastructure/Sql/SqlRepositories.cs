using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Processing;
using Fakt.Core.Storage;

namespace Fakt.Infrastructure.Sql;

/// <summary>
/// Регистрация версии источника: поиск существующей записи по (SourcePathHash, ContentHash), иначе вставка
/// с новым кодом T_… (повтор при коллизии UNIQUE). Выполняется в транзакции с блокировкой диапазона,
/// чтобы два клиента не создали две записи одной версии.
/// </summary>
public sealed class SqlSourceFileRepository : ISourceFileRepository
{
    private const int MaxCodeAttempts = 5;
    private readonly SqlConnectionFactory _factory;

    public SqlSourceFileRepository(SqlConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<SourceFileRow> RegisterAsync(SourceFileRegistration registration, CancellationToken cancellationToken)
    {
        var names = _factory.Names;
        var c = names.Columns;
        for (var attempt = 1; ; attempt++)
        {
            using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                using (var find = _factory.Command(connection, $@"
SELECT TOP (1) {names.Col(c.SourceFilesId)}, {names.Col(c.FileCode)}, {names.Col(c.FileName)}
FROM {names.SF} WITH (UPDLOCK, HOLDLOCK)
WHERE [SourcePathHash] = @ph AND [ContentHash] = @ch
ORDER BY {names.Col(c.SourceFilesId)};", transaction))
                {
                    find.Parameters.Add("@ph", SqlDbType.Binary, 32).Value = registration.PathHash;
                    find.Parameters.Add("@ch", SqlDbType.Binary, 32).Value = registration.ContentHash;
                    using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var existing = new SourceFileRow
                        {
                            Id = Convert.ToInt64(reader.GetValue(0)),
                            FileCode = Convert.ToString(reader.GetValue(1)),
                            FileName = Convert.ToString(reader.GetValue(2)),
                            Created = false,
                        };
                        reader.Close();
                        transaction.Commit();
                        return existing;
                    }
                }

                var code = FileCodeGenerator.Next();
                using var insert = _factory.Command(connection, $@"
INSERT INTO {names.SF} ({names.Col(c.FileCode)}, {names.Col(c.FileName)}, [SourcePath], [SourcePathHash], [FileSize], [LastWriteTimeUtc], [ContentHash], [CreatedAtUtc])
OUTPUT inserted.{names.Col(c.SourceFilesId)}
VALUES (@code, @name, @path, @ph, @size, @mtime, @ch, SYSUTCDATETIME());", transaction);
                insert.Parameters.Add("@code", SqlDbType.VarChar, 32).Value = code;
                insert.Parameters.Add("@name", SqlDbType.NVarChar, 1024).Value = registration.FileName;
                insert.Parameters.Add("@path", SqlDbType.NVarChar, -1).Value = registration.FullPath;
                insert.Parameters.Add("@ph", SqlDbType.Binary, 32).Value = registration.PathHash;
                insert.Parameters.Add("@size", SqlDbType.BigInt).Value = registration.Size;
                insert.Parameters.Add("@mtime", SqlDbType.DateTime2).Value = registration.LastWriteTimeUtc;
                insert.Parameters.Add("@ch", SqlDbType.Binary, 32).Value = registration.ContentHash;
                var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                transaction.Commit();
                return new SourceFileRow { Id = id, FileCode = code, FileName = registration.FileName, Created = true };
            }
            catch (SqlException ex) when (SqlConnectionFactory.IsUniqueViolation(ex) && attempt < MaxCodeAttempts)
            {
                // Коллизия FileCode (или параллельная регистрация той же версии): повтор с новым кодом / повторным поиском.
                SafeRollback(transaction);
            }
            catch
            {
                SafeRollback(transaction);
                throw;
            }
        }
    }

    public async Task MarkFileCompletedAsync(long sourceFileId, string extractionVersion, bool completed, CancellationToken cancellationToken)
    {
        var names = _factory.Names;
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
UPDATE {names.Aux("FaktFileProgress")} SET [Completed] = @done, [UpdatedAtUtc] = SYSUTCDATETIME()
WHERE [SourceFileId] = @file AND [ExtractionVersion] = @ver;
IF @@ROWCOUNT = 0
    INSERT INTO {names.Aux("FaktFileProgress")} ([SourceFileId], [ExtractionVersion], [Completed]) VALUES (@file, @ver, @done);");
        command.Parameters.Add("@done", SqlDbType.Bit).Value = completed;
        command.Parameters.Add("@file", SqlDbType.BigInt).Value = sourceFileId;
        command.Parameters.Add("@ver", SqlDbType.VarChar, 64).Value = extractionVersion;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void SafeRollback(SqlTransaction transaction)
    {
        try
        {
            if (transaction?.Connection != null)
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
    }
}

/// <summary>Задания, файлы заданий, границы обработки и ошибки строк (вспомогательные таблицы FAKT).</summary>
public sealed class SqlJobRepository : IJobRepository
{
    private readonly SqlConnectionFactory _factory;

    public SqlJobRepository(SqlConnectionFactory factory)
    {
        _factory = factory;
    }

    private SqlNames Names => _factory.Names;

    public async Task<long> CreateJobAsync(JobRecord job, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
INSERT INTO {Names.Aux("FaktJobs")} ([JobGuid], [Status], [CreatedBy], [SnapshotJson], [Summary])
OUTPUT inserted.[JobId]
VALUES (@guid, @status, @by, @snapshot, @summary);");
        command.Parameters.Add("@guid", SqlDbType.UniqueIdentifier).Value = job.JobGuid;
        command.Parameters.Add("@status", SqlDbType.VarChar, 32).Value = job.Status.ToString();
        command.Parameters.Add("@by", SqlDbType.NVarChar, 256).Value = (object)job.CreatedBy ?? DBNull.Value;
        command.Parameters.Add("@snapshot", SqlDbType.NVarChar, -1).Value = job.SnapshotJson;
        command.Parameters.Add("@summary", SqlDbType.NVarChar, 2000).Value = (object)job.Summary ?? DBNull.Value;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<long> AddJobFileAsync(JobFileRecord file, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
INSERT INTO {Names.Aux("FaktJobFiles")} ([JobId], [SourceFileId], [FileCode], [FileName], [SourcePath], [Status], [StructureJson], [ExtractionVersion],
    [ExpectedSize], [ExpectedLastWriteUtc], [ContentHash])
OUTPUT inserted.[JobFileId]
VALUES (@job, @file, @code, @name, @path, @status, @structure, @ver, @size, @mtime, @hash);");
        command.Parameters.Add("@job", SqlDbType.BigInt).Value = file.JobId;
        command.Parameters.Add("@file", SqlDbType.BigInt).Value = file.SourceFileId;
        command.Parameters.Add("@code", SqlDbType.VarChar, 32).Value = (object)file.FileCode ?? DBNull.Value;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 1024).Value = (object)file.FileName ?? DBNull.Value;
        command.Parameters.Add("@path", SqlDbType.NVarChar, -1).Value = (object)file.SourcePath ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.VarChar, 32).Value = file.Status.ToString();
        command.Parameters.Add("@structure", SqlDbType.NVarChar, -1).Value = (object)file.StructureJson ?? DBNull.Value;
        command.Parameters.Add("@ver", SqlDbType.VarChar, 64).Value = file.ExtractionVersion;
        command.Parameters.Add("@size", SqlDbType.BigInt).Value = file.ExpectedSize;
        command.Parameters.Add("@mtime", SqlDbType.DateTime2).Value = file.ExpectedLastWriteUtc;
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = file.ContentHashHex == null ? DBNull.Value : (object)HexToBytes(file.ContentHashHex);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task UpdateJobAsync(long jobId, JobStatus status, string summary, CancellationToken cancellationToken)
    {
        var terminal = status == JobStatus.Completed || status == JobStatus.CompletedWithErrors || status == JobStatus.Failed ||
                       status == JobStatus.Cancelled || status == JobStatus.Interrupted;
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
UPDATE {Names.Aux("FaktJobs")}
SET [Status] = @status, [Summary] = @summary, [FinishedAtUtc] = CASE WHEN @terminal = 1 THEN SYSUTCDATETIME() ELSE NULL END
WHERE [JobId] = @job;");
        command.Parameters.Add("@status", SqlDbType.VarChar, 32).Value = status.ToString();
        command.Parameters.Add("@summary", SqlDbType.NVarChar, 2000).Value = (object)Truncate(summary, 2000) ?? DBNull.Value;
        command.Parameters.Add("@terminal", SqlDbType.Bit).Value = terminal;
        command.Parameters.Add("@job", SqlDbType.BigInt).Value = jobId;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateJobFileAsync(long jobFileId, FileStatus status, string lastError, UsageDelta usage, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
UPDATE {Names.Aux("FaktJobFiles")}
SET [Status] = @status,
    [LastError] = @error,
    [Requests] += @req, [InputTokens] += @in, [OutputTokens] += @out,
    [RecordsRead] = CASE WHEN [RecordsRead] < @read THEN @read ELSE [RecordsRead] END,
    [UpdatedAtUtc] = SYSUTCDATETIME()
WHERE [JobFileId] = @id;");
        command.Parameters.Add("@status", SqlDbType.VarChar, 32).Value = status.ToString();
        command.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value = (object)Truncate(lastError, 2000) ?? DBNull.Value;
        command.Parameters.Add("@req", SqlDbType.BigInt).Value = usage?.Requests ?? 0;
        command.Parameters.Add("@in", SqlDbType.BigInt).Value = usage?.InputTokens ?? 0;
        command.Parameters.Add("@out", SqlDbType.BigInt).Value = usage?.OutputTokens ?? 0;
        command.Parameters.Add("@read", SqlDbType.BigInt).Value = usage?.RecordsRead ?? 0;
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = jobFileId;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileProgress> GetFileProgressAsync(long sourceFileId, string extractionVersion, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
SELECT [ConfirmedOrdinal], [Extracted], [NoFacts], [Errors], [Observations], [Completed]
FROM {Names.Aux("FaktFileProgress")} WHERE [SourceFileId] = @file AND [ExtractionVersion] = @ver;");
        command.Parameters.Add("@file", SqlDbType.BigInt).Value = sourceFileId;
        command.Parameters.Add("@ver", SqlDbType.VarChar, 64).Value = extractionVersion;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new FileProgress();
        }

        return new FileProgress
        {
            ConfirmedOrdinal = reader.GetInt64(0),
            Extracted = reader.GetInt64(1),
            NoFacts = reader.GetInt64(2),
            Errors = reader.GetInt64(3),
            Observations = reader.GetInt64(4),
            Completed = reader.GetBoolean(5),
        };
    }

    private string JobSelect => $@"
SELECT j.[JobId], j.[JobGuid], j.[Status], j.[CreatedAtUtc], j.[FinishedAtUtc], j.[CreatedBy], j.[SnapshotJson], j.[Summary],
       COUNT(f.[JobFileId]),
       ISNULL(SUM(f.[RecordsExtracted] + f.[RecordsNoFacts] + f.[RecordsError]), 0),
       ISNULL(SUM(f.[ObservationsSaved]), 0), ISNULL(SUM(f.[RecordsNoFacts]), 0), ISNULL(SUM(f.[RecordsError]), 0),
       ISNULL(SUM(f.[Requests]), 0), ISNULL(SUM(f.[InputTokens]), 0), ISNULL(SUM(f.[OutputTokens]), 0)
FROM {Names.Aux("FaktJobs")} AS j
LEFT JOIN {Names.Aux("FaktJobFiles")} AS f ON f.[JobId] = j.[JobId]";

    private const string JobGroupBy = " GROUP BY j.[JobId], j.[JobGuid], j.[Status], j.[CreatedAtUtc], j.[FinishedAtUtc], j.[CreatedBy], j.[SnapshotJson], j.[Summary]";

    public async Task<IReadOnlyList<JobRecord>> ListJobsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, JobSelect + JobGroupBy + " ORDER BY j.[JobId] DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;");
        command.Parameters.Add("@skip", SqlDbType.Int).Value = Math.Max(0, skip);
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Max(1, take);
        return await ReadJobsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRecord> GetJobAsync(long jobId, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, JobSelect + " WHERE j.[JobId] = @job" + JobGroupBy + ";");
        command.Parameters.Add("@job", SqlDbType.BigInt).Value = jobId;
        var jobs = await ReadJobsAsync(command, cancellationToken).ConfigureAwait(false);
        return jobs.Count > 0 ? jobs[0] : null;
    }

    private static async Task<List<JobRecord>> ReadJobsAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<JobRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new JobRecord
            {
                JobId = reader.GetInt64(0),
                JobGuid = reader.GetGuid(1),
                Status = Enum.TryParse<JobStatus>(reader.GetString(2), out var status) ? status : JobStatus.Failed,
                CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                FinishedAtUtc = reader.IsDBNull(4) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                SnapshotJson = reader.GetString(6),
                Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
                FileCount = reader.GetInt32(8),
                RecordsProcessed = Convert.ToInt64(reader.GetValue(9)),
                ObservationsSaved = Convert.ToInt64(reader.GetValue(10)),
                RecordsNoFacts = Convert.ToInt64(reader.GetValue(11)),
                RecordsError = Convert.ToInt64(reader.GetValue(12)),
                Requests = Convert.ToInt64(reader.GetValue(13)),
                InputTokens = Convert.ToInt64(reader.GetValue(14)),
                OutputTokens = Convert.ToInt64(reader.GetValue(15)),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<JobFileRecord>> ListJobFilesAsync(long jobId, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
SELECT f.[JobFileId], f.[JobId], f.[SourceFileId], f.[FileCode], f.[FileName], f.[SourcePath], f.[Status], f.[StructureJson], f.[ExtractionVersion],
       f.[ExpectedSize], f.[ExpectedLastWriteUtc], f.[RecordsRead], f.[RecordsExtracted], f.[RecordsNoFacts], f.[RecordsError], f.[ObservationsSaved],
       f.[Requests], f.[InputTokens], f.[OutputTokens], f.[LastError], f.[UpdatedAtUtc], ISNULL(p.[ConfirmedOrdinal], 0), f.[ContentHash]
FROM {Names.Aux("FaktJobFiles")} AS f
LEFT JOIN {Names.Aux("FaktFileProgress")} AS p ON p.[SourceFileId] = f.[SourceFileId] AND p.[ExtractionVersion] = f.[ExtractionVersion]
WHERE f.[JobId] = @job
ORDER BY f.[JobFileId];");
        command.Parameters.Add("@job", SqlDbType.BigInt).Value = jobId;
        var result = new List<JobFileRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new JobFileRecord
            {
                JobFileId = reader.GetInt64(0),
                JobId = reader.GetInt64(1),
                SourceFileId = reader.GetInt64(2),
                FileCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                SourcePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                Status = Enum.TryParse<FileStatus>(reader.GetString(6), out var status) ? status : FileStatus.Error,
                StructureJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                ExtractionVersion = reader.GetString(8),
                ExpectedSize = reader.GetInt64(9),
                ExpectedLastWriteUtc = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc),
                RecordsRead = reader.GetInt64(11),
                RecordsExtracted = reader.GetInt64(12),
                RecordsNoFacts = reader.GetInt64(13),
                RecordsError = reader.GetInt64(14),
                ObservationsSaved = reader.GetInt64(15),
                Requests = reader.GetInt64(16),
                InputTokens = reader.GetInt64(17),
                OutputTokens = reader.GetInt64(18),
                LastError = reader.IsDBNull(19) ? null : reader.GetString(19),
                UpdatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(20), DateTimeKind.Utc),
                CheckpointOrdinal = reader.GetInt64(21),
                ContentHashHex = reader.IsDBNull(22) ? null : BitConverter.ToString((byte[])reader.GetValue(22)).Replace("-", string.Empty).ToLowerInvariant(),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<RowErrorRecord>> ListErrorsAsync(long jobFileId, int take, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
SELECT TOP (@take) [ErrorId], [JobFileId], [RecordOrdinal], [SourceLine], [Stage], [ErrorCode], [Message], [CreatedAtUtc]
FROM {Names.Aux("FaktRowErrors")} WHERE [JobFileId] = @id ORDER BY [RecordOrdinal];");
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Max(1, take);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = jobFileId;
        var result = new List<RowErrorRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new RowErrorRecord
            {
                Id = reader.GetInt64(0),
                JobFileId = reader.GetInt64(1),
                SourceRecordKey = reader.GetInt64(2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceLine = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                Stage = reader.GetString(4),
                ErrorCode = reader.GetString(5),
                Message = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
            });
        }

        return result;
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _factory.Command(connection, $@"
UPDATE j SET [Status] = 'Interrupted', [FinishedAtUtc] = SYSUTCDATETIME(),
    [Summary] = CONCAT(ISNULL(j.[Summary] + N' ', N''), N'Задание прервано (нет активности более 30 минут).')
FROM {Names.Aux("FaktJobs")} AS j
WHERE j.[Status] = 'Running'
  AND NOT EXISTS (SELECT 1 FROM {Names.Aux("FaktJobFiles")} AS f WHERE f.[JobId] = j.[JobId] AND f.[UpdatedAtUtc] > DATEADD(MINUTE, -30, SYSUTCDATETIME()))
  AND j.[CreatedAtUtc] < DATEADD(MINUTE, -30, SYSUTCDATETIME());");
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) => value == null ? null : value.Length <= max ? value : value.Substring(0, max);

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }
}

public sealed class SqlStorage : IStorage
{
    private readonly SqlConnectionFactory _factory;

    public SqlStorage(SqlConnectionFactory factory)
    {
        _factory = factory;
        SourceFiles = new SqlSourceFileRepository(factory);
        Jobs = new SqlJobRepository(factory);
        Search = new SqlSearchRepository(factory);
    }

    public ISourceFileRepository SourceFiles { get; }

    public IJobRepository Jobs { get; }

    public ISearchRepository Search { get; }

    public IFactWriter CreateWriter(FieldLimits limits) => new SqlFactWriter(_factory, limits);
}

public sealed class SqlStorageFactory : IStorageFactory
{
    public IStorage Create(Fakt.Core.Settings.DatabaseSettings settings, string sqlPassword) => new SqlStorage(new SqlConnectionFactory(settings, sqlPassword));
}
