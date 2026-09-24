using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Processing;
using Fakt.Core.Files;
using Fakt.Core.Processing;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Core.Worker;
using Fakt.Infrastructure.Sql;
using Fakt.Infrastructure.Worker;
using Fakt.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Fakt.IntegrationTests;

[CollectionDefinition(PipelineFaultTests.CollectionName, DisableParallelization = true)]
public sealed class PipelineFaultCollection
{
}

/// <summary>
/// Отказы конвейера обработки на настоящих компонентах: ProcessingService/JobSession/FilePipeline, Python worker
/// (open_reader/read_chunk), BatchExtractor + ExtractionValidator, SqlFactWriter и временная база SQL Server.
/// Вместо провайдера LLM — локальный имитатор (он не доказывает работу провайдера). Итог каждого сценария
/// сравнивается с прогоном без отказов: те же строки, наблюдения, факты и индексы, без потерь и дубликатов.
/// </summary>
[Collection(CollectionName)]
[Trait("Category", "SqlServer")]
[Trait("Category", "PythonWorker")]
public sealed class PipelineFaultTests : IClassFixture<SqlServerFixture>, IDisposable
{
    public const string CollectionName = "Отказы конвейера (последовательно)";
    private const int Records = 2000;
    private const int ChunkSize = 250;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _migratedDatabase;
    private static string _baselineDatabase;
    private static StoredFileResults _baseline;

    private readonly SqlServerFixture _db;
    private readonly ITestOutputHelper _output;
    private readonly WorkerLaunchInfo _worker;
    private readonly string _dataDirectory;

    public PipelineFaultTests(SqlServerFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
        _worker = WorkerEnvironment.Resolve();
        _dataDirectory = Path.Combine(Path.GetTempPath(), "fakt-pipeline-fault-tests", Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, true);
        }
        catch (IOException)
        {
            // Синтетический файл ещё открыт завершающимся процессом worker — остаётся во временном каталоге.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------- Общая обвязка ----------

    private sealed class Run
    {
        public InMemorySettingsStore Store { get; set; }
        public LocalLlmSimulator Simulator { get; set; }
        public CountingLogger Logger { get; set; }
        public InstrumentedStorageFactory Storage { get; set; }
        public InstrumentedWorkerFactory Workers { get; set; }
        public ProductHost Host { get; set; }
        public ConcurrentQueue<FileStatusEvent> Events { get; } = new();
    }

    private static ProcessingSettings Processing() => new()
    {
        ChunkSize = ChunkSize,
        SqlBatchSize = 500,
        QueueCapacity = 8,
        MaxAttemptsPerRequest = 6,
        BudgetMaxRequests = 0,
    };

    /// <summary>Новый экземпляр приложения (как после запуска FAKT) над общими настройками и той же базой.</summary>
    private Run NewRun(InMemorySettingsStore store = null, LlmSimulatorOptions simulator = null)
    {
        var run = new Run
        {
            Store = store ?? ProductHost.CreateSettings(_db.Settings, Processing(), ProductHost.SimulatorProfile(batchRows: 10, maxConcurrentRequests: 2)),
            Simulator = new LocalLlmSimulator(simulator),
            Logger = new CountingLogger(),
        };
        run.Host = new ProductHost(run.Store, run.Simulator, _worker, run.Logger,
            inner => run.Storage = new InstrumentedStorageFactory(inner),
            inner => run.Workers = new InstrumentedWorkerFactory(inner));
        return run;
    }

    private void Attach(Run run, JobSession session) => session.FileChanged += e => run.Events.Enqueue(e);

    private async Task EnsureMigratedAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_migratedDatabase != _db.Database)
            {
                await _db.ApplyAllMigrationsAsync(_db.Settings);
                _migratedDatabase = _db.Database;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private string NewFile(string name)
    {
        var path = Path.Combine(_dataDirectory, name + ".csv");
        SyntheticPeople.WriteCsv(path, Records);
        return path;
    }

    private async Task<JobSession> CreateJobAsync(Run run, string path)
    {
        await EnsureMigratedAsync();
        var input = await run.Host.PrepareInputAsync(path, SyntheticPeople.CsvStructure(), CancellationToken.None);
        var session = await run.Host.Processing.CreateJobAsync(new[] { input }, CancellationToken.None);
        Attach(run, session);
        return session;
    }

    private static async Task<JobFileRecord> JobFileAsync(Run run, long jobId) =>
        (await run.Host.OpenStorage().Jobs.ListJobFilesAsync(jobId, CancellationToken.None)).Single();

    private Task<StoredFileResults> ResultsAsync(JobFileRecord file, bool hash = true) =>
        StoredFileResults.ReadAsync(_db.Settings, file.SourceFileId, file.ExtractionVersion, hash, CancellationToken.None);

    private async Task<StoredFileResults> BaselineAsync()
    {
        await EnsureMigratedAsync();
        await Gate.WaitAsync();
        try
        {
            if (_baseline != null && _baselineDatabase == _db.Database)
            {
                return _baseline;
            }
        }
        finally
        {
            Gate.Release();
        }

        var run = NewRun();
        var session = await CreateJobAsync(run, NewFile("baseline_clean"));
        var status = await session.RunAsync();
        Assert.Equal(JobStatus.Completed, status);
        var baseline = await ResultsAsync(await JobFileAsync(run, session.JobId));
        _output.WriteLine("Эталон (прогон без отказов): " + baseline);
        Assert.True(baseline.Completed);
        Assert.Equal(Records, baseline.ConfirmedOrdinal);
        Assert.True(baseline.IsContiguousUpToCheckpoint, baseline.ToString());
        Assert.Equal(Records, baseline.OutcomeExtracted);
        Assert.Equal(Records, baseline.PersonFacts);
        Assert.Equal(baseline.PersonFacts, baseline.SearchDocs);
        Assert.Equal(3L * Records, baseline.FactValues);
        Assert.Equal(0, baseline.RowErrors);
        Assert.Equal(0, baseline.DuplicateObservationKeys);
        Assert.Equal(0, baseline.IdOrderInversions);
        await Gate.WaitAsync();
        try
        {
            _baseline = baseline;
            _baselineDatabase = _db.Database;
        }
        finally
        {
            Gate.Release();
        }

        return baseline;
    }

    /// <summary>Итог совпадает с прогоном без отказов: те же строки и содержимое, без потерь и дубликатов.</summary>
    private void AssertSameAsBaseline(StoredFileResults baseline, StoredFileResults actual)
    {
        _output.WriteLine("Итог сценария: " + actual);
        Assert.True(actual.Completed, "Файл должен быть отмечен завершённым: " + actual);
        Assert.Equal(Records, actual.ConfirmedOrdinal);
        Assert.True(actual.IsContiguousUpToCheckpoint, "Реестр строк должен быть непрерывным 1..N: " + actual);
        Assert.Equal(0, actual.DuplicateObservationKeys);
        Assert.Equal(0, actual.IdOrderInversions);
        Assert.Equal(baseline.OutcomeExtracted, actual.OutcomeExtracted);
        Assert.Equal(baseline.OutcomeNoFacts, actual.OutcomeNoFacts);
        Assert.Equal(baseline.OutcomeErrors, actual.OutcomeErrors);
        Assert.Equal(baseline.PersonFacts, actual.PersonFacts);
        Assert.Equal(baseline.SearchDocs, actual.SearchDocs);
        Assert.Equal(baseline.FactValues, actual.FactValues);
        Assert.Equal(baseline.RowErrors, actual.RowErrors);
        // Счётчики checkpoint совпадают с фактическим содержимым реестра.
        Assert.Equal(actual.OutcomeExtracted, actual.ProgressExtracted);
        Assert.Equal(actual.OutcomeNoFacts, actual.ProgressNoFacts);
        Assert.Equal(actual.OutcomeErrors, actual.ProgressErrors);
        Assert.Equal(actual.PersonFacts, actual.ProgressObservations);
        Assert.Equal(baseline.ContentHash, actual.ContentHash);
    }

    private string KillerConnectionString(string applicationName) =>
        new SqlConnectionStringBuilder(SqlConnectionFactory.Build(_db.Settings, null)) { ApplicationName = applicationName, Pooling = false }.ConnectionString;

    private async Task KillSessionAsync(int sessionId)
    {
        using var connection = new SqlConnection(KillerConnectionString("FAKT-test-killer"));
        await connection.OpenAsync();
        using var command = new SqlCommand("KILL " + sessionId.ToString(CultureInfo.InvariantCulture) + ";", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// SPID соединения, которое SqlFactWriter держит между фиксациями (временные таблицы живут в этом сеансе).
    /// Вызывается между фиксациями, когда соединение писателя свободно.
    /// </summary>
    private static async Task<int> WriterSessionIdAsync(IFactWriter writer)
    {
        var field = typeof(SqlFactWriter).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("У SqlFactWriter нет поля _connection.");
        var connection = (SqlConnection)field.GetValue(writer) ?? throw new InvalidOperationException("Писатель ещё не открыл соединение.");
        using var command = new SqlCommand("SELECT @@SPID;", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Эксклюзивная блокировка таблицы из отдельного сеанса: следующая фиксация останавливается внутри своей
    /// транзакции (BEGIN TRAN и загрузка во временные таблицы уже выполнены). Затем этот сеанс писателя
    /// принудительно завершается (KILL) — разрыв соединения посреди фиксации, — и блокировка снимается.
    /// </summary>
    private sealed class SqlLockBlocker
    {
        private readonly string _connectionString;
        private SqlConnection _connection;
        private SqlTransaction _transaction;

        private SqlLockBlocker(string connectionString)
        {
            _connectionString = connectionString;
        }

        public int SessionId { get; private set; }

        public static async Task<SqlLockBlocker> AcquireAsync(string connectionString, string table)
        {
            var blocker = new SqlLockBlocker(connectionString) { _connection = new SqlConnection(connectionString) };
            await blocker._connection.OpenAsync();
            using (var spid = new SqlCommand("SELECT @@SPID;", blocker._connection))
            {
                blocker.SessionId = Convert.ToInt32(await spid.ExecuteScalarAsync());
            }

            blocker._transaction = blocker._connection.BeginTransaction();
            using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table} WITH (TABLOCKX, HOLDLOCK);", blocker._connection, blocker._transaction);
            await command.ExecuteScalarAsync();
            return blocker;
        }

        /// <summary>Возвращает SPID завершённого сеанса (0 — никто не был заблокирован за отведённое время).</summary>
        public async Task<int> KillBlockedSessionAndReleaseAsync(TimeSpan timeout)
        {
            var victim = 0;
            try
            {
                using var monitor = new SqlConnection(_connectionString);
                await monitor.OpenAsync();
                var deadline = DateTime.UtcNow + timeout;
                while (victim == 0 && DateTime.UtcNow < deadline)
                {
                    using var find = new SqlCommand("SELECT TOP (1) session_id FROM sys.dm_exec_requests WHERE blocking_session_id = @blocker;", monitor);
                    find.Parameters.Add("@blocker", SqlDbType.Int).Value = SessionId;
                    victim = Convert.ToInt32(await find.ExecuteScalarAsync() ?? 0);
                    if (victim == 0)
                    {
                        await Task.Delay(25);
                    }
                }

                if (victim != 0)
                {
                    using var kill = new SqlCommand("KILL " + victim.ToString(CultureInfo.InvariantCulture) + ";", monitor);
                    await kill.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                _transaction.Rollback();
                _connection.Dispose();
            }

            return victim;
        }
    }

    private static PipelineProgress FinalProgress(Run run, FileStatus status) =>
        run.Events.Where(e => e.Status == status && e.Progress != null).Select(e => e.Progress).LastOrDefault()
        ?? throw new InvalidOperationException("Нет события со статусом " + status);

    /// <summary>«Перезапуск приложения»: новые экземпляры сервисов над теми же настройками и базой, продолжение задания.</summary>
    private async Task<(Run Restart, JobSession Session)> ResumeAfterRestartAsync(Run previous, long jobId)
    {
        SqlConnection.ClearAllPools();
        var restart = NewRun(previous.Store);
        var resumed = await restart.Host.Processing.ResumeJobAsync(jobId, CancellationToken.None);
        Attach(restart, resumed);
        return (restart, resumed);
    }

    // ---------- Сценарии ----------

    [Fact]
    public async Task CleanRun_ProcessesEveryRecordExactlyOnce()
    {
        var baseline = await BaselineAsync();
        Assert.Equal(64, baseline.ContentHash.Length);
    }

    [Theory]
    [InlineData("between_requests")]
    [InlineData("request_in_flight")]
    public async Task WorkerProcessKilledMidFile_ErrorIsReported_ResumeAfterRestartCompletesWithoutLossOrDuplicates(string moment)
    {
        var baseline = await BaselineAsync();
        var run = NewRun();
        var killed = 0;

        Task Kill(WorkerCallContext context)
        {
            if (context.CallIndex == 3 && context.Process != null && Interlocked.Exchange(ref killed, 1) == 0)
            {
                context.Process.Kill();
                Assert.True(context.Process.WaitForExit(10000), "процесс worker должен завершиться");
            }

            return Task.CompletedTask;
        }

        if (moment == "between_requests")
        {
            // Процесс worker падает между двумя read_chunk (после двух прочитанных пакетов).
            run.Workers.BeforeReadChunk = Kill;
        }
        else
        {
            // Третий read_chunk уже отправлен, ответ ещё не получен.
            run.Workers.AfterReadChunkSent = Kill;
        }

        var session = await CreateJobAsync(run, NewFile("worker_killed_" + moment));
        var status = await session.RunAsync();

        // Ошибка видна пользователю: задание остановлено, файл — «Ошибка» с причиной; записи не помечены обработанными.
        Assert.Equal(1, killed);
        _output.WriteLine($"Задание: {status}; сообщение: {session.StatusMessage}");
        Assert.Equal(JobStatus.Failed, status);
        Assert.Contains("worker", session.StatusMessage, StringComparison.OrdinalIgnoreCase);
        var file = await JobFileAsync(run, session.JobId);
        Assert.Equal(FileStatus.Error, file.Status);
        Assert.Contains("worker", file.LastError, StringComparison.OrdinalIgnoreCase);
        var job = await run.Host.OpenStorage().Jobs.GetJobAsync(session.JobId, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, job.Status);
        var afterCrash = await ResultsAsync(file, hash: false);
        _output.WriteLine("После аварии worker: " + afterCrash);
        Assert.False(afterCrash.Completed);
        Assert.InRange(afterCrash.ConfirmedOrdinal, 0, Records - 1);
        Assert.True(afterCrash.IsContiguousUpToCheckpoint, afterCrash.ToString());
        Assert.Equal(0, afterCrash.DuplicateObservationKeys);

        // «Перезапуск приложения»: новые экземпляры сервисов и процесса worker, продолжение задания.
        var (restart, resumed) = await ResumeAfterRestartAsync(run, session.JobId);
        Assert.Equal(JobStatus.Completed, await resumed.RunAsync());
        var progress = FinalProgress(restart, FileStatus.Completed);
        // Продолжение, а не повтор с начала: записи до границы разобраны и пропущены worker.
        Assert.Equal(afterCrash.ConfirmedOrdinal, progress.RecordsSkipped);
        Assert.Equal(Records - afterCrash.ConfirmedOrdinal, progress.RecordsRead);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("tcp")]
    public async Task SqlConnectionKilledInsideCommitTransaction_CommitIsRetried_NoLossNoDuplicates(string transport)
    {
        var baseline = await BaselineAsync();
        var database = _db.NewSettings();
        if (transport == "tcp")
        {
            database.Server = "tcp:" + _db.Server;
        }

        var run = NewRun(ProductHost.CreateSettings(database, Processing(), ProductHost.SimulatorProfile(batchRows: 10, maxConcurrentRequests: 2)));
        SqlLockBlocker blocker = null;
        Task<int> killer = null;
        var commitLog = new SqlNames(_db.Settings).Aux("FaktCommitLog");
        run.Storage.AfterCommit = async context =>
        {
            if (context.Index == 2 && blocker == null)
            {
                blocker = await SqlLockBlocker.AcquireAsync(KillerConnectionString("FAKT-test-blocker"), commitLog);
                killer = blocker.KillBlockedSessionAndReleaseAsync(TimeSpan.FromSeconds(60));
            }
        };
        var session = await CreateJobAsync(run, NewFile("sql_killed_in_transaction_" + transport));
        var status = await session.RunAsync();
        var victim = killer == null ? 0 : await killer;

        _output.WriteLine($"Завершён сеанс {victim}; задание: {status}; {session.StatusMessage}");
        foreach (var warning in run.Logger.RecentWarnings)
        {
            _output.WriteLine($"{warning.Level} {warning.Event}: {warning.Message}");
        }

        Assert.True(victim > 0, "Фиксация должна была остановиться на блокировке и быть прервана");
        Assert.Equal(JobStatus.Completed, status);
        Assert.Equal(1, run.Storage.Stats.Failed);
        Assert.Equal(1, run.Logger.Count(Fakt.Core.Logging.LogLevel.Warning, "pipeline.sql_retry"));
        // Прерванная транзакция откатилась целиком: повтор — обычная фиксация, а не распознанный дубликат.
        Assert.Equal(0, run.Storage.Stats.Duplicates);
        var file = await JobFileAsync(run, session.JobId);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
        Assert.Equal(run.Storage.Stats.Succeeded, Convert.ToInt64(await _db.ScalarAsync($"SELECT COUNT_BIG(*) FROM {commitLog} WHERE [JobFileId] = {file.JobFileId}")));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("tcp")]
    public async Task SqlConnectionKilledWhileWriterIdle_NextCommitReconnects_NoLossNoDuplicates(string transport)
    {
        var baseline = await BaselineAsync();
        var database = _db.NewSettings();
        if (transport == "tcp")
        {
            // Явный TCP (как у клиента, подключённого к удалённому серверу), а не Shared Memory для localhost.
            database.Server = "tcp:" + _db.Server;
        }

        var run = NewRun(ProductHost.CreateSettings(database, Processing(), ProductHost.SimulatorProfile(batchRows: 10, maxConcurrentRequests: 2)));
        var victim = 0;
        run.Storage.AfterCommit = async context =>
        {
            // Сеанс писателя простаивает между фиксациями (временные таблицы остаются в нём) и обрывается сервером.
            if (context.Index == 2 && victim == 0)
            {
                victim = await WriterSessionIdAsync(context.Inner);
                await KillSessionAsync(victim);
            }
        };
        var session = await CreateJobAsync(run, NewFile("sql_killed_idle_" + transport));
        var status = await session.RunAsync();

        _output.WriteLine($"Завершён сеанс {victim}; задание: {status}; {session.StatusMessage}");
        foreach (var warning in run.Logger.RecentWarnings)
        {
            _output.WriteLine($"{warning.Level} {warning.Event}: {warning.Message}");
        }

        Assert.True(victim > 0);
        Assert.Equal(JobStatus.Completed, status);
        Assert.True(run.Storage.Stats.Failed >= 1, "Обрыв соединения должен проявиться ошибкой фиксации");
        Assert.Equal(run.Storage.Stats.Failed, run.Logger.Count(Fakt.Core.Logging.LogLevel.Warning, "pipeline.sql_retry"));
        Assert.Equal(0, run.Storage.Stats.Duplicates);
        var file = await JobFileAsync(run, session.JobId);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
    }

    [Fact]
    public async Task DatabaseUnavailableLongerThanRetries_ProcessingStops_ResumeAfterRecoveryWithoutDuplicates()
    {
        var baseline = await BaselineAsync();
        var run = NewRun();
        var master = new SqlConnectionStringBuilder(KillerConnectionString("FAKT-test-outage")) { InitialCatalog = "master" }.ConnectionString;
        var database = SqlNames.Quote(_db.Database);
        var outage = 0;
        run.Storage.AfterCommit = async context =>
        {
            if (context.Index == 2 && Interlocked.Exchange(ref outage, 1) == 0)
            {
                // База становится недоступной для записи: сеансы разорваны, а после переподключения любая запись отклоняется.
                using var connection = new SqlConnection(master);
                await connection.OpenAsync();
                using var command = new SqlCommand($"ALTER DATABASE {database} SET READ_ONLY WITH ROLLBACK IMMEDIATE;", connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync();
            }
        };

        var session = await CreateJobAsync(run, NewFile("database_outage"));
        JobStatus? status = null;
        Exception thrown = null;
        try
        {
            try
            {
                status = await session.RunAsync();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            _output.WriteLine(thrown == null
                ? $"Задание вернуло статус {status}: {session.StatusMessage}"
                : $"RunAsync завершился исключением {thrown.GetType().Name}: {thrown.Message} (Status сеанса: {session.Status}, {session.StatusMessage})");
            foreach (var warning in run.Logger.RecentWarnings)
            {
                _output.WriteLine($"{warning.Level} {warning.Event}: {warning.Message}");
            }

            // Обработка не продолжается «вслепую»: неудачная запись останавливает конвейер.
            Assert.Equal(1, outage);
            Assert.NotEqual(JobStatus.Completed, status);
            Assert.True(run.Storage.Stats.Failed >= 1);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            using var command = new SqlCommand($"ALTER DATABASE {database} SET READ_WRITE WITH ROLLBACK IMMEDIATE;", connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }

        // База снова доступна: сохранённое до сбоя не потеряно, продолжение — с подтверждённой границы.
        var file = await JobFileAsync(run, session.JobId);
        var afterOutage = await ResultsAsync(file, hash: false);
        var jobAfterOutage = await run.Host.OpenStorage().Jobs.GetJobAsync(session.JobId, CancellationToken.None);
        _output.WriteLine($"После сбоя базы: задание в базе — {jobAfterOutage.Status}, файл — {file.Status}; " + afterOutage);
        Assert.False(afterOutage.Completed);
        Assert.True(afterOutage.ConfirmedOrdinal > 0 && afterOutage.ConfirmedOrdinal < Records, afterOutage.ToString());
        Assert.True(afterOutage.IsContiguousUpToCheckpoint, afterOutage.ToString());

        var (restart, resumed) = await ResumeAfterRestartAsync(run, session.JobId);
        Assert.Equal(JobStatus.Completed, await resumed.RunAsync());
        Assert.Equal(afterOutage.ConfirmedOrdinal, FinalProgress(restart, FileStatus.Completed).RecordsSkipped);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
    }

    [Fact]
    public async Task AcknowledgementLostAfterSqlCommit_RetryOfSameCommitIsRecognized_NoDuplicates()
    {
        var baseline = await BaselineAsync();
        var run = NewRun();
        var commits = 0;
        run.Storage.WriterCreated = writer => ((SqlFactWriter)writer).AfterCommitHook = () =>
        {
            if (Interlocked.Increment(ref commits) == 3)
            {
                throw new IOException("Имитация: соединение разорвано после COMMIT, подтверждение фиксации не получено.");
            }
        };
        var session = await CreateJobAsync(run, NewFile("lost_ack"));
        var status = await session.RunAsync();

        Assert.Equal(JobStatus.Completed, status);
        Assert.Equal(1, run.Logger.Count(Fakt.Core.Logging.LogLevel.Warning, "pipeline.sql_retry"));
        // Повтор той же единицы фиксации распознан по CommitId: данные и счётчики не изменены повторно.
        Assert.Equal(1, run.Storage.Stats.Duplicates);
        var file = await JobFileAsync(run, session.JobId);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));

        var reported = FinalProgress(run, FileStatus.Completed);
        _output.WriteLine($"Прогресс в памяти после повтора-дубликата: committed={reported.Committed}, extracted={reported.Extracted}, " +
                          $"no_facts={reported.NoFacts}, errors={reported.Errors}, observations={reported.Observations} (в базе: {Records} записей)");
    }

    [Fact]
    public async Task CrashAfterSqlCommitBeforeAcknowledgement_RestartContinuesAfterCheckpoint_ReplayIsNoOp()
    {
        var baseline = await BaselineAsync();
        var run = NewRun();
        var commits = 0;
        CommitUnit unacknowledged = null;
        run.Storage.BeforeCommit = context =>
        {
            if (context.Index == 3)
            {
                unacknowledged = context.Unit;
            }

            return Task.CompletedTask;
        };
        run.Storage.WriterCreated = writer => ((SqlFactWriter)writer).AfterCommitHook = () =>
        {
            if (Interlocked.Increment(ref commits) == 3)
            {
                throw new InvalidOperationException("Имитация аварии процесса сразу после COMMIT (до получения подтверждения).");
            }
        };
        var session = await CreateJobAsync(run, NewFile("crash_after_commit"));
        var status = await session.RunAsync();
        _output.WriteLine($"Задание после аварии: {status}; {session.StatusMessage}");
        Assert.NotEqual(JobStatus.Completed, status);
        Assert.NotNull(unacknowledged);

        // Неподтверждённая фиксация сохранена вместе со своей границей (одна транзакция).
        var file = await JobFileAsync(run, session.JobId);
        var afterCrash = await ResultsAsync(file, hash: false);
        _output.WriteLine("После аварии: " + afterCrash);
        Assert.Equal(unacknowledged.ToOrdinal, afterCrash.ConfirmedOrdinal);
        Assert.True(afterCrash.IsContiguousUpToCheckpoint, afterCrash.ToString());
        Assert.False(afterCrash.Completed);

        // Настоящая авария оставила бы задание в состоянии «Выполняется».
        await _db.ExecuteAsync($"UPDATE [dbo].[FaktJobs] SET [Status] = 'Running', [FinishedAtUtc] = NULL WHERE [JobId] = {session.JobId}; " +
                               $"UPDATE [dbo].[FaktJobFiles] SET [Status] = 'Processing' WHERE [JobFileId] = {file.JobFileId};");

        // Перезапуск: повтор неподтверждённой фиксации (тот же CommitId) ничего не меняет.
        SqlConnection.ClearAllPools();
        var restart = NewRun(run.Store);
        using (var writer = restart.Host.OpenStorage().CreateWriter(new Fakt.Core.Extraction.FieldLimits()))
        {
            var replay = await writer.CommitAsync(unacknowledged, CancellationToken.None);
            Assert.True(replay.Duplicate);
            Assert.Equal(afterCrash.ConfirmedOrdinal, replay.ConfirmedOrdinal);
        }

        var afterReplay = await ResultsAsync(file, hash: false);
        Assert.Equal(afterCrash.RowOutcomes, afterReplay.RowOutcomes);
        Assert.Equal(afterCrash.PersonFacts, afterReplay.PersonFacts);
        Assert.Equal(afterCrash.ProgressExtracted, afterReplay.ProgressExtracted);

        // Продолжение задания начинается после подтверждённой границы.
        var (restartAfterCrash, resumed) = await ResumeAfterRestartAsync(run, session.JobId);
        Assert.Equal(JobStatus.Completed, await resumed.RunAsync());
        Assert.Equal(afterCrash.ConfirmedOrdinal, FinalProgress(restartAfterCrash, FileStatus.Completed).RecordsSkipped);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
    }

    [Fact]
    public async Task PauseAndResume_TwiceIncludingAppRestart_NoDuplicatesAndResultsIdenticalToCleanRun()
    {
        var baseline = await BaselineAsync();
        var run = NewRun(simulator: new LlmSimulatorOptions { BaseLatencyMs = 5 });
        var session = await CreateJobAsync(run, NewFile("pause_resume"));

        async Task<StoredFileResults> PauseMidFileAsync(long pauseAtCommitted, long holdAbove)
        {
            // Ответы для записей дальше holdAbove задерживаются до момента паузы (+0,6 с — дольше периода опроса паузы 0,2 с),
            // поэтому пауза гарантированно приходится на середину файла.
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = 0;
            run.Simulator.BeforeRespond = info => info.Ordinals.Max() > holdAbove ? release.Task : Task.CompletedTask;
            void OnProgress(FileStatusEvent e)
            {
                if (e.Progress != null && e.Progress.Committed >= pauseAtCommitted && Interlocked.Exchange(ref requested, 1) == 0)
                {
                    session.Pause();
                    _ = Task.Delay(600).ContinueWith(_ => release.TrySetResult(true), TaskScheduler.Default);
                }
            }

            session.FileChanged += OnProgress;
            try
            {
                var status = await session.RunAsync();
                Assert.Equal(JobStatus.Paused, status);
            }
            finally
            {
                session.FileChanged -= OnProgress;
                release.TrySetResult(true);
                run.Simulator.BeforeRespond = null;
            }

            var paused = await ResultsAsync(await JobFileAsync(run, session.JobId), hash: false);
            _output.WriteLine($"Пауза при {pauseAtCommitted}: " + paused);
            Assert.False(paused.Completed);
            Assert.InRange(paused.ConfirmedOrdinal, pauseAtCommitted, Records - 1);
            Assert.True(paused.IsContiguousUpToCheckpoint, paused.ToString());
            Assert.Equal(0, paused.DuplicateObservationKeys);
            return paused;
        }

        var first = await PauseMidFileAsync(400, 800);
        // Продолжение в том же сеансе приложения и вторая пауза.
        var second = await PauseMidFileAsync(first.ConfirmedOrdinal + 400, first.ConfirmedOrdinal + 800);
        Assert.True(second.ConfirmedOrdinal > first.ConfirmedOrdinal);
        var file = await JobFileAsync(run, session.JobId);
        Assert.Equal(FileStatus.Paused, file.Status);

        // Продолжение после перезапуска приложения.
        var (restart, resumed) = await ResumeAfterRestartAsync(run, session.JobId);
        Assert.Equal(JobStatus.Completed, await resumed.RunAsync());
        Assert.Equal(second.ConfirmedOrdinal, FinalProgress(restart, FileStatus.Completed).RecordsSkipped);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
        _output.WriteLine($"Запросов к имитатору: {run.Simulator.Stats.Calls} + {restart.Simulator.Stats.Calls} после перезапуска " +
                          $"(записей в запросах: {run.Simulator.Stats.RecordsSeen + restart.Simulator.Stats.RecordsSeen} на {Records} записей файла)");
        // Политика паузы: начатые пакеты завершаются и сохраняются, невыданные не отправляются — модель не получает запись дважды.
        Assert.Equal(Records, run.Simulator.Stats.RecordsSeen + restart.Simulator.Stats.RecordsSeen);
    }

    [Fact]
    public async Task LlmFaults_429_503_Timeout_InvalidJson_Truncation_MissingDuplicateUnknownIds_EveryRecordSavedOnce()
    {
        var baseline = await BaselineAsync();
        var options = new LlmSimulatorOptions
        {
            ScheduledFaults =
            {
                SimulatedFault.RateLimited, SimulatedFault.InvalidJson, SimulatedFault.MissingId, SimulatedFault.ServerError, SimulatedFault.DuplicateId,
                SimulatedFault.Timeout, SimulatedFault.UnknownId, SimulatedFault.LengthTruncation,
            },
            RateLimitRate = 0.04,
            InvalidJsonRate = 0.03,
            MissingIdRate = 0.03,
            DuplicateIdRate = 0.02,
            UnknownIdRate = 0.02,
            RetryAfterMs = 50,
        };
        var run = NewRun(simulator: options);
        var session = await CreateJobAsync(run, NewFile("llm_faults"));
        var status = await session.RunAsync();

        var stats = run.Simulator.Stats;
        _output.WriteLine($"Имитатор: запросов {stats.Calls}, ошибок {stats.TotalFaults}: " + string.Join(", ", stats.FaultCounts().Select(p => p.Key + "=" + p.Value)));
        foreach (var pair in run.Logger.Counts.OrderBy(p => p.Key))
        {
            _output.WriteLine($"{pair.Key}: {pair.Value}");
        }

        Assert.Equal(JobStatus.Completed, status);
        foreach (var fault in options.ScheduledFaults.Distinct())
        {
            Assert.True(stats.Faults(fault) >= 1, "Не внедрена ошибка " + fault);
        }

        Assert.True(run.Logger.Count(Fakt.Core.Logging.LogLevel.Warning, "llm.retry") >= 3, "429, 503 и тайм-аут должны повторяться");
        Assert.True(run.Logger.Count(Fakt.Core.Logging.LogLevel.Warning, "extraction.invalid_response") >= 1);
        var file = await JobFileAsync(run, session.JobId);
        AssertSameAsBaseline(baseline, await ResultsAsync(file));
    }
}
