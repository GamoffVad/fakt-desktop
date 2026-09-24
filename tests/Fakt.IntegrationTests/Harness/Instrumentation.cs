using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Core.Structure;
using Fakt.Core.Worker;

namespace Fakt.Testing;

/// <summary>Гистограмма длительностей с фиксированным числом корзин: память не растёт с числом измерений.</summary>
public sealed class LatencyHistogram
{
    // Корзины по 1 мс до 2 с, дальше — одна корзина переполнения.
    private const int Buckets = 2000;
    private readonly long[] _counts = new long[Buckets + 1];
    private readonly object _gate = new();
    private long _total;
    private double _sumMs;
    private double _maxMs;

    public void Add(TimeSpan duration)
    {
        var ms = duration.TotalMilliseconds;
        lock (_gate)
        {
            _counts[Math.Min(Buckets, (int)ms)]++;
            _total++;
            _sumMs += ms;
            _maxMs = Math.Max(_maxMs, ms);
        }
    }

    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    public double TotalMs
    {
        get
        {
            lock (_gate)
            {
                return _sumMs;
            }
        }
    }

    public double MeanMs
    {
        get
        {
            lock (_gate)
            {
                return _total == 0 ? 0 : _sumMs / _total;
            }
        }
    }

    public double MaxMs
    {
        get
        {
            lock (_gate)
            {
                return _maxMs;
            }
        }
    }

    /// <summary>Перцентиль с точностью до 1 мс (верхняя граница корзины).</summary>
    public double Percentile(double p)
    {
        lock (_gate)
        {
            if (_total == 0)
            {
                return 0;
            }

            var rank = (long)Math.Ceiling(p / 100.0 * _total);
            long seen = 0;
            for (var i = 0; i <= Buckets; i++)
            {
                seen += _counts[i];
                if (seen >= rank)
                {
                    return i == Buckets ? _maxMs : i + 1;
                }
            }

            return _maxMs;
        }
    }
}

/// <summary>Контекст фиксации для хуков декоратора хранилища.</summary>
public sealed class CommitContext
{
    public int Index { get; set; }

    public CommitUnit Unit { get; set; }

    /// <summary>Настоящий писатель (SqlFactWriter).</summary>
    public IFactWriter Inner { get; set; }

    public CommitResult Result { get; set; }

    public Exception Error { get; set; }
}

public sealed class CommitStats
{
    private long _attempts;
    private long _succeeded;
    private long _failed;
    private long _duplicates;
    private long _rows;
    private long _maxRows;

    public LatencyHistogram Latency { get; } = new();

    public long Attempts => Interlocked.Read(ref _attempts);

    public long Succeeded => Interlocked.Read(ref _succeeded);

    public long Failed => Interlocked.Read(ref _failed);

    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>Строк в успешных фиксациях (без повторов-дубликатов).</summary>
    public long Rows => Interlocked.Read(ref _rows);

    public long MaxRowsPerCommit => Interlocked.Read(ref _maxRows);

    public double AverageRowsPerCommit => Succeeded - Duplicates <= 0 ? 0 : (double)Rows / (Succeeded - Duplicates);

    internal void Attempt() => Interlocked.Increment(ref _attempts);

    internal void Fail() => Interlocked.Increment(ref _failed);

    internal void Success(int rows, bool duplicate, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _succeeded);
        Latency.Add(elapsed);
        if (duplicate)
        {
            Interlocked.Increment(ref _duplicates);
            return;
        }

        Interlocked.Add(ref _rows, rows);
        long current;
        while (rows > (current = Interlocked.Read(ref _maxRows)) && Interlocked.CompareExchange(ref _maxRows, rows, current) != current)
        {
        }
    }
}

/// <summary>
/// Декоратор фабрики хранилища: всё делегируется настоящему SqlStorage/SqlFactWriter; добавлены измерение фиксаций
/// и точки внедрения отказов до и после настоящей фиксации.
/// </summary>
public sealed class InstrumentedStorageFactory : IStorageFactory
{
    private readonly IStorageFactory _inner;
    private int _commitIndex;

    public InstrumentedStorageFactory(IStorageFactory inner)
    {
        _inner = inner;
    }

    public CommitStats Stats { get; } = new();

    public Action<IFactWriter> WriterCreated { get; set; }

    public Func<CommitContext, Task> BeforeCommit { get; set; }

    public Func<CommitContext, Task> AfterCommit { get; set; }

    public Action<CommitContext> CommitFailed { get; set; }

    public IStorage Create(DatabaseSettings settings, string sqlPassword) => new Storage(_inner.Create(settings, sqlPassword), this);

    private sealed class Storage : IStorage
    {
        private readonly IStorage _inner;
        private readonly InstrumentedStorageFactory _owner;

        public Storage(IStorage inner, InstrumentedStorageFactory owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public ISourceFileRepository SourceFiles => _inner.SourceFiles;

        public IJobRepository Jobs => _inner.Jobs;

        public ISearchRepository Search => _inner.Search;

        public IFactWriter CreateWriter(Fakt.Core.Extraction.FieldLimits limits)
        {
            var writer = _inner.CreateWriter(limits);
            _owner.WriterCreated?.Invoke(writer);
            return new Writer(writer, _owner);
        }
    }

    private sealed class Writer : IFactWriter
    {
        private readonly IFactWriter _inner;
        private readonly InstrumentedStorageFactory _owner;

        public Writer(IFactWriter inner, InstrumentedStorageFactory owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public async Task<CommitResult> CommitAsync(CommitUnit unit, CancellationToken cancellationToken)
        {
            var context = new CommitContext { Index = Interlocked.Increment(ref _owner._commitIndex), Unit = unit, Inner = _inner };
            _owner.Stats.Attempt();
            if (_owner.BeforeCommit != null)
            {
                await _owner.BeforeCommit(context).ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                context.Result = await _inner.CommitAsync(unit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _owner.Stats.Fail();
                context.Error = ex;
                _owner.CommitFailed?.Invoke(context);
                throw;
            }

            _owner.Stats.Success(unit.Rows.Count, context.Result.Duplicate, stopwatch.Elapsed);
            if (_owner.AfterCommit != null)
            {
                await _owner.AfterCommit(context).ConfigureAwait(false);
            }

            return context.Result;
        }

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>Контекст вызова read_chunk для хуков декоратора worker.</summary>
public sealed class WorkerCallContext
{
    public int CallIndex { get; set; }

    public int? Pid { get; set; }

    /// <summary>Заранее открытый дескриптор процесса worker: завершение без поиска процесса (мгновенно).</summary>
    public Process Process { get; set; }

    public string ReaderId { get; set; }
}

public sealed class WorkerStats
{
    private long _chunks;
    private long _records;
    private long _clients;

    public LatencyHistogram ReadChunkLatency { get; } = new();

    public long Chunks => Interlocked.Read(ref _chunks);

    public long Records => Interlocked.Read(ref _records);

    public long ClientsCreated => Interlocked.Read(ref _clients);

    internal void Client() => Interlocked.Increment(ref _clients);

    internal void Chunk(int records, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _chunks);
        Interlocked.Add(ref _records, records);
        ReadChunkLatency.Add(elapsed);
    }
}

/// <summary>
/// Декоратор фабрики worker: делегирует настоящему PythonWorkerClient; узнаёт PID процесса командой hello
/// (для измерения памяти и имитации аварийного завершения), измеряет read_chunk и даёт точки внедрения отказов.
/// </summary>
public sealed class InstrumentedWorkerFactory : IWorkerClientFactory
{
    private readonly IWorkerClientFactory _inner;
    private int _readChunkCalls;

    public InstrumentedWorkerFactory(IWorkerClientFactory inner)
    {
        _inner = inner;
    }

    public WorkerStats Stats { get; } = new();

    /// <summary>PID процессов worker, открывших читатель; значение — последние измерения памяти процесса.</summary>
    public ConcurrentDictionary<int, ProcessMemory> ActivePids { get; } = new();

    /// <summary>Пиковые значения памяти по уже завершённым процессам worker (последнее измерение перед закрытием).</summary>
    public ConcurrentBag<ProcessMemory> FinishedWorkers { get; } = new();

    public Func<WorkerCallContext, Task> BeforeReadChunk { get; set; }

    /// <summary>Вызывается сразу после отправки read_chunk (ответ ещё не получен) — для «аварии во время чтения».</summary>
    public Func<WorkerCallContext, Task> AfterReadChunkSent { get; set; }

    public IWorkerClient Create()
    {
        Stats.Client();
        return new Client(_inner.Create(), this);
    }

    public Task<WorkerHello> ProbeAsync(CancellationToken cancellationToken) => _inner.ProbeAsync(cancellationToken);

    private sealed class Client : IWorkerClient
    {
        private readonly IWorkerClient _inner;
        private readonly InstrumentedWorkerFactory _owner;
        private int? _pid;
        private Process _process;

        public Client(IWorkerClient inner, InstrumentedWorkerFactory owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public bool IsFaulted => _inner.IsFaulted;

        public Task<WorkerHello> HelloAsync(CancellationToken cancellationToken) => _inner.HelloAsync(cancellationToken);

        public Task<SampleResult> SampleAsync(string path, int maxLines, int maxBytes, string encoding, CancellationToken cancellationToken) =>
            _inner.SampleAsync(path, maxLines, maxBytes, encoding, cancellationToken);

        public Task<ValidateResult> ValidateAsync(string path, StructureDescriptor structure, int maxRecords, int maxBytes, CancellationToken cancellationToken) =>
            _inner.ValidateAsync(path, structure, maxRecords, maxBytes, cancellationToken);

        public async Task<string> OpenReaderAsync(OpenReaderRequest request, CancellationToken cancellationToken)
        {
            if (_pid == null)
            {
                var hello = await _inner.HelloAsync(cancellationToken).ConfigureAwait(false);
                _pid = hello.Pid;
                try
                {
                    _process = Process.GetProcessById(hello.Pid);
                }
                catch (ArgumentException)
                {
                    _process = null;
                }

                _owner.ActivePids[hello.Pid] = MemoryProbe.Read(hello.Pid) ?? new ProcessMemory { Pid = hello.Pid };
            }

            return await _inner.OpenReaderAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RecordChunk> ReadChunkAsync(string readerId, CancellationToken cancellationToken)
        {
            var context = new WorkerCallContext { CallIndex = Interlocked.Increment(ref _owner._readChunkCalls), Pid = _pid, Process = _process, ReaderId = readerId };
            if (_owner.BeforeReadChunk != null)
            {
                await _owner.BeforeReadChunk(context).ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            var pending = _inner.ReadChunkAsync(readerId, cancellationToken);
            if (_owner.AfterReadChunkSent != null)
            {
                await _owner.AfterReadChunkSent(context).ConfigureAwait(false);
            }

            var chunk = await pending.ConfigureAwait(false);
            _owner.Stats.Chunk(chunk.Records.Count, stopwatch.Elapsed);
            return chunk;
        }

        public Task CloseReaderAsync(string readerId, CancellationToken cancellationToken) => _inner.CloseReaderAsync(readerId, cancellationToken);

        public void Dispose()
        {
            if (_pid.HasValue && _owner.ActivePids.TryRemove(_pid.Value, out var last))
            {
                // Последнее измерение до завершения процесса: пиковые счётчики ОС монотонны.
                _owner.FinishedWorkers.Add(MemoryProbe.Read(_pid.Value) ?? last);
            }

            _inner.Dispose();
            _process?.Dispose();
        }
    }
}

public static class EnumerableExtensions
{
    public static double MedianOrZero(this IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
