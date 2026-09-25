using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Logging;
using Fakt.Core.Structure;
using Fakt.Core.Worker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fakt.Infrastructure.Worker;

public sealed class WorkerLaunchInfo
{
    public string PythonPath { get; set; }

    public string WorkerDirectory { get; set; }

    public string ScriptPath => Path.Combine(WorkerDirectory, "fakt_worker_main.py");

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Клиент процесса Python worker (протокол v1: JSON Lines через stdin/stdout, диагностика — stderr).
/// Запросы выполняются строго по одному. Секреты и пути к данным в командную строку не передаются.
/// </summary>
public sealed class PythonWorkerClient : IWorkerClient
{
    private const int ProtocolVersion = 1;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly WorkerLaunchInfo _launch;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<string> _stderrTail = new();
    private readonly object _stderrGate = new();

    // Читатель существует только в процессе, который его открыл.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _readerOwners = new(StringComparer.Ordinal);
    private Process _process;
    private StreamWriter _stdin;
    private Task _readerTask;
    private TaskCompletionSource<JObject> _pending;
    private string _pendingId;
    private long _nextId;
    private volatile bool _faulted;
    private bool _disposed;

    public PythonWorkerClient(WorkerLaunchInfo launch, IAppLogger logger)
    {
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsFaulted => _faulted;

    public async Task<WorkerHello> HelloAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("hello", new JObject(), TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        var hello = result.ToObject<WorkerHello>();
        if (hello.ProtocolVersion != ProtocolVersion)
        {
            throw new WorkerException("unsupported_protocol_version", $"Worker поддерживает протокол {hello.ProtocolVersion}, клиент — {ProtocolVersion}.");
        }

        return hello;
    }

    public async Task<SampleResult> SampleAsync(string path, int maxLines, int maxBytes, string encoding, CancellationToken cancellationToken)
    {
        var args = new JObject
        {
            ["path"] = path,
            ["max_lines"] = maxLines,
            ["max_bytes"] = maxBytes,
            ["encoding"] = encoding,
        };
        var result = await SendAsync("sample", args, _launch.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return result.ToObject<SampleResult>();
    }

    public async Task<ValidateResult> ValidateAsync(string path, StructureDescriptor structure, int maxRecords, int maxBytes, CancellationToken cancellationToken)
    {
        var args = new JObject
        {
            ["path"] = path,
            ["structure"] = JObject.FromObject(structure),
            ["max_records"] = maxRecords,
            ["max_bytes"] = maxBytes,
        };
        var result = await SendAsync("validate", args, _launch.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return result.ToObject<ValidateResult>();
    }

    public async Task<string> OpenReaderAsync(OpenReaderRequest request, CancellationToken cancellationToken)
    {
        var args = new JObject
        {
            ["path"] = request.Path,
            ["structure"] = JObject.FromObject(request.Structure),
            ["chunk_size"] = request.ChunkSize,
            ["start_after_ordinal"] = request.StartAfterOrdinal,
            ["max_chunk_bytes"] = request.MaxChunkBytes,
            ["expected_size"] = request.ExpectedSize,
            ["expected_mtime_ns"] = request.ExpectedMtimeNs,
        };
        var result = await SendAsync("open_reader", args, _launch.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var readerId = (string)result["reader_id"];
        var owner = _process;
        if (readerId != null && owner != null)
        {
            _readerOwners[readerId] = owner;
        }

        return readerId;
    }

    public async Task<RecordChunk> ReadChunkAsync(string readerId, CancellationToken cancellationToken)
    {
        EnsureReaderProcessAlive(readerId);
        // Пропуск до границы возобновления может потребовать перечитывания большой части файла.
        var result = await SendAsync("read_chunk", new JObject { ["reader_id"] = readerId }, TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);
        return result.ToObject<RecordChunk>();
    }

    public async Task CloseReaderAsync(string readerId, CancellationToken cancellationToken)
    {
        _readerOwners.TryRemove(readerId ?? string.Empty, out _);
        if (_faulted || _process == null)
        {
            return;
        }

        await SendAsync("close_reader", new JObject { ["reader_id"] = readerId }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Состояние читателя живёт только в процессе, который его открыл. Если этот процесс завершился (авария,
    /// принудительное завершение), перезапуск worker читателя не восстановит: новый процесс ответил бы
    /// «reader_not_found» и скрыл бы настоящую причину. Поэтому сообщается сбой worker.
    /// </summary>
    private void EnsureReaderProcessAlive(string readerId)
    {
        if (readerId == null || !_readerOwners.TryGetValue(readerId, out var owner))
        {
            return;
        }

        string exitCode = null;
        var alive = ReferenceEquals(owner, _process) && !_faulted;
        try
        {
            if (owner.HasExited)
            {
                alive = false;
                exitCode = owner.ExitCode.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже заменён новым и освобождён.
            alive = false;
        }

        if (!alive)
        {
            _readerOwners.TryRemove(readerId, out _);
            throw new WorkerException("worker_crashed",
                $"Процесс worker завершился{(exitCode != null ? " (код " + exitCode + ")" : string.Empty)}: открытое чтение файла прервано.{StderrSummary()}");
        }
    }

    private async Task<JObject> SendAsync(string command, JObject args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
            var request = new JObject { ["v"] = ProtocolVersion, ["id"] = id, ["cmd"] = command, ["args"] = args };
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            _pendingId = id;
            try
            {
                await _stdin.WriteLineAsync(request.ToString(Formatting.None)).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                Fault();
                throw new WorkerException("worker_crashed", "Канал связи с worker разорван: " + ex.Message + StderrSummary(), null, ex);
            }

            var stopwatch = Stopwatch.StartNew();
            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            {
                var cancelled = new TaskCompletionSource<bool>();
                using (linked.Token.Register(() => cancelled.TrySetResult(true)))
                {
                    var completed = await Task.WhenAny(tcs.Task, cancelled.Task).ConfigureAwait(false);
                    if (completed != tcs.Task)
                    {
                        // Команду нельзя прервать внутри worker: процесс завершается и будет перезапущен при следующем запросе.
                        Kill();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw new WorkerException("worker_timeout", $"Worker не ответил на команду {command} за {timeout.TotalSeconds:0} с; процесс перезапущен.");
                    }
                }
            }

            var response = await tcs.Task.ConfigureAwait(false);
            _logger.Debug("worker.command", $"Команда worker {command} выполнена", e =>
            {
                e.Stage = "worker";
                e.DurationMs = stopwatch.ElapsedMilliseconds;
            });
            if ((bool?)response["ok"] == true)
            {
                return response["result"] as JObject ?? new JObject();
            }

            var error = response["error"] as JObject ?? new JObject();
            throw new WorkerException((string)error["code"] ?? "internal_error", (string)error["message"] ?? "Ошибка worker", error["details"] as JObject);
        }
        finally
        {
            _pending = null;
            _pendingId = null;
            _gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PythonWorkerClient));
        }

        if (_process != null && !_faulted && !_process.HasExited)
        {
            return;
        }

        CleanupProcess();
        if (!File.Exists(_launch.PythonPath))
        {
            throw new WorkerException("worker_start_failed", $"Не найден интерпретатор Python worker: {_launch.PythonPath}. Папка worker из поставки должна находиться рядом с FAKT.exe — переустановите или распакуйте программу заново.");
        }

        if (!File.Exists(_launch.ScriptPath))
        {
            throw new WorkerException("worker_start_failed", $"Не найден скрипт worker: {_launch.ScriptPath}.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _launch.PythonPath,
            Arguments = "-I -X utf8 -u \"" + _launch.ScriptPath + "\"",
            WorkingDirectory = _launch.WorkerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var variable in new[] { "PYTHONHOME", "PYTHONPATH", "PYTHONSTARTUP", "PYTHONINSPECT" })
        {
            startInfo.EnvironmentVariables.Remove(variable);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new WorkerException("worker_start_failed", $"Не удалось запустить worker: {ex.Message}", null, ex);
        }

        _process = process;
        _faulted = false;
        // В .NET Framework 4.8 нет StandardInputEncoding: без явной обёртки stdin использовал бы OEM-кодировку консоли.
        _stdin = new StreamWriter(process.StandardInput.BaseStream, Utf8NoBom, 64 * 1024) { AutoFlush = false, NewLine = "\n" };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (_stderrGate)
            {
                _stderrTail.AddLast(e.Data.Length > 500 ? e.Data.Substring(0, 500) : e.Data);
                while (_stderrTail.Count > 40)
                {
                    _stderrTail.RemoveFirst();
                }
            }

            _logger.Debug("worker.stderr", e.Data, entry => entry.Stage = "worker");
        };
        process.BeginErrorReadLine();
        var stdout = new StreamReader(process.StandardOutput.BaseStream, Utf8NoBom, false, 1024 * 1024);
        _readerTask = Task.Factory.StartNew(() => ReadLoop(process, stdout), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _logger.Info("worker.started", "Запущен процесс worker", e => e.Data = new Dictionary<string, object> { ["pid"] = process.Id });
    }

    private void ReadLoop(Process process, StreamReader stdout)
    {
        try
        {
            string line;
            while ((line = stdout.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JObject message;
                try
                {
                    message = JObject.Parse(line);
                }
                catch (JsonException)
                {
                    _logger.Warn("worker.protocol", "Строка stdout worker не является JSON и проигнорирована", e => e.Stage = "worker");
                    continue;
                }

                var id = (string)message["id"];
                var pending = _pending;
                if (pending != null && (id == _pendingId || id == null))
                {
                    pending.TrySetResult(message);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _faulted = true;
        var exitCode = "?";
        try
        {
            if (process.WaitForExit(2000))
            {
                exitCode = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _pending?.TrySetException(new WorkerException("worker_crashed", $"Процесс worker завершился (код {exitCode}).{StderrSummary()}"));
    }

    private string StderrSummary()
    {
        lock (_stderrGate)
        {
            if (_stderrTail.Count == 0)
            {
                return string.Empty;
            }

            return " Последние сообщения worker: " + string.Join(" | ", _stderrTail.Skip(Math.Max(0, _stderrTail.Count - 5)));
        }
    }

    private void Fault()
    {
        _faulted = true;
        Kill();
    }

    private void Kill()
    {
        _faulted = true;
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void CleanupProcess()
    {
        if (_process == null)
        {
            return;
        }

        Kill();
        try
        {
            _stdin?.Dispose();
        }
        catch (IOException)
        {
        }

        _process.Dispose();
        _process = null;
        _stdin = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_process != null && !_process.HasExited && !_faulted)
            {
                _stdin.WriteLine(new JObject { ["v"] = ProtocolVersion, ["id"] = "shutdown", ["cmd"] = "shutdown", ["args"] = new JObject() }.ToString(Formatting.None));
                _stdin.Flush();
                _stdin.Close();
                _process.WaitForExit(3000);
            }
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        CleanupProcess();
        _gate.Dispose();
    }
}

/// <summary>Поиск поставляемого Python и каталога worker; в режиме разработки — каталог worker репозитория.</summary>
public sealed class WorkerClientFactory : IWorkerClientFactory
{
    private readonly Func<WorkerLaunchInfo> _resolve;
    private readonly IAppLogger _logger;

    public WorkerClientFactory(Func<WorkerLaunchInfo> resolve, IAppLogger logger)
    {
        _resolve = resolve;
        _logger = logger;
    }

    public IWorkerClient Create() => new PythonWorkerClient(_resolve(), _logger);

    public async Task<WorkerHello> ProbeAsync(CancellationToken cancellationToken)
    {
        using var client = Create();
        return await client.HelloAsync(cancellationToken).ConfigureAwait(false);
    }

    public static WorkerLaunchInfo Resolve(string configuredPython, string configuredWorkerDir, string applicationDirectory)
    {
        var workerDir = !string.IsNullOrWhiteSpace(configuredWorkerDir)
            ? configuredWorkerDir
            : FindWorkerDirectory(applicationDirectory);
        var python = !string.IsNullOrWhiteSpace(configuredPython)
            ? configuredPython
            : FindPython(applicationDirectory, workerDir);
        return new WorkerLaunchInfo { PythonPath = python, WorkerDirectory = workerDir };
    }

    private static string FindWorkerDirectory(string applicationDirectory)
    {
        var bundled = Path.Combine(applicationDirectory, "worker");
        if (File.Exists(Path.Combine(bundled, "fakt_worker_main.py")))
        {
            return bundled;
        }

        // Запуск из каталога сборки репозитория: ищем worker/ вверх по дереву.
        var current = new DirectoryInfo(applicationDirectory);
        for (var i = 0; i < 8 && current != null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "worker");
            if (File.Exists(Path.Combine(candidate, "fakt_worker_main.py")))
            {
                return candidate;
            }
        }

        return bundled;
    }

    private static string FindPython(string applicationDirectory, string workerDir)
    {
        var candidates = new List<string>
        {
            Path.Combine(applicationDirectory, "worker", "python", "python.exe"),
            Path.Combine(workerDir ?? string.Empty, "python", "python.exe"),
            Path.Combine(workerDir ?? string.Empty, "build", "out", "python", "python.exe"),
        };
        var fromEnvironment = Environment.GetEnvironmentVariable("FAKT_PYTHON");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            candidates.Insert(0, fromEnvironment);
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
