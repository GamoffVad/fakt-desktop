using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Processing;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Processing;
using Fakt.Core.Records;
using Fakt.Core.Search;
using Fakt.Core.Settings;
using Fakt.Core.Structure;
using Fakt.Core.Worker;
using Fakt.Infrastructure.Sql;
using Fakt.Infrastructure.Worker;
using Fakt.Testing;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Fakt.LoadTest;

/// <summary>
/// Прогон нагрузочного теста: файл → только чтение worker → полный конвейер приложения → проверка SQL →
/// полнотекстовый индекс и поиск → отчёт. Все компоненты, кроме провайдера LLM, — настоящие классы приложения.
/// </summary>
public sealed class LoadTestRunner
{
    private static readonly int[] FixedWidths = { 10, 45, 14, 20, 20, 40, 22 };
    private static readonly string[] Columns = { "Номер", "ФИО", "Дата рождения", "Место рождения", "Телефон", "Email", "Номер счёта" };

    private readonly LoadTestOptions _options;
    private readonly LoadTestReport _report = new();
    private readonly string _repositoryRoot;
    private WorkerLaunchInfo _worker;
    private ThrowawayDatabase _database;
    private string _dataPath;
    private long _progressRead;
    private long _progressCommitted;
    private readonly CancellationTokenSource _stop = new();
    private volatile JobSession _session;

    public LoadTestRunner(LoadTestOptions options)
    {
        _options = options;
        _repositoryRoot = LoadTestOptions.FindRepositoryRoot();
    }

    /// <summary>Ctrl+C: задание останавливается штатно, оставшиеся этапы пропускаются, временная база удаляется.</summary>
    public void RequestStop()
    {
        Log("Получен запрос остановки: задание останавливается, временная база будет удалена.");
        _stop.Cancel();
        _session?.Stop();
    }

    public async Task<int> RunAsync()
    {
        _report.StartedAtUtc = DateTime.UtcNow;
        _report.CommandLine = _options.CommandLine;
        _report.Notes.Add("LLM заменён локальным детерминированным имитатором (Fakt.Testing.LocalLlmSimulator): он нагружает конвейер, но не является провайдером и не доказывает работу реальных API.");
        _report.Notes.Add("Замеры выполнены на Windows 11 (современная ОС). Целевая платформа Windows 7 SP1 x64 недоступна — совместимость и производительность на ней этим прогоном не проверены.");
        _report.Notes.Add("SQL Server работает на той же машине, что и клиент: сетевые задержки клиент–сервер не учтены.");
        var cancellation = _stop.Token;
        try
        {
            Log("Окружение…");
            _worker = WorkerEnvironment.Resolve(_options.Python);
            await CollectEnvironmentAsync(cancellation).ConfigureAwait(false);
            FillSettings();
            await PrepareDataAsync().ConfigureAwait(false);

            Log($"Временная база на {_options.Server}…");
            _database = await ThrowawayDatabase.CreateAsync(_options.Server, cancellation).ConfigureAwait(false);
            _report.Environment.Database = _database.Name;
            await ApplyMigrationsAsync(cancellation).ConfigureAwait(false);
            await CollectSqlEnvironmentAsync().ConfigureAwait(false);

            var structure = StructureFor(_options.Format);
            if (!_options.SkipParseOnly)
            {
                await RunParseOnlyAsync(structure, cancellation).ConfigureAwait(false);
            }

            var (host, session) = await RunPipelineAsync(structure, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await VerifyAsync(host, session, cancellation).ConfigureAwait(false);
            if (!_options.SkipSearch && _report.Pipeline?.JobStatus == JobStatus.Completed.ToString())
            {
                await RunSearchAsync(host, session, cancellation).ConfigureAwait(false);
            }

            _report.Success = _report.Pipeline?.JobStatus == JobStatus.Completed.ToString() && _report.Verification.Count > 0 && _report.Verification.All(c => c.Passed);
        }
        catch (Exception ex)
        {
            _report.Error = ex.ToString();
            Log("ОШИБКА: " + ex.Message);
        }
        finally
        {
            if (_database != null)
            {
                if (_options.KeepDatabase)
                {
                    Log($"База {_database.Name} сохранена (--keep-db).");
                }
                else
                {
                    try
                    {
                        await _database.DropAsync().ConfigureAwait(false);
                        Log($"База {_database.Name} удалена.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Не удалось удалить базу {_database.Name}: {ex.Message}");
                        _report.Notes.Add($"Временная база {_database.Name} не удалена: {ex.Message}");
                    }
                }
            }

            _report.FinishedAtUtc = DateTime.UtcNow;
            WriteReports();
        }

        Log(_report.Success ? "ИТОГ: прогон успешен, все проверки пройдены." : "ИТОГ: прогон НЕ успешен — см. отчёт.");
        return _report.Success ? 0 : 1;
    }

    // ---------- Окружение и данные ----------

    private async Task CollectEnvironmentAsync(CancellationToken cancellationToken)
    {
        var env = _report.Environment;
        env.Os = OsDescription();
        env.Cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        env.Cpu = env.Cpu?.Trim();
        env.LogicalProcessors = Environment.ProcessorCount;
        env.TotalRamGb = Math.Round(TotalPhysicalMemory() / (1024.0 * 1024 * 1024), 1);
        var release = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null);
        env.DotNetRuntime = RuntimeInformation.FrameworkDescription + (release != null ? " (Release " + release + ")" : string.Empty);
        env.Is64BitProcess = Environment.Is64BitProcess;
        env.GcMode = (GCSettings.IsServerGC ? "server" : "workstation") + ", " + GCSettings.LatencyMode;
        env.PythonPath = Relative(_worker.PythonPath);
        env.WorkerDirectory = Relative(_worker.WorkerDirectory);
        var hello = await new WorkerClientFactory(() => _worker, null).ProbeAsync(cancellationToken).ConfigureAwait(false);
        env.WorkerVersion = hello.WorkerVersion;
        env.PythonVersion = hello.PythonVersion;
        env.PandasVersion = hello.PandasVersion;
        env.NumpyVersion = hello.NumpyVersion;
        env.WorkerPlatform = hello.Platform;
        env.SqlServer = _options.Server;
    }

    private async Task CollectSqlEnvironmentAsync()
    {
        using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = new SqlCommand(@"
SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)), CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128)),
       (SELECT net_transport FROM sys.dm_exec_connections WHERE session_id = @@SPID),
       (SELECT recovery_model_desc FROM sys.databases WHERE name = DB_NAME());", connection);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            _report.Environment.SqlServerVersion = reader.GetString(0);
            _report.Environment.SqlServerEdition = reader.GetString(1);
            _report.Environment.SqlTransport = reader.IsDBNull(2) ? null : reader.GetString(2);
            _report.Environment.DatabaseRecoveryModel = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
    }

    private void FillSettings()
    {
        var s = _report.Settings;
        s.Records = _options.Records;
        s.Format = _options.Format;
        s.ChunkSize = _options.ChunkSize;
        s.BatchRows = _options.BatchRows;
        s.Concurrency = _options.Concurrency;
        s.QueueCapacity = _options.QueueCapacity;
        s.SqlBatchSize = _options.SqlBatchSize;
        s.MaxInputTokens = _options.MaxInputTokens;
        s.Simulator = SimulatorOptions().Describe();
    }

    private LlmSimulatorOptions SimulatorOptions() => new()
    {
        BaseLatencyMs = _options.LatencyMs,
        PerRecordLatencyMs = _options.PerRecordLatencyMs,
        JitterMs = _options.JitterMs,
        RateLimitRate = _options.Fault429,
        ServerErrorRate = _options.Fault503,
        TimeoutRate = _options.FaultTimeout,
        InvalidJsonRate = _options.FaultInvalidJson,
        MissingIdRate = _options.FaultMissingId,
        DuplicateIdRate = _options.FaultDuplicateId,
        UnknownIdRate = _options.FaultUnknownId,
        RetryAfterMs = 100,
    };

    private async Task PrepareDataAsync()
    {
        var directory = _options.DataDirectory ?? Path.Combine(_repositoryRoot, "testdata", "large");
        var extension = _options.Format == "csv" ? "csv" : _options.Format == "fixed" ? "txt" : "jsonl";
        _dataPath = Path.GetFullPath(Path.Combine(directory, $"synthetic_{_options.Records}.{extension}"));
        var data = _report.Data;
        if (_options.Regenerate || !File.Exists(_dataPath))
        {
            var python = _options.GeneratorPython ?? _worker.PythonPath;
            var script = Path.Combine(_repositoryRoot, "tools", "testdata", "generate_testdata.py");
            // Файл создаётся во временном каталоге и переносится на место только после успешного завершения
            // генератора: прерванная генерация не оставит неполный файл, который следующий прогон принял бы за готовый.
            var staging = Path.Combine(directory, ".generating-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);
            var arguments = $"-X utf8 \"{script}\" --large {_options.Records.ToString(CultureInfo.InvariantCulture)} --large-dir \"{staging}\" --large-format {_options.Format}";
            Log($"Генерация синтетического файла: {_options.Records:N0} записей ({_options.Format})…");
            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(new ProcessStartInfo(python, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }) ?? throw new InvalidOperationException("Не удалось запустить генератор данных.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Генератор завершился с кодом {process.ExitCode}: {await error.ConfigureAwait(false)} {await output.ConfigureAwait(false)}");
            }

            if (File.Exists(_dataPath))
            {
                File.Delete(_dataPath);
            }

            File.Move(Path.Combine(staging, Path.GetFileName(_dataPath)), _dataPath);
            Directory.Delete(staging, true);
            data.Generated = true;
            data.GenerationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            Log((await output.ConfigureAwait(false)).Trim());
        }

        data.Generator = $"python tools/testdata/generate_testdata.py --large {_options.Records} --large-dir <каталог> --large-format {_options.Format}";
        data.Path = Relative(_dataPath);
        data.SizeBytes = new FileInfo(_dataPath).Length;
        data.SizeMb = Math.Round(data.SizeBytes / (1024.0 * 1024.0), 1);
        Log($"Файл: {data.Path}, {data.SizeMb:N1} МБ{(data.Generated ? $", создан за {data.GenerationSeconds:N1} с" : ", использован существующий")}");
    }

    private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        var admin = new DatabaseAdmin(null);
        foreach (var migration in MigrationCatalog.All)
        {
            if (_options.SkipV008 && migration.Id == "V008")
            {
                continue;
            }

            var result = await admin.ApplyMigrationAsync(_database.Settings, null, migration.Id, "load-test", cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            _report.Settings.MigrationsApplied.Add(migration.Id);
        }

        var inspection = await admin.InspectAsync(_database.Settings, null, cancellationToken).ConfigureAwait(false);
        if (!inspection.CanProcess)
        {
            throw new InvalidOperationException("База не готова к записи: " + string.Join("; ", inspection.ProcessingBlockers));
        }
    }

    private static StructureDescriptor StructureFor(string format)
    {
        switch (format)
        {
            case "fixed":
                return new StructureDescriptor
                {
                    Classification = StructureDescriptor.ClassStructured, Format = StructureDescriptor.FormatFixedWidth, Encoding = "utf-8-sig",
                    HasHeader = true, HeaderRow = 0, SkipRows = 0, FixedWidths = FixedWidths.ToList(), Columns = Columns.ToList(),
                };
            case "jsonl":
                return new StructureDescriptor
                {
                    Classification = StructureDescriptor.ClassStructured, Format = StructureDescriptor.FormatJsonl, Encoding = "utf-8",
                    HasHeader = false, SkipRows = 0, Columns = Columns.ToList(),
                };
            default:
                return new StructureDescriptor
                {
                    Classification = StructureDescriptor.ClassStructured, Format = StructureDescriptor.FormatDelimited, Encoding = "utf-8-sig",
                    HasHeader = true, HeaderRow = 0, SkipRows = 0, Delimiter = ";", QuoteChar = "\"", Columns = Columns.ToList(),
                };
        }
    }

    private ProcessingSettings ProcessingSettings() => new()
    {
        ChunkSize = _options.ChunkSize,
        SqlBatchSize = _options.SqlBatchSize,
        QueueCapacity = _options.QueueCapacity,
        MaxAttemptsPerRequest = 6,
        // Предел бюджета задания снят: при значении по умолчанию (5000 запросов) задание встало бы на паузу.
        BudgetMaxRequests = 0,
        BudgetMaxTokens = 0,
    };

    private ProductHost NewHost(LocalLlmSimulator simulator, CountingLogger logger, out InstrumentedStorageFactory storage, out InstrumentedWorkerFactory workers)
    {
        var store = ProductHost.CreateSettings(_database.Settings, ProcessingSettings(),
            ProductHost.SimulatorProfile(_options.BatchRows, _options.Concurrency, _options.MaxInputTokens));
        InstrumentedStorageFactory createdStorage = null;
        InstrumentedWorkerFactory createdWorkers = null;
        var host = new ProductHost(store, simulator, _worker, logger,
            inner => createdStorage = new InstrumentedStorageFactory(inner),
            inner => createdWorkers = new InstrumentedWorkerFactory(inner));
        storage = createdStorage;
        workers = createdWorkers;
        return host;
    }

    // ---------- Только чтение (worker без конвейера) ----------

    private async Task RunParseOnlyAsync(StructureDescriptor structure, CancellationToken cancellationToken)
    {
        Log("Этап 1: только чтение файла worker (open_reader/read_chunk) без LLM и SQL…");
        var host = NewHost(new LocalLlmSimulator(), new CountingLogger(), out _, out var workers);
        var input = await host.PrepareInputAsync(_dataPath, structure, cancellationToken).ConfigureAwait(false);
        FullGc();
        using var sampler = new MemorySampler(TimeSpan.FromMilliseconds(_options.SampleMs), () => workers.ActivePids.Keys.ToList(),
            () => (Interlocked.Read(ref _progressRead), Interlocked.Read(ref _progressRead)));
        _progressRead = 0;
        sampler.BeginPhase("parse-only");
        var stopwatch = Stopwatch.StartNew();
        long records = 0, bad = 0, chunks = 0, blank = 0, expected = 1;
        var contiguous = true;
        var lastLog = Stopwatch.StartNew();
        using (var client = workers.Create())
        {
            var readerId = await client.OpenReaderAsync(new OpenReaderRequest
            {
                Path = input.File.LongPath,
                Structure = input.Structure,
                ChunkSize = _options.ChunkSize,
                StartAfterOrdinal = 0,
                ExpectedSize = input.File.Size,
                ExpectedMtimeNs = input.File.Fingerprint.LastWriteUnixNanoseconds,
            }, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var chunk = await client.ReadChunkAsync(readerId, cancellationToken).ConfigureAwait(false);
                chunks++;
                foreach (var record in SourceRecord.FromChunk(chunk))
                {
                    contiguous &= record.Ordinal == expected++;
                    bad += record.HasError ? 1 : 0;
                    records++;
                }

                Interlocked.Exchange(ref _progressRead, records);
                blank = chunk.Stats?.BlankLines ?? blank;
                if (lastLog.Elapsed > TimeSpan.FromSeconds(5))
                {
                    Log($"  прочитано {records:N0} ({records / stopwatch.Elapsed.TotalSeconds:N0} зап/с)");
                    lastLog.Restart();
                }

                if (chunk.Eof)
                {
                    break;
                }
            }

            await client.CloseReaderAsync(readerId, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        var memory = sampler.EndPhase(workers.FinishedWorkers.ToList());
        var seconds = stopwatch.Elapsed.TotalSeconds;
        _report.ParseOnly = new ParseOnlyResult
        {
            Seconds = Math.Round(seconds, 2),
            Records = records,
            RecordsPerSecond = Math.Round(records / seconds),
            MegabytesPerSecond = Math.Round(_report.Data.SizeMb / seconds, 2),
            Chunks = chunks,
            BadRecords = bad,
            BlankLines = blank,
            OrdinalsContiguous = contiguous && records == _options.Records,
            ReadChunkP50Ms = workers.Stats.ReadChunkLatency.Percentile(50),
            ReadChunkP95Ms = workers.Stats.ReadChunkLatency.Percentile(95),
            ReadChunkMaxMs = Math.Round(workers.Stats.ReadChunkLatency.MaxMs, 1),
            Memory = memory,
        };
        Log($"  чтение: {records:N0} записей за {seconds:N1} с = {records / seconds:N0} зап/с; пик Python {memory.PythonOsPeakPrivateMb:N0} МБ private, .NET {memory.DotNetPeakPrivateMb:N0} МБ private");
    }

    // ---------- Полный конвейер приложения ----------

    private async Task<(ProductHost Host, JobSession Session)> RunPipelineAsync(StructureDescriptor structure, CancellationToken cancellationToken)
    {
        Log("Этап 2: полный конвейер (ProcessingService → FilePipeline → имитатор LLM → проверка → SqlFactWriter)…");
        var simulator = new LocalLlmSimulator(SimulatorOptions());
        var logger = new CountingLogger();
        var host = NewHost(simulator, logger, out var storage, out var workers);
        var input = await host.PrepareInputAsync(_dataPath, structure, cancellationToken).ConfigureAwait(false);
        var session = await host.Processing.CreateJobAsync(new[] { input }, cancellationToken).ConfigureAwait(false);
        _session = session;
        if (cancellationToken.IsCancellationRequested)
        {
            session.Stop();
        }

        Interlocked.Exchange(ref _progressRead, 0);
        Interlocked.Exchange(ref _progressCommitted, 0);
        var clock = new Stopwatch();
        var processingStartedMs = -1L;
        session.FileChanged += e =>
        {
            if (e.Status == FileStatus.Processing)
            {
                Interlocked.CompareExchange(ref processingStartedMs, clock.ElapsedMilliseconds, -1L);
            }

            if (e.Progress != null)
            {
                InterlockedMax(ref _progressRead, e.Progress.RecordsRead);
                InterlockedMax(ref _progressCommitted, e.Progress.Committed);
            }
        };

        FullGc();
        using var sampler = new MemorySampler(TimeSpan.FromMilliseconds(_options.SampleMs), () => workers.ActivePids.Keys.ToList(),
            () => (Interlocked.Read(ref _progressRead), Interlocked.Read(ref _progressCommitted)));
        sampler.BeginPhase("pipeline");
        clock.Start();
        var run = session.RunAsync();
        var lastCommitted = 0L;
        var lastTick = clock.Elapsed;
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            var committed = Interlocked.Read(ref _progressCommitted);
            var read = Interlocked.Read(ref _progressRead);
            var now = clock.Elapsed;
            var rate = (committed - lastCommitted) / Math.Max(0.001, (now - lastTick).TotalSeconds);
            var self = MemoryProbe.ReadCurrent();
            var python = workers.ActivePids.Keys.Select(MemoryProbe.Read).Where(m => m != null).Sum(m => m.PrivateBytes);
            Log($"  {now.TotalSeconds,6:N0} с: зафиксировано {committed:N0} / прочитано {read:N0} (в пути {read - committed:N0}), {rate:N0} зап/с; " +
                $".NET {MemorySampler.Mb(self?.PrivateBytes ?? 0):N0} МБ, Python {MemorySampler.Mb(python):N0} МБ; фиксаций {storage.Stats.Succeeded:N0}");
            lastCommitted = committed;
            lastTick = now;
        }

        var status = await run.ConfigureAwait(false);
        clock.Stop();
        var memory = sampler.EndPhase(workers.FinishedWorkers.ToList());
        FullGc();
        var total = clock.Elapsed.TotalSeconds;
        var processing = processingStartedMs < 0 ? total : total - processingStartedMs / 1000.0;
        var commitStats = storage.Stats;
        var result = new PipelineResult
        {
            JobStatus = status.ToString(),
            StatusMessage = session.StatusMessage,
            TotalSeconds = Math.Round(total, 2),
            HashAndRegisterSeconds = Math.Round(processingStartedMs < 0 ? 0 : processingStartedMs / 1000.0, 2),
            ProcessingSeconds = Math.Round(processing, 2),
            RecordsPerSecondTotal = Math.Round(_options.Records / total),
            RecordsPerSecondProcessing = Math.Round(_options.Records / processing),
            LlmRequests = simulator.Stats.Calls,
            RecordsSentToLlm = simulator.Stats.RecordsSeen,
            SimulatedFaults = simulator.Stats.FaultCounts().Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value),
            Commits = commitStats.Succeeded,
            FailedCommits = commitStats.Failed,
            DuplicateCommits = commitStats.Duplicates,
            AverageRowsPerCommit = Math.Round(commitStats.AverageRowsPerCommit, 1),
            MaxRowsPerCommit = commitStats.MaxRowsPerCommit,
            CommitP50Ms = commitStats.Latency.Percentile(50),
            CommitP95Ms = commitStats.Latency.Percentile(95),
            CommitMaxMs = Math.Round(commitStats.Latency.MaxMs, 1),
            CommitTotalSeconds = Math.Round(commitStats.Latency.TotalMs / 1000.0, 1),
            CommitBusyShare = Math.Round(commitStats.Latency.TotalMs / 1000.0 / processing, 3),
            WorkerChunks = workers.Stats.Chunks,
            WorkerReadTotalSeconds = Math.Round(workers.Stats.ReadChunkLatency.TotalMs / 1000.0, 1),
            WorkerBusyShare = Math.Round(workers.Stats.ReadChunkLatency.TotalMs / 1000.0 / processing, 3),
            ReadChunkP50Ms = workers.Stats.ReadChunkLatency.Percentile(50),
            ReadChunkP95Ms = workers.Stats.ReadChunkLatency.Percentile(95),
            Memory = memory,
            ManagedHeapAfterFullGcMb = Math.Round(MemorySampler.Mb(GC.GetTotalMemory(true)), 1),
            DotNetPrivateAfterRunMb = Math.Round(MemorySampler.Mb(MemoryProbe.ReadCurrent()?.PrivateBytes ?? 0), 1),
            LogCounts = logger.Counts.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
            RecentWarnings = logger.RecentWarnings.Reverse().Take(10).Select(w => $"{w.Event}: {w.Message}").ToList(),
        };
        foreach (var share in new[] { 0.10, 0.25, 0.50, 0.75, 0.90, 1.00 })
        {
            var target = (long)Math.Ceiling(share * _options.Records);
            var sample = memory.Series.FirstOrDefault(s => s.RecordsCommitted >= target);
            if (sample != null)
            {
                result.ProgressByShare.Add(new ProgressPoint
                {
                    Share = share.ToString("P0", CultureInfo.InvariantCulture),
                    Seconds = sample.Seconds,
                    RecordsCommitted = sample.RecordsCommitted,
                    DotNetPrivateMb = sample.DotNetPrivateMb,
                    ManagedHeapMb = sample.ManagedHeapMb,
                    PythonPrivateMb = sample.PythonPrivateMb,
                    InFlightRecords = sample.RecordsRead - sample.RecordsCommitted,
                });
            }
        }

        _report.Pipeline = result;
        Log($"  конвейер: {status} за {total:N1} с (хеш и регистрация {result.HashAndRegisterSeconds:N1} с), {result.RecordsPerSecondProcessing:N0} зап/с обработки; " +
            $"пик .NET {memory.DotNetPeakPrivateMb:N0} МБ private / {memory.DotNetPeakWorkingSetMb:N0} МБ WS, Python {memory.PythonOsPeakPrivateMb:N0} МБ private; " +
            $"фиксаций {commitStats.Succeeded:N0} по {commitStats.AverageRowsPerCommit:N1} строк");
        return (host, session);
    }

    // ---------- Проверка результата в SQL ----------

    private async Task VerifyAsync(ProductHost host, JobSession session, CancellationToken cancellationToken)
    {
        Log("Этап 3: проверка результата в SQL (без потерь и дубликатов)…");
        var n = (long)_options.Records;
        var jobFile = (await host.OpenStorage().Jobs.ListJobFilesAsync(session.JobId, cancellationToken).ConfigureAwait(false)).Single();
        var stopwatch = Stopwatch.StartNew();
        var stored = await StoredFileResults.ReadAsync(_database.Settings, jobFile.SourceFileId, jobFile.ExtractionVersion, false, cancellationToken).ConfigureAwait(false);
        var sql = new SqlResult
        {
            Stored = stored,
            JobFileRecordsRead = jobFile.RecordsRead,
            JobFileExtracted = jobFile.RecordsExtracted,
            JobFileObservations = jobFile.ObservationsSaved,
            JobFileRequests = jobFile.Requests,
        };
        sql.CommitLogRows = Convert.ToInt64(await _database.ScalarAsync($"SELECT COUNT_BIG(*) FROM [dbo].[FaktCommitLog] WHERE [JobFileId] = {jobFile.JobFileId};").ConfigureAwait(false));
        sql.VerificationQuerySeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        await ReadDatabaseSizeAsync(sql).ConfigureAwait(false);
        _report.Sql = sql;

        void Check(string name, bool passed, string details) => _report.Verification.Add(new CheckResult { Name = name, Passed = passed, Details = details });
        Check("Задание завершено", _report.Pipeline.JobStatus == JobStatus.Completed.ToString(), _report.Pipeline.JobStatus + ": " + _report.Pipeline.StatusMessage);
        Check("Файл отмечен обработанным, граница = N", stored.Completed && stored.ConfirmedOrdinal == n, $"Completed={stored.Completed}, ConfirmedOrdinal={stored.ConfirmedOrdinal:N0}");
        Check("Реестр строк: N записей, номера 1..N без пропусков и повторов",
            stored.RowOutcomes == n && stored.DistinctOrdinals == n && stored.MinOrdinal == 1 && stored.MaxOrdinal == n,
            $"строк {stored.RowOutcomes:N0}, различных {stored.DistinctOrdinals:N0}, {stored.MinOrdinal}..{stored.MaxOrdinal}");
        Check("Каждая запись дала результат «извлечено» без ошибок", stored.OutcomeExtracted == n && stored.OutcomeErrors == 0 && stored.RowErrors == 0,
            $"извлечено {stored.OutcomeExtracted:N0}, без фактов {stored.OutcomeNoFacts:N0}, ошибок {stored.OutcomeErrors:N0}, журнал ошибок {stored.RowErrors:N0}");
        Check("PersonFacts: одно наблюдение на запись, без дубликатов ключа", stored.PersonFacts == n && stored.DuplicateObservationKeys == 0,
            $"наблюдений {stored.PersonFacts:N0}, повторов ключа {stored.DuplicateObservationKeys}");
        Check("Поисковая проекция: документ на каждое наблюдение", stored.SearchDocs == stored.PersonFacts, $"документов {stored.SearchDocs:N0}");
        Check("Индекс идентификаторов: телефон, e-mail и счёт каждого лица", stored.FactValues == 3 * n, $"значений {stored.FactValues:N0} (ожидалось {3 * n:N0})");
        Check("Порядок ID наблюдений совпадает с порядком записей файла", stored.IdOrderInversions == 0, $"нарушений {stored.IdOrderInversions}");
        Check("Счётчики checkpoint равны содержимому реестра",
            stored.ProgressExtracted == stored.OutcomeExtracted && stored.ProgressNoFacts == stored.OutcomeNoFacts && stored.ProgressErrors == stored.OutcomeErrors &&
            stored.ProgressObservations == stored.PersonFacts,
            $"checkpoint: {stored.ProgressExtracted:N0}/{stored.ProgressNoFacts:N0}/{stored.ProgressErrors:N0}/{stored.ProgressObservations:N0}");
        Check("Счётчики файла задания", jobFile.RecordsExtracted == n && jobFile.ObservationsSaved == n && jobFile.RecordsRead == n,
            $"прочитано {jobFile.RecordsRead:N0}, извлечено {jobFile.RecordsExtracted:N0}, наблюдений {jobFile.ObservationsSaved:N0}, запросов {jobFile.Requests:N0}");
        Check("Журнал фиксаций: по записи на успешную фиксацию", sql.CommitLogRows == _report.Pipeline.Commits - _report.Pipeline.DuplicateCommits,
            $"записей журнала {sql.CommitLogRows:N0}, фиксаций {_report.Pipeline.Commits:N0} (повторов-дубликатов {_report.Pipeline.DuplicateCommits})");
        await SpotCheckAsync(jobFile, Check).ConfigureAwait(false);
        foreach (var check in _report.Verification)
        {
            Log($"  [{(check.Passed ? "OK" : "СБОЙ")}] {check.Name}: {check.Details}");
        }
    }

    private async Task ReadDatabaseSizeAsync(SqlResult sql)
    {
        using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = new SqlCommand(@"
SELECT SUM(CASE WHEN type = 0 THEN size END) * 8 / 1024.0,
       SUM(CASE WHEN type = 0 THEN CAST(FILEPROPERTY(name, 'SpaceUsed') AS BIGINT) END) * 8 / 1024.0,
       SUM(CASE WHEN type = 1 THEN size END) * 8 / 1024.0
FROM sys.database_files;", connection);
        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
        {
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                sql.DatabaseSizeMb = Math.Round(Convert.ToDouble(reader.GetValue(0)), 1);
                sql.DataUsedMb = Math.Round(Convert.ToDouble(reader.GetValue(1)), 1);
                sql.LogSizeMb = Math.Round(Convert.ToDouble(reader.GetValue(2)), 1);
            }
        }

        using var tables = new SqlCommand(@"
SELECT OBJECT_NAME(COALESCE(it.parent_object_id, ps.object_id)),
       SUM(CASE WHEN it.object_id IS NULL AND ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END),
       SUM(ps.reserved_page_count) * 8 / 1024.0
FROM sys.dm_db_partition_stats AS ps
LEFT JOIN sys.internal_tables AS it ON it.object_id = ps.object_id
WHERE OBJECTPROPERTY(COALESCE(it.parent_object_id, ps.object_id), 'IsUserTable') = 1
GROUP BY OBJECT_NAME(COALESCE(it.parent_object_id, ps.object_id))
ORDER BY 3 DESC;", connection) { CommandTimeout = 300 };
        using var rows = await tables.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rows.ReadAsync().ConfigureAwait(false))
        {
            sql.Tables.Add(new TableSize
            {
                Table = rows.GetString(0),
                Rows = Convert.ToInt64(rows.GetValue(1)),
                ReservedMb = Math.Round(Convert.ToDouble(rows.GetValue(2)), 1),
            });
        }
    }

    /// <summary>Выборочная сверка: запись с номером K в файле ↔ наблюдение с SourceRecordKey = K в базе.</summary>
    private async Task SpotCheckAsync(JobFileRecord jobFile, Action<string, bool, string> check)
    {
        var n = _options.Records;
        var random = new Random(20260924);
        var ordinals = new SortedSet<long> { 1, n, Math.Max(1, n / 2), Math.Max(1, n / 3), Math.Max(1, n - 1) };
        while (ordinals.Count < Math.Min(n, 25))
        {
            ordinals.Add(1 + random.Next(n));
        }

        var expected = ReadRecordsFromFile(ordinals);
        var names = new SqlNames(_database.Settings);
        var c = names.Columns;
        var mismatches = new List<string>();
        using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var ordinal in ordinals)
        {
            var fields = expected[ordinal];
            var fio = fields["ФИО"].Split(' ');
            using var command = new SqlCommand($@"
SELECT p.{names.Col(c.Surname)}, p.{names.Col(c.Name)}, p.{names.Col(c.Patronymic)}, p.{names.Col(c.BirthDate)}, p.{names.Col(c.BirthPlace)},
       (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), v.[FactType] + N'=' + v.[NormalizedValue]), N'|') WITHIN GROUP (ORDER BY v.[FactType])
        FROM {names.Aux("FaktFactValues")} AS v WHERE v.[PersonFactId] = p.{names.Col(c.PersonFactsId)})
FROM {names.PF} AS p
WHERE p.{names.Col(c.FileId)} = @f AND p.[ExtractionVersion] = @v AND p.[SourceRecordKey] = @k AND p.[PersonIndex] = 0;", connection);
            command.Parameters.Add("@f", SqlDbType.BigInt).Value = jobFile.SourceFileId;
            command.Parameters.Add("@v", SqlDbType.VarChar, 64).Value = jobFile.ExtractionVersion;
            command.Parameters.Add("@k", SqlDbType.VarChar, 64).Value = ordinal.ToString(CultureInfo.InvariantCulture);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                mismatches.Add($"запись {ordinal}: наблюдение не найдено");
                continue;
            }

            var birth = DateTime.ParseExact(fields["Дата рождения"], "dd.MM.yyyy", CultureInfo.InvariantCulture);
            var expectedFacts = $"bank_account={FactNormalizer.DigitsOnly(fields["Номер счёта"])}|email={fields["Email"].ToLowerInvariant()}|phone={FactNormalizer.DigitsOnly(fields["Телефон"])}";
            var actual = new[]
            {
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            };
            var wanted = new[]
            {
                fio[0], fio[1], fio.Length > 2 ? string.Join(" ", fio.Skip(2)) : null, birth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                fields["Место рождения"], expectedFacts,
            };
            for (var i = 0; i < wanted.Length; i++)
            {
                if (!string.Equals(wanted[i], actual[i], StringComparison.Ordinal))
                {
                    mismatches.Add($"запись {ordinal}, поле {i}: ожидалось «{wanted[i]}», в базе «{actual[i]}»");
                }
            }
        }

        check($"Выборочная сверка {ordinals.Count} записей файла с базой (ФИО, дата и место рождения, телефон, e-mail, счёт)", mismatches.Count == 0,
            mismatches.Count == 0 ? "номера записей: " + string.Join(", ", ordinals) : string.Join("; ", mismatches.Take(10)));
    }

    private Dictionary<long, Dictionary<string, string>> ReadRecordsFromFile(ICollection<long> ordinals)
    {
        var result = new Dictionary<long, Dictionary<string, string>>();
        var max = ordinals.Max();
        using var reader = new StreamReader(_dataPath, new UTF8Encoding(false), true, 1 << 20);
        if (_options.Format != "jsonl")
        {
            reader.ReadLine(); // заголовок
        }

        for (long ordinal = 1; ordinal <= max; ordinal++)
        {
            var line = reader.ReadLine() ?? throw new InvalidDataException($"В файле меньше {max} записей.");
            if (!ordinals.Contains(ordinal))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (_options.Format)
            {
                case "jsonl":
                    var obj = JObject.Parse(line);
                    foreach (var column in Columns)
                    {
                        fields[column] = (string)obj[column];
                    }

                    break;
                case "fixed":
                    var start = 0;
                    for (var i = 0; i < Columns.Length; i++)
                    {
                        fields[Columns[i]] = start >= line.Length ? string.Empty : line.Substring(start, Math.Min(FixedWidths[i], line.Length - start)).Trim();
                        start += FixedWidths[i];
                    }

                    break;
                default:
                    var parts = line.Split(';');
                    for (var i = 0; i < Columns.Length; i++)
                    {
                        fields[Columns[i]] = parts[i];
                    }

                    break;
            }

            result[ordinal] = fields;
        }

        return result;
    }

    // ---------- Полнотекстовый индекс и поиск ----------

    private async Task RunSearchAsync(ProductHost host, JobSession session, CancellationToken cancellationToken)
    {
        Log("Этап 4: ожидание полнотекстового индекса и замер поиска (SqlSearchRepository)…");
        var fts = new FullTextResult();
        var stopwatch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromMinutes(_options.FullTextTimeoutMinutes);
        var expectedItems = _report.Sql?.Stored?.SearchDocs ?? _options.Records;
        var first = true;
        while (true)
        {
            using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = new SqlCommand(@"
DECLARE @id INT = OBJECT_ID(N'[dbo].[FaktSearchDocs]');
SELECT CAST(OBJECTPROPERTYEX(@id, 'TableFulltextPendingChanges') AS BIGINT), CAST(OBJECTPROPERTYEX(@id, 'TableFulltextPopulateStatus') AS INT),
       CAST(OBJECTPROPERTYEX(@id, 'TableFulltextItemCount') AS BIGINT);", connection);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            await reader.ReadAsync().ConfigureAwait(false);
            var pending = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            var status = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            var items = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            if (first)
            {
                fts.PendingAtPipelineEnd = pending;
                first = false;
            }

            fts.IndexedItems = items;
            fts.PopulateStatus = status;
            if (pending == 0 && status == 0 && items >= expectedItems)
            {
                break;
            }

            if (stopwatch.Elapsed > deadline || cancellationToken.IsCancellationRequested)
            {
                fts.TimedOut = true;
                break;
            }

            if (stopwatch.Elapsed.TotalSeconds % 30 < 2)
            {
                Log($"  индекс: проиндексировано {items:N0}, в очереди {pending:N0}, состояние {status}");
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }

        fts.WaitSecondsAfterPipeline = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        _report.FullText = fts;
        Log($"  полнотекстовый индекс готов через {fts.WaitSecondsAfterPipeline:N0} с после конвейера (в очереди на момент окончания: {fts.PendingAtPipelineEnd:N0}), документов {fts.IndexedItems:N0}");

        var jobFile = (await host.OpenStorage().Jobs.ListJobFilesAsync(session.JobId, cancellationToken).ConfigureAwait(false)).Single();
        var sample = ReadRecordsFromFile(new[] { (long)Math.Max(1, _options.Records / 2) }).Values.Single();
        var fio = sample["ФИО"].Split(' ');
        var phone = sample["Телефон"];
        var account = sample["Номер счёта"];
        var city = sample["Место рождения"].Split(' ').Last();
        var queries = new List<(string Name, string Description, SearchQuery Query, bool Count)>
        {
            ("fts_common_word", "Все слова: «Иван» (частое имя)", new SearchQuery { Text = "Иван", Mode = SearchMode.AllWords, PageSize = 50 }, false),
            ("fts_surname_name", "Все слова: «Тестов Иван»", new SearchQuery { Text = "Тестов Иван", Mode = SearchMode.AllWords, PageSize = 50 }, false),
            ("fts_rare_person", $"Все слова: ФИО и город одной записи ({fio[0]} … {city})",
                new SearchQuery { Text = string.Join(" ", fio) + " " + city, Mode = SearchMode.AllWords, PageSize = 50 }, false),
            ("fts_exact_phrase", "Точная фраза: имя и отчество одной записи", new SearchQuery { Text = string.Join(" ", fio.Skip(1)), Mode = SearchMode.ExactPhrase, PageSize = 50 }, false),
            ("fts_any_word", "Любое слово: два города", new SearchQuery { Text = "Тестовск Эталонск", Mode = SearchMode.AnyWord, PageSize = 50 }, false),
            ("fts_file_code", "Все слова: код файла (совпадают все документы)", new SearchQuery { Text = jobFile.FileCode, Mode = SearchMode.AllWords, PageSize = 50 }, false),
            ("identifier_phone", "Идентификатор: телефон одной записи в другом формате (тип «телефон»)",
                new SearchQuery { Identifier = Reformat(phone), FactType = FactTypes.Phone, PageSize = 50 }, false),
            ("identifier_account_any", "Идентификатор: счёт одной записи без указания типа", new SearchQuery { Identifier = account, PageSize = 50 }, false),
            ("identifier_prefix", "Идентификатор по началу: 7 первых цифр телефона",
                new SearchQuery { Identifier = FactNormalizer.DigitsOnly(phone).Substring(0, 7), IdentifierPrefix = true, PageSize = 50 }, false),
            ("filter_surname_prefix", "Фильтр: фамилия по началу «Синтетик»", new SearchQuery { Surname = "Синтетик", PageSize = 50, Sort = SearchSort.IdAscending }, false),
            ("filter_birth_range", "Фильтр: дата рождения в январе 1990", new SearchQuery
            {
                BirthDateFrom = new DateTime(1990, 1, 1), BirthDateTo = new DateTime(1990, 1, 31), PageSize = 50, Sort = SearchSort.IdAscending,
            }, false),
            ("filter_birth_place", $"Фильтр: место рождения «{city}» (полнотекстовый по проекции)", new SearchQuery { BirthPlace = city, PageSize = 50, Sort = SearchSort.IdAscending }, false),
            ("count_common_word", "Точное число результатов «Иван» (отдельный подсчёт)", new SearchQuery { Text = "Иван", Mode = SearchMode.AllWords }, true),
            ("substring_like", "Подстрока (LIKE %…%, без индекса): частый фрагмент «интетико» — просмотр останавливается на первых 51 совпадении",
                new SearchQuery { Text = "интетико", Mode = SearchMode.Substring, PageSize = 50 }, false),
            ("substring_like_rare", "Подстрока (LIKE %…%, без индекса): 10 цифр номера счёта (≈0,01 % записей) — до 51-го совпадения просматривается большая часть проекции",
                new SearchQuery { Text = account.Substring(6, 10), Mode = SearchMode.Substring, PageSize = 50 }, false),
        };

        foreach (var (name, description, query, count) in queries)
        {
            var result = new SearchResult { Name = name, Description = description };
            for (var run = 0; run < 3; run++)
            {
                var repository = host.OpenStorage().Search;
                var watch = Stopwatch.StartNew();
                try
                {
                    if (count)
                    {
                        result.TotalCount = await repository.CountAsync(query, cancellationToken).ConfigureAwait(false);
                        result.UsedFullText = true;
                    }
                    else
                    {
                        var page = await repository.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                        result.Rows = page.Rows.Count;
                        result.HasMore = page.HasMore;
                        result.UsedFullText = page.UsedFullText;
                    }
                }
                catch (Exception ex) when (ex is SqlException || ex is SearchValidationException || ex is InvalidOperationException)
                {
                    result.Error = ex.GetType().Name + ": " + ex.Message;
                }

                result.RunsMs.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 1));
            }

            result.FirstMs = result.RunsMs[0];
            result.WarmMedianMs = Math.Round(result.RunsMs.Skip(1).MedianOrZero(), 1);
            _report.Search.Add(result);
            Log($"  {name}: первый {result.FirstMs:N0} мс, повтор {result.WarmMedianMs:N0} мс, строк {result.Rows}{(result.HasMore ? "+" : string.Empty)}" +
                (result.TotalCount.HasValue ? $", всего {result.TotalCount:N0}" : string.Empty) + (result.Error != null ? " ОШИБКА " + result.Error : string.Empty));
        }

        var phoneResult = _report.Search.First(s => s.Name == "identifier_phone");
        var rareResult = _report.Search.First(s => s.Name == "fts_rare_person");
        _report.Verification.Add(new CheckResult
        {
            Name = "Поиск находит выбранную запись по телефону в другом формате и по ФИО с городом",
            Passed = phoneResult.Rows >= 1 && rareResult.Rows >= 1 && phoneResult.Error == null && rareResult.Error == null,
            Details = $"телефон: строк {phoneResult.Rows}; ФИО+город: строк {rareResult.Rows}",
        });
    }

    /// <summary>«+7 000 124 22 60» → «+7(000)1242260»: тот же номер в другом написании.</summary>
    private static string Reformat(string phone)
    {
        var digits = FactNormalizer.DigitsOnly(phone);
        return phone.TrimStart().StartsWith("+", StringComparison.Ordinal) && digits.Length == 11
            ? $"+{digits[0]}({digits.Substring(1, 3)}){digits.Substring(4)}"
            : digits;
    }

    // ---------- Отчёт ----------

    private void WriteReports()
    {
        try
        {
            var directory = _options.OutputDirectory ?? Path.Combine(_repositoryRoot, "docs", "test-results");
            Directory.CreateDirectory(directory);
            var baseName = $"load-test-{_report.StartedAtUtc.ToLocalTime():yyyy-MM-dd}-{_options.EffectiveLabel}";
            var json = JsonConvert.SerializeObject(_report, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
                Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
            });
            File.WriteAllText(Path.Combine(directory, baseName + ".json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, baseName + ".md"), MarkdownReport.Render(_report, baseName + ".json"), new UTF8Encoding(false));
            Log($"Отчёт: {Relative(Path.Combine(directory, baseName + ".md"))} и .json");
        }
        catch (Exception ex)
        {
            Log("Не удалось записать отчёт: " + ex.Message);
        }
    }

    // ---------- Вспомогательное ----------

    private string Relative(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        var root = _repositoryRoot.TrimEnd('\\') + "\\";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length).Replace('\\', '/') : full;
    }

    private static void FullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static string OsDescription()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Registry.GetValue(key, "ProductName", null) as string ?? "Windows";
        var build = Registry.GetValue(key, "CurrentBuildNumber", null) as string ?? "?";
        var ubr = Registry.GetValue(key, "UBR", null);
        var display = Registry.GetValue(key, "DisplayVersion", null) as string;
        if (int.TryParse(build, out var number) && number >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11"); // реестр Windows 11 сохраняет ProductName «Windows 10»
        }

        return $"{product} {display} (сборка {build}{(ubr != null ? "." + ubr : string.Empty)}, x64)".Replace("  ", " ");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static double TotalPhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    }
}
