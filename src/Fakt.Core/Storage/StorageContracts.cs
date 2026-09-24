using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Logging;
using Fakt.Core.Processing;
using Fakt.Core.Search;
using Fakt.Core.Settings;

namespace Fakt.Core.Storage;

public enum IssueSeverity
{
    Info,
    Warning,
    Blocking,
}

public sealed class SchemaIssue
{
    public IssueSeverity Severity { get; set; }

    /// <summary>Объект: [dbo].[PersonFacts].[Имя], полнотекстовый индекс и т. п.</summary>
    public string Object { get; set; }

    public string Expected { get; set; }

    public string Actual { get; set; }

    public string Message { get; set; }

    /// <summary>Следующий шаг: сопоставить имя, применить миграцию, выдать право.</summary>
    public string Suggestion { get; set; }

    public string MigrationId { get; set; }

    /// <summary>Логическое поле (для предложения сопоставления имён), например «Surname».</summary>
    public string LogicalField { get; set; }
}

public sealed class ColumnInfo
{
    public string Name { get; set; }
    public string DataType { get; set; }

    /// <summary>Длина в символах (для nvarchar — символы, не байты); -1 — MAX; null — не применимо.</summary>
    public int? MaxLength { get; set; }

    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsComputed { get; set; }
    public string DefaultDefinition { get; set; }

    public string TypeText()
    {
        var type = DataType?.ToUpperInvariant();
        if (MaxLength.HasValue && (type == "NVARCHAR" || type == "VARCHAR" || type == "NCHAR" || type == "CHAR" || type == "VARBINARY" || type == "BINARY"))
        {
            type += MaxLength.Value < 0 ? "(MAX)" : "(" + MaxLength.Value + ")";
        }

        return type + (IsNullable ? " NULL" : " NOT NULL") + (IsIdentity ? " IDENTITY" : string.Empty);
    }
}

public sealed class TableInfo
{
    public string Schema { get; set; }
    public string Name { get; set; }
    public bool Exists { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
    public long? ApproximateRows { get; set; }
}

public sealed class PermissionInfo
{
    public bool CanSelect { get; set; }
    public bool CanInsert { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanCreateTable { get; set; }
    public bool CanAlter { get; set; }
    public bool CanCreateFullText { get; set; }
}

public sealed class FullTextInfo
{
    public bool Installed { get; set; }
    public bool CatalogExists { get; set; }
    public bool IndexExists { get; set; }
    public int? Language { get; set; }
    public int? PopulateStatus { get; set; }
    public string PopulateStatusText { get; set; }
    public long? PendingChanges { get; set; }
    public long? IndexedItems { get; set; }
    public string Notes { get; set; }

    public bool Ready => Installed && CatalogExists && IndexExists;
}

public sealed class MigrationInfo
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public bool Applied { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public string AppliedBy { get; set; }
    public bool RequiresNoTransaction { get; set; }

    /// <summary>Миграция добавляет столбцы или ограничения в пользовательские таблицы (без удаления данных).</summary>
    public bool AltersUserTables { get; set; }

    public bool RequiredForProcessing { get; set; }
    public bool RequiredForSearch { get; set; }
    public string Script { get; set; }
}

public sealed class MigrationResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class SchemaReport
{
    public bool Connected { get; set; }
    public string ConnectionError { get; set; }
    public ErrorCategory ErrorCategory { get; set; }
    public string ServerVersion { get; set; }
    public string ProductVersion { get; set; }
    public string Edition { get; set; }
    public string DatabaseName { get; set; }
    public string LoginName { get; set; }
    public bool? EncryptedConnection { get; set; }
    public string AuthScheme { get; set; }
    public TableInfo SourceFiles { get; set; }
    public TableInfo PersonFacts { get; set; }
    public PermissionInfo Permissions { get; set; } = new();
    public FullTextInfo FullText { get; set; } = new();
    public List<MigrationInfo> Migrations { get; set; } = new();
    public List<SchemaIssue> Issues { get; set; } = new();
    public FieldLimits Limits { get; set; } = new();
    public List<string> ProcessingBlockers { get; set; } = new();
    public List<string> SearchBlockers { get; set; } = new();

    public bool CanProcess => Connected && ProcessingBlockers.Count == 0;
    public bool CanSearch => Connected && SearchBlockers.Count == 0;
}

public interface IDatabaseAdmin
{
    Task<SchemaReport> InspectAsync(DatabaseSettings settings, string sqlPassword, CancellationToken cancellationToken);

    Task<MigrationResult> ApplyMigrationAsync(DatabaseSettings settings, string sqlPassword, string migrationId, string appliedBy, CancellationToken cancellationToken);

    /// <summary>Перестроение поисковой проекции пакетами (восстановимая вспомогательная структура).</summary>
    Task<long> RebuildSearchProjectionAsync(DatabaseSettings settings, string sqlPassword, IProgress<long> progress, CancellationToken cancellationToken);
}

public sealed class SourceFileRegistration
{
    public string FileName { get; set; }
    public string FullPath { get; set; }
    public byte[] PathHash { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public byte[] ContentHash { get; set; }
}

public sealed class SourceFileRow
{
    public long Id { get; set; }
    public string FileCode { get; set; }
    public string FileName { get; set; }

    /// <summary>true — создана новая запись источника; false — найдена существующая той же версии.</summary>
    public bool Created { get; set; }
}

public sealed class RowErrorRecord
{
    public long Id { get; set; }
    public long JobFileId { get; set; }
    public string SourceRecordKey { get; set; }
    public long? SourceLine { get; set; }
    public string Stage { get; set; }
    public string ErrorCode { get; set; }
    public string Message { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class UsageDelta
{
    public long Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long RecordsRead { get; set; }

    public bool IsEmpty => Requests == 0 && InputTokens == 0 && OutputTokens == 0 && RecordsRead == 0;
}

/// <summary>Непрерывный по порядковым номерам набор результатов, фиксируемый одной короткой транзакцией вместе с checkpoint.</summary>
public sealed class CommitUnit
{
    /// <summary>
    /// Идентификатор фиксации. Повтор той же фиксации (сбой после COMMIT до получения подтверждения)
    /// распознаётся по нему и не изменяет данные и счётчики.
    /// </summary>
    public Guid CommitId { get; set; } = Guid.NewGuid();

    public long JobId { get; set; }
    public long JobFileId { get; set; }
    public long SourceFileId { get; set; }
    public string ExtractionVersion { get; set; }
    public string FileName { get; set; }
    public string FileCode { get; set; }
    public IReadOnlyList<RowOutcome> Rows { get; set; }

    /// <summary>Граница подтверждённой обработки после фиксации (включительно).</summary>
    public long ToOrdinal { get; set; }

    public UsageDelta Usage { get; set; } = new();
}

public sealed class CommitResult
{
    /// <summary>Фиксация с этим CommitId уже была выполнена ранее; данные не изменялись.</summary>
    public bool Duplicate { get; set; }

    public int ObservationsInserted { get; set; }
    public int RowsRegistered { get; set; }
    public int RowsAlreadyPresent { get; set; }
    public int ExtractedRows { get; set; }
    public int NoFactsRows { get; set; }
    public int ErrorRows { get; set; }
    public long ConfirmedOrdinal { get; set; }
}

public interface IFactWriter : IDisposable
{
    Task<CommitResult> CommitAsync(CommitUnit unit, CancellationToken cancellationToken);
}

public interface IJobRepository
{
    Task<long> CreateJobAsync(JobRecord job, CancellationToken cancellationToken);

    Task<long> AddJobFileAsync(JobFileRecord file, CancellationToken cancellationToken);

    Task UpdateJobAsync(long jobId, JobStatus status, string summary, CancellationToken cancellationToken);

    Task UpdateJobFileAsync(long jobFileId, FileStatus status, string lastError, UsageDelta usage, CancellationToken cancellationToken);

    /// <summary>Граница подтверждённой обработки для версии файла и версии извлечения (по всем заданиям).</summary>
    Task<FileProgress> GetFileProgressAsync(long sourceFileId, string extractionVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<JobRecord>> ListJobsAsync(int skip, int take, CancellationToken cancellationToken);

    Task<JobRecord> GetJobAsync(long jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<JobFileRecord>> ListJobFilesAsync(long jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RowErrorRecord>> ListErrorsAsync(long jobFileId, int take, CancellationToken cancellationToken);

    /// <summary>При запуске: задания в состоянии «выполняется» без живого процесса переводятся в «прервано».</summary>
    Task<int> MarkInterruptedAsync(CancellationToken cancellationToken);
}

public sealed class FileProgress
{
    public long ConfirmedOrdinal { get; set; }
    public long Extracted { get; set; }
    public long NoFacts { get; set; }
    public long Errors { get; set; }
    public long Observations { get; set; }
    public bool Completed { get; set; }
}

public interface ISourceFileRepository
{
    Task<SourceFileRow> RegisterAsync(SourceFileRegistration registration, CancellationToken cancellationToken);

    Task MarkFileCompletedAsync(long sourceFileId, string extractionVersion, bool completed, CancellationToken cancellationToken);
}

public interface ISearchRepository
{
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>Точное общее число результатов — отдельный, возможно дорогой запрос.</summary>
    Task<long> CountAsync(SearchQuery query, CancellationToken cancellationToken);

    Task<ObservationDetails> GetObservationAsync(long personFactId, CancellationToken cancellationToken);

    Task<FullTextInfo> GetFullTextInfoAsync(CancellationToken cancellationToken);
}

/// <summary>Доступ к базе с конкретными настройками. Пароль передаётся из хранилища секретов и живёт только в памяти.</summary>
public interface IStorage
{
    ISourceFileRepository SourceFiles { get; }

    IJobRepository Jobs { get; }

    ISearchRepository Search { get; }

    IFactWriter CreateWriter(FieldLimits limits);
}

public interface IStorageFactory
{
    IStorage Create(DatabaseSettings settings, string sqlPassword);
}
