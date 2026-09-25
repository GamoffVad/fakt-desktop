using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Extraction;
using Fakt.Application.Llm;
using Fakt.Application.Settings;
using Fakt.Core.Extraction;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Processing;
using Fakt.Core.Security;
using Fakt.Core.Storage;
using Fakt.Core.Structure;
using Fakt.Core.Worker;
using Newtonsoft.Json;

namespace Fakt.Application.Processing;

/// <summary>Файл, выбранный для извлечения, с проверенной структурой.</summary>
public sealed class JobFileInput
{
    public ScannedFile File { get; set; }
    public StructureDescriptor Structure { get; set; }
}

public sealed class FileStatusEvent
{
    public string FullPath { get; set; }
    public FileStatus Status { get; set; }
    public string Message { get; set; }
    public string FileCode { get; set; }
    public PipelineProgress Progress { get; set; }
}

/// <summary>
/// Выполнение задания: снимок конфигурации (без секретов), регистрация источников (полный потоковый хеш,
/// FileCode), конвейер по файлам, пауза/продолжение/остановка, бюджет. Изменение настроек после запуска
/// не влияет на задание: параметры LLM зафиксированы в снимке.
/// </summary>
public sealed class JobSession
{
    private readonly ProcessingService _service;
    private readonly IStorage _storage;
    private readonly LlmRuntimeConfig _runtime;
    private readonly StructuredOutputMode _mode;
    private readonly JobSnapshot _snapshot;
    private readonly List<JobFileInput> _files;
    private readonly Dictionary<string, PreparedFile> _prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly FieldLimits _limits;
    private readonly TokenEstimator _estimator = new();
    private readonly AdaptiveBatchSize _batchSize;
    private readonly BudgetTracker _budget;
    private JobControl _control = new();

    internal JobSession(ProcessingService service, IStorage storage, LlmRuntimeConfig runtime, StructuredOutputMode mode, JobSnapshot snapshot,
        IEnumerable<JobFileInput> files, FieldLimits limits, long jobId, BudgetTracker budget = null)
    {
        _service = service;
        _storage = storage;
        _runtime = runtime;
        _mode = mode;
        _snapshot = snapshot;
        _files = files.ToList();
        _limits = limits;
        JobId = jobId;
        _batchSize = new AdaptiveBatchSize(runtime.Profile.BatchRows);
        _budget = budget ?? new BudgetTracker(snapshot.Processing.BudgetMaxRequests, snapshot.Processing.BudgetMaxTokens);
    }

    public long JobId { get; }

    public JobStatus Status { get; private set; } = JobStatus.Running;

    public string StatusMessage { get; private set; }

    public bool IsRunning { get; private set; }

    public BudgetTracker Budget => _budget;

    public event Action<FileStatusEvent> FileChanged;

    public event Action<JobSession> StateChanged;

    public void Pause() => _control.RequestPause();

    public void Stop() => _control.RequestStop();

    /// <summary>Файл, уже зарегистрированный в задании (возобновление): хеш и FileCode не пересчитываются.</summary>
    internal void RegisterPrepared(PreparedFile prepared) => _prepared[prepared.File.FullPath] = prepared;

    public IReadOnlyList<JobFileInput> Files => _files;

    /// <summary>Запуск или продолжение: необработанные файлы продолжаются с подтверждённой границы.</summary>
    public async Task<JobStatus> RunAsync()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Задание уже выполняется.");
        }

        IsRunning = true;
        if (_control.PauseRequested || _control.StopRequested)
        {
            _control = new JobControl();
        }

        Status = JobStatus.Running;
        StatusMessage = null;
        StateChanged?.Invoke(this);
        try
        {
            await _storage.Jobs.UpdateJobAsync(JobId, JobStatus.Running, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Нет соединения с базой: задание не запускается и не остаётся «выполняющимся» в приложении.
            IsRunning = false;
            Status = JobStatus.Failed;
            StatusMessage = "Нет соединения с базой данных, задание не запущено: " + ex.Message + " Повторите попытку после восстановления соединения.";
            _service.Logger.Error("job.start_failed", ex.Message, ErrorCategory.Connection, ex, e => e.JobId = JobId);
            StateChanged?.Invoke(this);
            return Status;
        }

        var anyErrors = false;
        var finalStatus = JobStatus.Completed;
        string finalMessage = null;
        try
        {
            foreach (var input in _files)
            {
                if (_control.StopRequested)
                {
                    finalStatus = JobStatus.Cancelled;
                    Raise(input.File, FileStatus.Cancelled, "Задание остановлено до обработки файла.");
                    continue;
                }

                if (_control.PauseRequested)
                {
                    finalStatus = JobStatus.Paused;
                    Raise(input.File, FileStatus.Paused, "Ожидает продолжения.");
                    continue;
                }

                var outcome = await ProcessFileAsync(input).ConfigureAwait(false);
                switch (outcome.Status)
                {
                    case FileStatus.CompletedWithErrors:
                    case FileStatus.Error:
                        anyErrors = true;
                        break;
                    case FileStatus.Paused:
                        finalStatus = JobStatus.Paused;
                        finalMessage = outcome.Message;
                        break;
                    case FileStatus.Cancelled:
                        finalStatus = JobStatus.Cancelled;
                        break;
                }

                if (outcome.StopsJob)
                {
                    finalStatus = outcome.JobStatus;
                    finalMessage = outcome.Message;
                    break;
                }
            }

            if (finalStatus == JobStatus.Completed && anyErrors)
            {
                finalStatus = JobStatus.CompletedWithErrors;
            }
        }
        catch (Exception ex)
        {
            finalStatus = JobStatus.Failed;
            finalMessage = ex.Message + " Задание можно продолжить на странице «История» после устранения причины.";
            _service.Logger.Error("job.failed", ex.Message, ErrorCategory.Internal, ex, e => e.JobId = JobId);
        }
        finally
        {
            IsRunning = false;
        }

        Status = finalStatus;
        StatusMessage = finalMessage ?? Status.ToText();
        try
        {
            await _storage.Jobs.UpdateJobAsync(JobId, Status, Summary(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // База недоступна: итог не сохранён. В базе задание останется «выполняющимся» и на странице «История»
            // будет помечено прерванным; продолжение — после восстановления соединения.
            StatusMessage += " Состояние задания не сохранено в базе данных: " + ex.Message;
            _service.Logger.Error("job.status_not_saved", ex.Message, ErrorCategory.Connection, ex, e => e.JobId = JobId);
        }
        _service.Logger.Info("job.finished", $"Задание {JobId}: {Status.ToText()}", e =>
        {
            e.JobId = JobId;
            e.Data = new Dictionary<string, object> { ["requests"] = _budget.Requests, ["input_tokens"] = _budget.InputTokens, ["output_tokens"] = _budget.OutputTokens };
        });
        StateChanged?.Invoke(this);
        return Status;
    }

    private string Summary()
    {
        var parts = new List<string> { Status.ToText(), $"запросов к модели: {_budget.Requests}" };
        if (_budget.InputTokens + _budget.OutputTokens > 0)
        {
            parts.Add($"токены: вход {_budget.InputTokens}, выход {_budget.OutputTokens}");
        }

        if (!string.IsNullOrEmpty(StatusMessage) && StatusMessage != Status.ToText())
        {
            parts.Add(StatusMessage);
        }

        return string.Join("; ", parts);
    }

    private sealed class FileOutcome
    {
        public FileStatus Status { get; set; }
        public string Message { get; set; }
        public bool StopsJob { get; set; }
        public JobStatus JobStatus { get; set; }
    }

    private void Raise(ScannedFile file, FileStatus status, string message, string fileCode = null, PipelineProgress progress = null) =>
        FileChanged?.Invoke(new FileStatusEvent { FullPath = file.FullPath, Status = status, Message = message, FileCode = fileCode, Progress = progress });

    private async Task<FileOutcome> ProcessFileAsync(JobFileInput input)
    {
        var file = input.File;
        if (!_prepared.TryGetValue(file.FullPath, out var prepared))
        {
            Raise(file, FileStatus.Queued, "Подсчёт хеша содержимого…");
            try
            {
                prepared = await _service.PrepareFileAsync(_storage, JobId, input, _snapshot, _control.StopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Raise(file, FileStatus.Cancelled, "Задание остановлено.");
                return new FileOutcome { Status = FileStatus.Cancelled };
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is FileChangedException)
            {
                Raise(file, FileStatus.Error, ex.Message);
                return new FileOutcome { Status = FileStatus.Error, Message = ex.Message };
            }

            _prepared[file.FullPath] = prepared;
        }
        else if (!_service.FingerprintMatches(prepared.File))
        {
            var message = "Файл изменён после паузы: продолжение со старой позиции невозможно. Запустите обработку заново — будет создана новая версия источника.";
            Raise(file, FileStatus.Error, message, prepared.FileCode);
            await _storage.Jobs.UpdateJobFileAsync(prepared.JobFileId, FileStatus.Error, message, null, CancellationToken.None).ConfigureAwait(false);
            return new FileOutcome { Status = FileStatus.Error, Message = message };
        }

        var progress = await _storage.Jobs.GetFileProgressAsync(prepared.SourceFileId, prepared.ExtractionVersion, CancellationToken.None).ConfigureAwait(false);
        if (progress.Completed)
        {
            var message = $"Эта версия файла уже обработана этой конфигурацией: сохранено наблюдений {progress.Observations}, без фактов {progress.NoFacts}, ошибок {progress.Errors}. Повторная обработка не создаёт дубликатов.";
            var doneStatus = progress.Errors > 0 ? FileStatus.CompletedWithErrors : FileStatus.Completed;
            Raise(file, doneStatus, message, prepared.FileCode, new PipelineProgress
            {
                Committed = progress.Extracted + progress.NoFacts + progress.Errors,
                Extracted = progress.Extracted, NoFacts = progress.NoFacts, Errors = progress.Errors, Observations = progress.Observations,
                ConfirmedOrdinal = progress.ConfirmedOrdinal, Eof = true,
            });
            await _storage.Jobs.UpdateJobFileAsync(prepared.JobFileId, doneStatus, message, null, CancellationToken.None).ConfigureAwait(false);
            return new FileOutcome { Status = doneStatus };
        }

        Raise(file, FileStatus.Processing, progress.ConfirmedOrdinal > 0 ? $"Продолжение после записи {progress.ConfirmedOrdinal}" : null, prepared.FileCode);
        await _storage.Jobs.UpdateJobFileAsync(prepared.JobFileId, FileStatus.Processing, null, null, CancellationToken.None).ConfigureAwait(false);

        var context = new ExtractionContext
        {
            ProviderId = _runtime.Profile.ProviderId,
            ModelId = _runtime.Profile.ModelId,
            PromptVersion = Prompts.FactsPromptVersion,
            ExtractionVersion = prepared.ExtractionVersion,
            JobId = JobId,
            ProcessedAtUtc = DateTime.UtcNow,
            Limits = _limits,
            InferredColumnNames = prepared.Structure?.HasInferredColumnNames == true,
        };
        var llm = new ResilientLlmClient(_service.Registry.Get(_runtime.Profile.ProviderId), _runtime,
            new RetryPolicy { MaxAttempts = _snapshot.Processing.MaxAttemptsPerRequest }, _budget, _service.Logger)
        {
            JobId = JobId,
            FileId = prepared.SourceFileId,
        };
        var extractor = new BatchExtractor(llm, context, _estimator, _batchSize, _service.Logger) { Mode = _mode };
        var baseline = progress;
        using var writer = _storage.CreateWriter(_limits);
        var pipeline = new FilePipeline(prepared, JobId, progress.ConfirmedOrdinal, _service.Workers, writer, extractor, _budget, _control,
            new PipelineOptions
            {
                ChunkSize = _snapshot.Processing.ChunkSize,
                SqlBatchSize = _snapshot.Processing.SqlBatchSize,
                QueueCapacity = _snapshot.Processing.QueueCapacity,
                MaxInputTokens = _runtime.Profile.MaxInputTokensPerRequest,
            },
            _batchSize, _estimator, _runtime.Profile.MaxConcurrentRequests, _service.Logger,
            new Progress<PipelineProgress>(p => Raise(file, FileStatus.Processing, p.LastWarning, prepared.FileCode, Merge(baseline, p))));

        var result = await pipeline.RunAsync().ConfigureAwait(false);
        var merged = Merge(baseline, result.Progress);
        var tail = pipeline.RemainingUsage();
        FileOutcome outcome;
        switch (result.Outcome)
        {
            case PipelineOutcome.Completed:
            {
                await _storage.SourceFiles.MarkFileCompletedAsync(prepared.SourceFileId, prepared.ExtractionVersion, true, CancellationToken.None).ConfigureAwait(false);
                var status = merged.Errors > 0 ? FileStatus.CompletedWithErrors : FileStatus.Completed;
                var message = merged.Errors > 0 ? $"Записей с ошибками: {merged.Errors} (причины — в журнале задания)." : null;
                outcome = new FileOutcome { Status = status, Message = message };
                break;
            }

            case PipelineOutcome.Paused:
                outcome = new FileOutcome { Status = FileStatus.Paused, Message = result.Message };
                break;
            case PipelineOutcome.Stopped:
                outcome = new FileOutcome { Status = FileStatus.Cancelled, Message = result.Message };
                break;
            case PipelineOutcome.BudgetExceeded:
                outcome = new FileOutcome { Status = FileStatus.Paused, Message = result.Message, StopsJob = true, JobStatus = JobStatus.Paused };
                break;
            case PipelineOutcome.ServiceUnavailable:
                outcome = new FileOutcome { Status = FileStatus.Paused, Message = result.Message, StopsJob = true, JobStatus = JobStatus.Paused };
                break;
            case PipelineOutcome.FileChanged:
                outcome = new FileOutcome { Status = FileStatus.Error, Message = result.Message };
                break;
            default:
                // Неверный ключ, неизвестная модель, недоступная конфигурация: задание останавливается с понятной причиной.
                outcome = new FileOutcome { Status = FileStatus.Error, Message = result.Message, StopsJob = true, JobStatus = JobStatus.Failed };
                break;
        }

        Raise(file, outcome.Status, outcome.Message, prepared.FileCode, merged);
        await _storage.Jobs.UpdateJobFileAsync(prepared.JobFileId, outcome.Status, outcome.Message, tail, CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    private static PipelineProgress Merge(FileProgress baseline, PipelineProgress current)
    {
        var merged = current.Clone();
        merged.Extracted += baseline.Extracted;
        merged.NoFacts += baseline.NoFacts;
        merged.Errors += baseline.Errors;
        merged.Observations += baseline.Observations;
        merged.Committed += baseline.Extracted + baseline.NoFacts + baseline.Errors;
        return merged;
    }
}

public sealed class FileChangedException : Exception
{
    public FileChangedException(string message) : base(message)
    {
    }
}

/// <summary>Точка входа прикладного слоя для обработки: подготовка задания, запуск и возобновление.</summary>
public sealed class ProcessingService
{
    private readonly SettingsService _settings;
    private readonly IAuthorizationService _authorization;
    private readonly IStorageFactory _storageFactory;
    private readonly IDatabaseAdmin _databaseAdmin;
    private readonly IFileHasher _hasher;

    public ProcessingService(SettingsService settings, IAuthorizationService authorization, ILlmAdapterRegistry registry, IStorageFactory storageFactory,
        IDatabaseAdmin databaseAdmin, IWorkerClientFactory workers, IFileHasher hasher, IAppLogger logger)
    {
        _settings = settings;
        _authorization = authorization;
        Registry = registry;
        _storageFactory = storageFactory;
        _databaseAdmin = databaseAdmin;
        Workers = workers;
        _hasher = hasher;
        Logger = logger ?? NullLogger.Instance;
    }

    internal ILlmAdapterRegistry Registry { get; }

    internal IWorkerClientFactory Workers { get; }

    internal IAppLogger Logger { get; }

    public string AppVersion { get; set; } = "1.0.0";

    /// <summary>Создание задания: проверка прав и схемы, снимок конфигурации, запись задания в базу.</summary>
    public async Task<JobSession> CreateJobAsync(IReadOnlyList<JobFileInput> files, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ProcessData);
        if (files == null || files.Count == 0)
        {
            throw new InvalidOperationException("Нет файлов с подтверждённой табличной структурой.");
        }

        var settings = _settings.Current;
        var database = settings.Database;
        if (!database.IsConfigured)
        {
            throw new InvalidOperationException("Подключение к базе данных не настроено («Администрирование → База данных»).");
        }

        var password = _settings.SqlPassword(database);

        // Базы, указанной в настройках, нет на сервере — она создаётся автоматически вместе со схемой FAKT.
        var provision = await _databaseAdmin.EnsureDatabaseAsync(database, password, _authorization.CurrentUserName, cancellationToken).ConfigureAwait(false);
        if (!provision.Success)
        {
            throw new InvalidOperationException(provision.Message);
        }

        var report = await _databaseAdmin.InspectAsync(database, password, cancellationToken).ConfigureAwait(false);
        if (!report.CanProcess)
        {
            throw new InvalidOperationException("База данных не готова к записи: " + string.Join("; ", report.ProcessingBlockers.Take(5)));
        }

        var profile = _settings.ActiveProfile ?? throw new InvalidOperationException("Профиль LLM не настроен («Администрирование → LLM»).");
        var descriptor = Registry.Providers.FirstOrDefault(p => p.Id == profile.ProviderId);
        var runtime = _settings.BuildRuntimeConfig(profile, descriptor);
        var mode = CapabilityResolver.Resolve(profile) ??
                   throw new InvalidOperationException("Возможности модели не подтверждены. Выполните «Тест извлечения» в разделе «Администрирование → LLM» — он определит поддерживаемый режим структурированного ответа.");
        var worker = await Workers.ProbeAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new JobSnapshot
        {
            AppVersion = AppVersion,
            WorkerVersion = worker.WorkerVersion,
            PythonVersion = worker.PythonVersion,
            PandasVersion = worker.PandasVersion,
            Llm = LlmProfileSnapshot.From(profile, mode, !Fakt.Core.Llm.UrlBuilder.IsLocalOrPrivate(profile.BaseUrl)),
            Processing = new ProcessingSnapshot
            {
                ChunkSize = settings.Processing.ChunkSize,
                SqlBatchSize = settings.Processing.SqlBatchSize,
                QueueCapacity = settings.Processing.QueueCapacity,
                MaxAttemptsPerRequest = settings.Processing.MaxAttemptsPerRequest,
                BudgetMaxRequests = settings.Processing.BudgetMaxRequests,
                BudgetMaxTokens = settings.Processing.BudgetMaxTokens,
            },
            StructurePromptVersion = Prompts.StructurePromptVersion,
            FactsPromptVersion = Prompts.FactsPromptVersion,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = _authorization.CurrentUserName,
        };

        var storage = _storageFactory.Create(database, password);
        var jobId = await storage.Jobs.CreateJobAsync(new JobRecord
        {
            Status = JobStatus.Created,
            CreatedBy = _authorization.CurrentUserName,
            SnapshotJson = JsonConvert.SerializeObject(snapshot),
            Summary = $"Файлов: {files.Count}",
        }, cancellationToken).ConfigureAwait(false);
        Logger.Info("job.created", $"Создано задание {jobId}: файлов {files.Count}, модель {profile.ModelId}", e =>
        {
            e.JobId = jobId;
            e.User = _authorization.CurrentUserName;
        });

        // Параметры выполнения берутся из снимка, даже если настройки изменятся позже.
        var frozen = new LlmRuntimeConfig(snapshot.Llm.ToProfile(profile), runtime.ApiKey, runtime.Headers);
        return new JobSession(this, storage, frozen, mode, snapshot, files, report.Limits, jobId);
    }

    /// <summary>Потоковый хеш, регистрация версии источника (FileCode), запись файла задания.</summary>
    internal async Task<PreparedFile> PrepareFileAsync(IStorage storage, long jobId, JobFileInput input, JobSnapshot snapshot, CancellationToken cancellationToken)
    {
        var file = input.File;
        var before = CurrentFingerprint(file);
        if (!before.Equals(file.Fingerprint))
        {
            throw new FileChangedException("Файл изменён после сканирования; выполните сканирование и анализ структуры заново.");
        }

        var hash = await _hasher.ComputeSha256Async(file.FullPath, null, cancellationToken).ConfigureAwait(false);
        if (!CurrentFingerprint(file).Equals(before))
        {
            throw new FileChangedException("Файл изменялся во время подсчёта хеша; повторите обработку, когда запись в файл завершится.");
        }

        var structureJson = JsonConvert.SerializeObject(input.Structure);
        var source = await storage.SourceFiles.RegisterAsync(new SourceFileRegistration
        {
            FileName = file.Name,
            FullPath = file.FullPath,
            PathHash = Sha256(PathUtil.NormalizeForIdentity(file.FullPath)),
            Size = file.Size,
            LastWriteTimeUtc = file.LastWriteTimeUtc,
            ContentHash = hash,
        }, cancellationToken).ConfigureAwait(false);
        var version = ExtractionVersionCalculator.Compute(snapshot.Llm.ProviderId, snapshot.Llm.ModelId, snapshot.FactsPromptVersion, structureJson);
        var jobFileId = await storage.Jobs.AddJobFileAsync(new JobFileRecord
        {
            JobId = jobId,
            SourceFileId = source.Id,
            FileCode = source.FileCode,
            FileName = file.Name,
            SourcePath = file.FullPath,
            Status = FileStatus.Queued,
            StructureJson = structureJson,
            ExtractionVersion = version,
            ExpectedSize = file.Size,
            ExpectedLastWriteUtc = file.LastWriteTimeUtc,
            ContentHashHex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant(),
        }, cancellationToken).ConfigureAwait(false);
        return new PreparedFile
        {
            File = file,
            Structure = input.Structure,
            SourceFileId = source.Id,
            FileCode = source.FileCode,
            ContentHash = hash,
            ExtractionVersion = version,
            JobFileId = jobFileId,
        };
    }

    internal bool FingerprintMatches(ScannedFile file) => CurrentFingerprint(file).Equals(file.Fingerprint);

    private static FileFingerprint CurrentFingerprint(ScannedFile file)
    {
        var info = new System.IO.FileInfo(file.LongPath);
        if (!info.Exists)
        {
            throw new FileChangedException("Файл больше не существует: " + file.FullPath);
        }

        return new FileFingerprint(info.Length, info.LastWriteTimeUtc);
    }

    private static byte[] Sha256(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Продолжение задания после перезапуска приложения (статус «На паузе» или «Прервано»).
    /// Используется снимок параметров; ключ берётся из текущего профиля только если провайдер и адрес совпадают.
    /// </summary>
    public async Task<JobSession> ResumeJobAsync(long jobId, CancellationToken cancellationToken)
    {
        _authorization.Demand(Permission.ProcessData);
        var settings = _settings.Current;
        var password = _settings.SqlPassword(settings.Database);
        var storage = _storageFactory.Create(settings.Database, password);
        var job = await storage.Jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Задание {jobId} не найдено.");
        if (job.Status == JobStatus.Completed || job.Status == JobStatus.CompletedWithErrors)
        {
            throw new InvalidOperationException("Задание уже завершено.");
        }

        var snapshot = JsonConvert.DeserializeObject<JobSnapshot>(job.SnapshotJson);
        var profile = settings.LlmProfiles.FirstOrDefault(p => p.Id == snapshot.Llm.ProfileId)
                      ?? throw new InvalidOperationException("Профиль LLM, использованный заданием, удалён. Создайте новое задание.");
        if (!string.Equals(profile.ProviderId, snapshot.Llm.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(Fakt.Core.Llm.UrlBuilder.HostOf(profile.BaseUrl), Fakt.Core.Llm.UrlBuilder.HostOf(snapshot.Llm.BaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Провайдер или адрес профиля изменились после запуска задания: ключ не будет отправлен на другой адрес. Создайте новое задание.");
        }

        var descriptor = Registry.Providers.FirstOrDefault(p => p.Id == profile.ProviderId);
        var runtime = _settings.BuildRuntimeConfig(profile, descriptor);
        var frozen = new LlmRuntimeConfig(snapshot.Llm.ToProfile(profile), runtime.ApiKey, runtime.Headers);
        var report = await _databaseAdmin.InspectAsync(settings.Database, password, cancellationToken).ConfigureAwait(false);
        if (!report.CanProcess)
        {
            throw new InvalidOperationException("База данных не готова к записи: " + string.Join("; ", report.ProcessingBlockers.Take(5)));
        }

        var jobFiles = await storage.Jobs.ListJobFilesAsync(jobId, cancellationToken).ConfigureAwait(false);
        var inputs = new List<JobFileInput>();
        var prepared = new List<PreparedFile>();
        foreach (var record in jobFiles)
        {
            if (record.Status == FileStatus.Completed || record.Status == FileStatus.CompletedWithErrors)
            {
                continue;
            }

            var info = new System.IO.FileInfo(PathUtil.ToLongPath(record.SourcePath));
            var scanned = new ScannedFile(record.SourcePath, System.IO.Path.GetFileName(record.SourcePath), record.FileName,
                System.IO.Path.GetExtension(record.FileName), record.ExpectedSize, record.ExpectedLastWriteUtc, FileAttributesInfo.None);
            if (!info.Exists || info.Length != record.ExpectedSize || info.LastWriteTimeUtc != record.ExpectedLastWriteUtc)
            {
                await storage.Jobs.UpdateJobFileAsync(record.JobFileId, FileStatus.Error,
                    "Файл изменён или удалён после паузы: продолжение со старой позиции невозможно. Запустите обработку заново — будет создана новая версия источника.", null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var structure = JsonConvert.DeserializeObject<StructureDescriptor>(record.StructureJson);
            inputs.Add(new JobFileInput { File = scanned, Structure = structure });
            prepared.Add(new PreparedFile
            {
                File = scanned,
                Structure = structure,
                SourceFileId = record.SourceFileId,
                FileCode = record.FileCode,
                ExtractionVersion = record.ExtractionVersion,
                JobFileId = record.JobFileId,
            });
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("В задании не осталось файлов, которые можно продолжить (все завершены, изменены или удалены).");
        }

        // Бюджет — на всё задание: уже израсходованное учитывается; пределы — из текущих настроек, чтобы после
        // остановки по пределу его можно было поднять в «Администрирование → Обработка» и продолжить.
        var budget = new BudgetTracker(settings.Processing.BudgetMaxRequests, settings.Processing.BudgetMaxTokens, job.Requests, job.InputTokens, job.OutputTokens);
        var session = new JobSession(this, storage, frozen, snapshot.Llm.OutputMode, snapshot, inputs, report.Limits, jobId, budget);
        foreach (var file in prepared)
        {
            session.RegisterPrepared(file);
        }

        Logger.Info("job.resumed", $"Задание {jobId} продолжено: файлов {inputs.Count}", e =>
        {
            e.JobId = jobId;
            e.User = _authorization.CurrentUserName;
        });
        return session;
    }
}
