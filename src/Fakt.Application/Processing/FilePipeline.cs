using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Common;
using Fakt.Application.Extraction;
using Fakt.Application.Llm;
using Fakt.Core.Extraction;
using Fakt.Core.Llm;
using Fakt.Core.Logging;
using Fakt.Core.Processing;
using Fakt.Core.Records;
using Fakt.Core.Storage;
using Fakt.Core.Worker;

namespace Fakt.Application.Processing;

/// <summary>Управление заданием из интерфейса: пауза (завершить начатое и сохранить) и остановка.</summary>
public sealed class JobControl
{
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _pauseRequested;

    public bool PauseRequested => _pauseRequested;

    public CancellationToken StopToken => _stop.Token;

    public bool StopRequested => _stop.IsCancellationRequested;

    public event Action Changed;

    /// <summary>Пауза: новые пакеты не выдаются, начатые запросы завершаются и сохраняются.</summary>
    public void RequestPause()
    {
        _pauseRequested = true;
        Changed?.Invoke();
    }

    public void ClearPause() => _pauseRequested = false;

    /// <summary>Остановка: начатые запросы отменяются на стороне клиента (провайдер мог их уже принять и оплатить).</summary>
    public void RequestStop()
    {
        _stop.Cancel();
        Changed?.Invoke();
    }
}

public enum PipelineOutcome
{
    Completed,
    Paused,
    Stopped,
    Failed,
    BudgetExceeded,
    FileChanged,
    ServiceUnavailable,
}

public sealed class PipelineProgress
{
    public long RecordsRead { get; set; }
    public long RecordsSkipped { get; set; }
    public long Committed { get; set; }
    public long Extracted { get; set; }
    public long NoFacts { get; set; }
    public long Errors { get; set; }
    public long Observations { get; set; }
    public long Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ConfirmedOrdinal { get; set; }
    public long BadRecords { get; set; }
    public bool Eof { get; set; }
    public string LastWarning { get; set; }

    public PipelineProgress Clone() => (PipelineProgress)MemberwiseClone();
}

public sealed class FilePipelineResult
{
    public PipelineOutcome Outcome { get; set; }
    public string Message { get; set; }
    public PipelineProgress Progress { get; set; }
}

public sealed class PipelineOptions
{
    public int ChunkSize { get; set; } = 5000;
    public int SqlBatchSize { get; set; } = 500;
    public int QueueCapacity { get; set; } = 32;
    public int MaxInputTokens { get; set; } = 6000;
    public int SqlRetryAttempts { get; set; } = 5;
}

/// <summary>
/// Конвейер обработки одного файла: чтение (chunk Pandas) → очередь LLM (пакеты по числу записей и токенам)
/// → проверка → буфер упорядочивания → SQL-фиксация с checkpoint. Все очереди ограничены «окном»:
/// при заполнении чтение ждёт, память не растёт с размером файла. Фиксация идёт строго в порядке
/// source_row_id, поэтому граница подтверждённой обработки непрерывна.
/// </summary>
public sealed class FilePipeline
{
    private readonly PreparedFile _file;
    private readonly IWorkerClientFactory _workers;
    private readonly IFactWriter _writer;
    private readonly BatchExtractor _extractor;
    private readonly BudgetTracker _budget;
    private readonly JobControl _control;
    private readonly PipelineOptions _options;
    private readonly AdaptiveBatchSize _batchSize;
    private readonly TokenEstimator _estimator;
    private readonly long _jobId;
    private readonly long _startAfter;
    private readonly int _concurrency;
    private readonly IAppLogger _logger;
    private readonly IDelay _delay;
    private readonly IProgress<PipelineProgress> _progress;
    private readonly PipelineProgress _state = new();
    private readonly object _stateGate = new();

    private readonly SortedDictionary<long, WorkResult> _reorder = new();
    private readonly object _reorderGate = new();
    private readonly SemaphoreSlim _resultSignal = new(0, int.MaxValue);

    private CancellationTokenSource _dispatch;
    private volatile Exception _failure;

    // Записывается до отмены диспетчеризации и читается после ожидания всех задач (Task.WhenAll даёт барьер памяти).
    private PipelineOutcome? _failureOutcome;
    private long _lastRequests;
    private long _lastInputTokens;
    private long _lastOutputTokens;

    public FilePipeline(PreparedFile file, long jobId, long startAfterOrdinal, IWorkerClientFactory workers, IFactWriter writer, BatchExtractor extractor,
        BudgetTracker budget, JobControl control, PipelineOptions options, AdaptiveBatchSize batchSize, TokenEstimator estimator, int concurrency,
        IAppLogger logger, IProgress<PipelineProgress> progress, IDelay delay = null)
    {
        _file = file;
        _jobId = jobId;
        _startAfter = startAfterOrdinal;
        _workers = workers;
        _writer = writer;
        _extractor = extractor;
        _budget = budget;
        _control = control;
        _options = options;
        _batchSize = batchSize;
        _estimator = estimator;
        _concurrency = Math.Max(1, concurrency);
        _logger = logger ?? NullLogger.Instance;
        _progress = progress;
        _delay = delay ?? RealDelay.Instance;
        _state.ConfirmedOrdinal = startAfterOrdinal;
    }

    private sealed class WorkItem
    {
        public long Seq { get; set; }
        public IReadOnlyList<SourceRecord> Records { get; set; }
        public List<RowOutcome> Ready { get; set; }
        public long MaxOrdinal { get; set; }
    }

    private sealed class WorkResult
    {
        public List<RowOutcome> Rows { get; set; }
        public long MaxOrdinal { get; set; }
    }

    public async Task<FilePipelineResult> RunAsync()
    {
        _lastRequests = _budget.Requests;
        _lastInputTokens = _budget.InputTokens;
        _lastOutputTokens = _budget.OutputTokens;
        using (_dispatch = CancellationTokenSource.CreateLinkedTokenSource(_control.StopToken))
        {
            var window = new SemaphoreSlim(_options.QueueCapacity + _concurrency + 2);
            var queue = new AsyncBoundedQueue<WorkItem>(Math.Max(1, _options.QueueCapacity));
            var producerCount = 0L;
            var readerDone = false;
            using var pauseWatcher = new Timer(_ =>
            {
                // Timer.Dispose не ждёт выполняющийся обратный вызов: источник отмены может быть уже освобождён,
                // а необработанное исключение в обратном вызове таймера завершило бы процесс.
                try
                {
                    if (_control.PauseRequested && !_dispatch.IsCancellationRequested)
                    {
                        _dispatch.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }, null, 200, 200);

            var reader = Task.Run(async () =>
            {
                try
                {
                    producerCount = await ReadAsync(queue, window).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_dispatch.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
                finally
                {
                    Volatile.Write(ref readerDone, true);
                    queue.Complete();
                    _resultSignal.Release();
                }
            });

            var workers = Enumerable.Range(0, _concurrency).Select(_ => Task.Run(() => WorkAsync(queue))).ToArray();
            var allWorkers = Task.WhenAll(workers).ContinueWith(_ => _resultSignal.Release(), TaskScheduler.Default);
            var committedItems = await CommitLoopAsync(window, () => Volatile.Read(ref readerDone) && workers.All(w => w.IsCompleted)).ConfigureAwait(false);
            await Task.WhenAll(reader, allWorkers).ConfigureAwait(false);
            bool eof;
            lock (_stateGate)
            {
                eof = _state.Eof;
            }

            // Файл завершён, только если прочитан до конца и зафиксированы все сформированные элементы.
            return Finish(eof && committedItems == Interlocked.Read(ref producerCount));
        }
    }

    private async Task<long> ReadAsync(AsyncBoundedQueue<WorkItem> queue, SemaphoreSlim window)
    {
        var token = _dispatch.Token;
        using var worker = _workers.Create();
        var readerId = await worker.OpenReaderAsync(new OpenReaderRequest
        {
            Path = _file.File.LongPath,
            Structure = _file.Structure,
            ChunkSize = _options.ChunkSize,
            StartAfterOrdinal = _startAfter,
            ExpectedSize = _file.File.Size,
            ExpectedMtimeNs = _file.File.Fingerprint.LastWriteUnixNanoseconds,
        }, token).ConfigureAwait(false);

        long seq = 0;
        var planner = new BatchPlanner(_estimator, TokenEstimator.EstimateRaw(Prompts.FactsSystem) + TokenEstimator.EstimateRaw(JsonSchemas.Extraction().ToString(Newtonsoft.Json.Formatting.None)) + 400);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var chunk = await worker.ReadChunkAsync(readerId, token).ConfigureAwait(false);
            var records = SourceRecord.FromChunk(chunk);
            lock (_stateGate)
            {
                _state.RecordsRead += records.Count;
                _state.RecordsSkipped = chunk.Stats?.RecordsSkipped ?? _state.RecordsSkipped;
                _state.BadRecords = chunk.Stats?.BadRecords ?? _state.BadRecords;
                if (chunk.Warnings?.Count > 0)
                {
                    _state.LastWarning = chunk.Warnings.Last().ToString();
                }
            }

            foreach (var warning in chunk.Warnings ?? new List<WorkerIssue>())
            {
                _logger.Warn("pipeline.structure_warning", warning.ToString(), e =>
                {
                    e.JobId = _jobId;
                    e.FileId = _file.SourceFileId;
                    e.Stage = "parse";
                    e.ErrorCode = warning.Code;
                    e.Category = ErrorCategory.DataFormat;
                });
            }

            foreach (var item in Plan(records, planner))
            {
                await window.WaitAsync(token).ConfigureAwait(false);
                item.Seq = seq++;
                await queue.EnqueueAsync(item, token).ConfigureAwait(false);
            }

            Report();
            if (chunk.Eof)
            {
                lock (_stateGate)
                {
                    _state.Eof = true;
                }

                break;
            }
        }

        await worker.CloseReaderAsync(readerId, CancellationToken.None).ConfigureAwait(false);
        return seq;
    }

    /// <summary>Разбиение прочитанных записей на элементы в порядке номеров: пакеты для модели и готовые ошибки разбора.</summary>
    private IEnumerable<WorkItem> Plan(List<SourceRecord> records, BatchPlanner planner)
    {
        var run = new List<SourceRecord>();
        IEnumerable<WorkItem> FlushRun()
        {
            if (run.Count == 0)
            {
                yield break;
            }

            var batches = new List<PlannedBatch>();
            var oversized = new List<RowOutcome>();
            planner.Plan(run, _batchSize.Rows, _options.MaxInputTokens, batches, oversized);
            // Слишком большие записи и пакеты перемешиваются по порядку номеров.
            var items = batches.Select(b => new WorkItem { Records = b.Records, MaxOrdinal = b.Records.Max(r => r.Ordinal) })
                .Concat(oversized.Select(o => new WorkItem { Ready = new List<RowOutcome> { o }, MaxOrdinal = o.Ordinal }))
                .OrderBy(i => i.Records?.Min(r => r.Ordinal) ?? i.MaxOrdinal);
            foreach (var item in items)
            {
                yield return item;
            }

            run = new List<SourceRecord>();
        }

        foreach (var record in records)
        {
            if (record.HasError)
            {
                foreach (var item in FlushRun())
                {
                    yield return item;
                }

                yield return new WorkItem
                {
                    Ready = new List<RowOutcome> { RowOutcome.Error(record.Ordinal, record.Line, record.Hash, record.ErrorCode, record.ErrorMessage) },
                    MaxOrdinal = record.Ordinal,
                };
                continue;
            }

            run.Add(record);
        }

        foreach (var item in FlushRun())
        {
            yield return item;
        }
    }

    private async Task WorkAsync(AsyncBoundedQueue<WorkItem> queue)
    {
        while (true)
        {
            (bool Success, WorkItem Item) next;
            try
            {
                next = await queue.DequeueAsync(_dispatch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!next.Success)
            {
                return;
            }

            var item = next.Item;
            List<RowOutcome> rows;
            try
            {
                rows = item.Ready ?? await _extractor.ExtractAsync(item.Records, _control.StopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_control.StopRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }

            lock (_reorderGate)
            {
                _reorder[item.Seq] = new WorkResult { Rows = rows, MaxOrdinal = item.MaxOrdinal };
            }

            _resultSignal.Release();
        }
    }

    /// <summary>Фиксирует результаты строго по порядку; возвращает число зафиксированных элементов.</summary>
    private async Task<long> CommitLoopAsync(SemaphoreSlim window, Func<bool> producersDone)
    {
        long expected = 0;
        while (true)
        {
            var batch = new List<WorkResult>();
            lock (_reorderGate)
            {
                var rows = 0;
                while (_reorder.TryGetValue(expected + batch.Count, out var ready) && (batch.Count == 0 || rows + ready.Rows.Count <= _options.SqlBatchSize))
                {
                    batch.Add(ready);
                    rows += ready.Rows.Count;
                    _reorder.Remove(expected + batch.Count - 1);
                }
            }

            if (batch.Count > 0)
            {
                var committed = await CommitWithRetryAsync(batch).ConfigureAwait(false);
                if (!committed)
                {
                    return expected;
                }

                expected += batch.Count;
                window.Release(batch.Count);
                Report();
                continue;
            }

            if (producersDone())
            {
                // Всё, что можно зафиксировать непрерывно, зафиксировано. Результаты после «дыры» (остановка) отбрасываются.
                bool hasNext;
                lock (_reorderGate)
                {
                    hasNext = _reorder.ContainsKey(expected);
                }

                if (!hasNext)
                {
                    return expected;
                }

                continue;
            }

            await _resultSignal.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    private async Task<bool> CommitWithRetryAsync(List<WorkResult> batch)
    {
        var rows = batch.SelectMany(b => b.Rows).OrderBy(r => r.Ordinal).ToList();
        var unit = new CommitUnit
        {
            JobId = _jobId,
            JobFileId = _file.JobFileId,
            SourceFileId = _file.SourceFileId,
            ExtractionVersion = _file.ExtractionVersion,
            FileName = _file.File.Name,
            FileCode = _file.FileCode,
            Rows = rows,
            ToOrdinal = batch.Max(b => b.MaxOrdinal),
            Usage = TakeUsageDelta(),
        };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Фиксация не отменяется паузой и остановкой: начатые результаты сохраняются.
                var result = await _writer.CommitAsync(unit, CancellationToken.None).ConfigureAwait(false);
                lock (_stateGate)
                {
                    _state.Committed += rows.Count;
                    _state.Extracted += result.ExtractedRows;
                    _state.NoFacts += result.NoFactsRows;
                    _state.Errors += result.ErrorRows;
                    _state.Observations += result.ObservationsInserted;
                    _state.ConfirmedOrdinal = Math.Max(_state.ConfirmedOrdinal, result.ConfirmedOrdinal);
                }

                return true;
            }
            catch (Exception ex) when (IsTransientDbError(ex) && attempt < _options.SqlRetryAttempts)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                _logger.Warn("pipeline.sql_retry", $"Ошибка записи в базу, повтор через {wait.TotalSeconds:0} с (попытка {attempt}): {ex.Message}", e =>
                {
                    e.JobId = _jobId;
                    e.FileId = _file.SourceFileId;
                    e.Stage = "sql";
                    e.Category = ErrorCategory.Connection;
                });
                await _delay.DelayAsync(wait, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _failureOutcome = PipelineOutcome.ServiceUnavailable;
                Fail(new InvalidOperationException("Не удалось сохранить результаты в базу данных: " + ex.Message +
                                                   ". Обработка поставлена на паузу; сохранённые записи не потеряны, продолжение начнётся с подтверждённой границы.", ex));
                return false;
            }
        }
    }

    private static bool IsTransientDbError(Exception ex)
    {
        if (ex is System.Data.SqlClient.SqlException sql)
        {
            // Тайм-аут, разрыв соединения, взаимоблокировка, недоступность сервера.
            return sql.Number == -2 || sql.Number == 1205 || sql.Number == 53 || sql.Number == 10053 || sql.Number == 10054 ||
                   sql.Number == 10060 || sql.Number == 233 || sql.Number == 64 || sql.Number == 40613 || sql.Class >= 20;
        }

        return ex is System.IO.IOException || ex is TimeoutException;
    }

    private UsageDelta TakeUsageDelta()
    {
        var requests = _budget.Requests;
        var input = _budget.InputTokens;
        var output = _budget.OutputTokens;
        var delta = new UsageDelta
        {
            Requests = requests - Interlocked.Exchange(ref _lastRequests, requests),
            InputTokens = input - Interlocked.Exchange(ref _lastInputTokens, input),
            OutputTokens = output - Interlocked.Exchange(ref _lastOutputTokens, output),
        };
        lock (_stateGate)
        {
            _state.Requests += delta.Requests;
            _state.InputTokens += delta.InputTokens;
            _state.OutputTokens += delta.OutputTokens;
            delta.RecordsRead = _startAfter + _state.RecordsRead;
        }

        return delta;
    }

    /// <summary>Использование токенов после последней фиксации (например, запросы, завершившиеся ошибкой).</summary>
    public UsageDelta RemainingUsage() => TakeUsageDelta();

    private void Fail(Exception ex)
    {
        if (_failure == null)
        {
            _failure = ex;
            _failureOutcome ??= ex switch
            {
                BudgetExceededException => PipelineOutcome.BudgetExceeded,
                WorkerException { IsFileChanged: true } => PipelineOutcome.FileChanged,
                LlmException { IsTransient: true } => PipelineOutcome.ServiceUnavailable,
                _ => PipelineOutcome.Failed,
            };
            _logger.Error("pipeline.failed", ex.Message, ex is LlmException ? ErrorCategory.ModelResponse : ex is WorkerException ? ErrorCategory.DataFormat : ErrorCategory.Internal,
                ex, e =>
                {
                    e.JobId = _jobId;
                    e.FileId = _file.SourceFileId;
                    e.Stage = "pipeline";
                });
        }

        try
        {
            _dispatch?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Report()
    {
        PipelineProgress snapshot;
        lock (_stateGate)
        {
            snapshot = _state.Clone();
        }

        _progress?.Report(snapshot);
    }

    private FilePipelineResult Finish(bool allCommitted)
    {
        PipelineProgress progress;
        lock (_stateGate)
        {
            progress = _state.Clone();
        }
        if (_failure != null)
        {
            var message = _failure switch
            {
                WorkerException { IsFileChanged: true } =>
                    "Файл изменён после начала обработки: продолжение со старой позиции невозможно. Запустите обработку заново — будет создана новая версия источника.",
                WorkerException worker => "Ошибка чтения файла: " + worker.Message,
                LlmException llm => llm.UserMessage,
                _ => _failure.Message,
            };
            return new FilePipelineResult { Outcome = _failureOutcome ?? PipelineOutcome.Failed, Message = message, Progress = progress };
        }

        if (_control.StopRequested)
        {
            return new FilePipelineResult { Outcome = PipelineOutcome.Stopped, Message = "Обработка остановлена. Уже принятые провайдером запросы могли быть выполнены и оплачены.", Progress = progress };
        }

        if (!allCommitted)
        {
            return new FilePipelineResult { Outcome = PipelineOutcome.Paused, Message = "Обработка на паузе: начатые пакеты сохранены, продолжение — с подтверждённой границы.", Progress = progress };
        }

        return new FilePipelineResult { Outcome = PipelineOutcome.Completed, Progress = progress };
    }
}
