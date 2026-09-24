using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Fakt.Testing;

/// <summary>Счётчики памяти процесса (GetProcessMemoryInfo). Пиковые значения ведёт ОС — они не зависят от частоты опроса.</summary>
public sealed class ProcessMemory
{
    public int Pid { get; set; }

    /// <summary>Private bytes (PrivateUsage, закреплённая частная память).</summary>
    public long PrivateBytes { get; set; }

    public long WorkingSet { get; set; }

    /// <summary>Пик рабочего набора за время жизни процесса (ОС).</summary>
    public long PeakWorkingSet { get; set; }

    /// <summary>Пик закреплённой частной памяти за время жизни процесса (PeakPagefileUsage, ОС).</summary>
    public long PeakPrivateBytes { get; set; }

    public DateTime AtUtc { get; set; }
}

public static class MemoryProbe
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static readonly int CurrentPid = Process.GetCurrentProcess().Id;

    public static ProcessMemory ReadCurrent() => Read(GetCurrentProcess(), CurrentPid);

    /// <summary>null — процесс уже завершён или недоступен.</summary>
    public static ProcessMemory Read(int pid)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero)
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Read(handle, pid);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static ProcessMemory Read(IntPtr handle, int pid)
    {
        if (!GetProcessMemoryInfo(handle, out var counters, (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx))))
        {
            return null;
        }

        return new ProcessMemory
        {
            Pid = pid,
            PrivateBytes = (long)counters.PrivateUsage.ToUInt64(),
            WorkingSet = (long)counters.WorkingSetSize.ToUInt64(),
            PeakWorkingSet = (long)counters.PeakWorkingSetSize.ToUInt64(),
            PeakPrivateBytes = (long)counters.PeakPagefileUsage.ToUInt64(),
            AtUtc = DateTime.UtcNow,
        };
    }
}

/// <summary>Точка временного ряда памяти и прогресса.</summary>
public sealed class MemorySample
{
    public double Seconds { get; set; }
    public double DotNetPrivateMb { get; set; }
    public double DotNetWorkingSetMb { get; set; }
    public double ManagedHeapMb { get; set; }
    public double PythonPrivateMb { get; set; }
    public double PythonWorkingSetMb { get; set; }
    public long RecordsRead { get; set; }
    public long RecordsCommitted { get; set; }
}

/// <summary>Пиковые значения памяти за фазу.</summary>
public sealed class PhaseMemory
{
    public string Phase { get; set; }
    public double Seconds { get; set; }
    public int Samples { get; set; }
    public double DotNetPeakPrivateMb { get; set; }
    public double DotNetPeakWorkingSetMb { get; set; }
    public double ManagedHeapPeakMb { get; set; }
    public double PythonPeakPrivateMb { get; set; }
    public double PythonPeakWorkingSetMb { get; set; }

    /// <summary>Пики ОС по процессам worker этой фазы (PeakPagefileUsage / PeakWorkingSetSize).</summary>
    public double PythonOsPeakPrivateMb { get; set; }
    public double PythonOsPeakWorkingSetMb { get; set; }

    /// <summary>Максимум записей «в пути» (прочитано, но ещё не зафиксировано в SQL).</summary>
    public long MaxInFlightRecords { get; set; }
    public double AverageInFlightRecords { get; set; }
    public List<MemorySample> Series { get; set; } = new();
}

/// <summary>
/// Периодический опрос памяти процесса .NET и процессов Python worker (отдельный фоновый поток).
/// Временной ряд прореживается, чтобы его объём не рос с длительностью прогона.
/// </summary>
public sealed class MemorySampler : IDisposable
{
    private const int MaxSeriesPoints = 1200;
    private readonly Func<IEnumerable<int>> _workerPids;
    private readonly Func<(long Read, long Committed)> _progress;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private PhaseMemory _phase;
    private Stopwatch _phaseClock;
    private int _stride = 1;
    private long _tick;
    private double _inFlightSum;
    private long _inFlightSamples;

    public MemorySampler(TimeSpan interval, Func<IEnumerable<int>> workerPids, Func<(long Read, long Committed)> progress = null)
    {
        Interval = interval;
        _workerPids = workerPids ?? (() => Enumerable.Empty<int>());
        _progress = progress;
        _thread = new Thread(Loop) { IsBackground = true, Name = "fakt-memory-sampler" };
        _thread.Start();
    }

    public TimeSpan Interval { get; }

    public void BeginPhase(string name)
    {
        lock (_gate)
        {
            _phase = new PhaseMemory { Phase = name };
            _phaseClock = Stopwatch.StartNew();
            _stride = 1;
            _tick = 0;
            _inFlightSum = 0;
            _inFlightSamples = 0;
        }

        SampleOnce();
    }

    /// <summary>Завершить фазу; finishedWorkers — последние измерения завершившихся процессов worker (пики ОС).</summary>
    public PhaseMemory EndPhase(IEnumerable<ProcessMemory> finishedWorkers = null)
    {
        SampleOnce();
        lock (_gate)
        {
            var phase = _phase ?? new PhaseMemory { Phase = "(нет фазы)" };
            phase.Seconds = _phaseClock?.Elapsed.TotalSeconds ?? 0;
            foreach (var worker in finishedWorkers ?? Enumerable.Empty<ProcessMemory>())
            {
                phase.PythonOsPeakPrivateMb = Math.Max(phase.PythonOsPeakPrivateMb, Mb(worker.PeakPrivateBytes));
                phase.PythonOsPeakWorkingSetMb = Math.Max(phase.PythonOsPeakWorkingSetMb, Mb(worker.PeakWorkingSet));
                phase.PythonPeakPrivateMb = Math.Max(phase.PythonPeakPrivateMb, Mb(worker.PrivateBytes));
            }

            phase.AverageInFlightRecords = _inFlightSamples == 0 ? 0 : _inFlightSum / _inFlightSamples;
            _phase = null;
            return phase;
        }
    }

    private void Loop()
    {
        while (!_stop.Wait(Interval))
        {
            try
            {
                SampleOnce();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Сбой опроса не должен останавливать прогон.
            }
        }
    }

    private void SampleOnce()
    {
        var self = MemoryProbe.ReadCurrent();
        var managed = GC.GetTotalMemory(false);
        var workers = _workerPids().Select(MemoryProbe.Read).Where(m => m != null).ToList();
        var progress = _progress?.Invoke() ?? (0, 0);
        lock (_gate)
        {
            var phase = _phase;
            if (phase == null)
            {
                return;
            }

            phase.Samples++;
            if (self != null)
            {
                phase.DotNetPeakPrivateMb = Math.Max(phase.DotNetPeakPrivateMb, Mb(self.PrivateBytes));
                phase.DotNetPeakWorkingSetMb = Math.Max(phase.DotNetPeakWorkingSetMb, Mb(self.WorkingSet));
            }

            phase.ManagedHeapPeakMb = Math.Max(phase.ManagedHeapPeakMb, Mb(managed));
            var pyPrivate = workers.Sum(w => w.PrivateBytes);
            var pyWorkingSet = workers.Sum(w => w.WorkingSet);
            phase.PythonPeakPrivateMb = Math.Max(phase.PythonPeakPrivateMb, Mb(pyPrivate));
            phase.PythonPeakWorkingSetMb = Math.Max(phase.PythonPeakWorkingSetMb, Mb(pyWorkingSet));
            foreach (var worker in workers)
            {
                phase.PythonOsPeakPrivateMb = Math.Max(phase.PythonOsPeakPrivateMb, Mb(worker.PeakPrivateBytes));
                phase.PythonOsPeakWorkingSetMb = Math.Max(phase.PythonOsPeakWorkingSetMb, Mb(worker.PeakWorkingSet));
            }

            var inFlight = Math.Max(0, progress.Read - progress.Committed);
            phase.MaxInFlightRecords = Math.Max(phase.MaxInFlightRecords, inFlight);
            if (progress.Read > 0)
            {
                _inFlightSum += inFlight;
                _inFlightSamples++;
            }

            if (_tick++ % _stride == 0)
            {
                phase.Series.Add(new MemorySample
                {
                    Seconds = Math.Round(_phaseClock.Elapsed.TotalSeconds, 2),
                    DotNetPrivateMb = Math.Round(Mb(self?.PrivateBytes ?? 0), 1),
                    DotNetWorkingSetMb = Math.Round(Mb(self?.WorkingSet ?? 0), 1),
                    ManagedHeapMb = Math.Round(Mb(managed), 1),
                    PythonPrivateMb = Math.Round(Mb(pyPrivate), 1),
                    PythonWorkingSetMb = Math.Round(Mb(pyWorkingSet), 1),
                    RecordsRead = progress.Read,
                    RecordsCommitted = progress.Committed,
                });
                if (phase.Series.Count > MaxSeriesPoints)
                {
                    phase.Series = phase.Series.Where((_, i) => i % 2 == 0).ToList();
                    _stride *= 2;
                }
            }
        }
    }

    public static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }
}
