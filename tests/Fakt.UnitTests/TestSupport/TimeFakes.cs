using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Common;

namespace Fakt.UnitTests.TestSupport;

/// <summary>Управляемые часы: время меняется только явно (или через <see cref="FakeDelay"/>).</summary>
internal sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private DateTime _now;

    public FakeClock(DateTime startUtc)
    {
        _now = startUtc;
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }
}

/// <summary>Задержка без ожидания: запоминает запрошенные интервалы и сдвигает часы (если заданы).</summary>
internal sealed class FakeDelay : IDelay
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _delays = new();
    private readonly FakeClock _clock;

    public FakeDelay(FakeClock clock = null)
    {
        _clock = clock;
    }

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return _delays.ToList();
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            _delays.Add(delay);
        }

        _clock?.Advance(delay);
        return Task.CompletedTask;
    }
}

/// <summary>Задержка, которую тест завершает вручную: позволяет наблюдать состояние во время паузы между попытками.</summary>
internal sealed class ManualDelay : IDelay
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<bool>> _pending = new();
    private readonly SemaphoreSlim _requested = new(0);

    public List<TimeSpan> Requested { get; } = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            Requested.Add(delay);
            _pending.Add(tcs);
        }

        cancellationToken.Register(() => tcs.TrySetCanceled());
        _requested.Release();
        return tcs.Task;
    }

    /// <summary>Дождаться, пока код под тестом запросит задержку.</summary>
    public Task<bool> WaitForRequestAsync(TimeSpan timeout) => _requested.WaitAsync(timeout);

    public void ReleaseAll()
    {
        List<TaskCompletionSource<bool>> pending;
        lock (_gate)
        {
            pending = _pending.ToList();
            _pending.Clear();
        }

        foreach (var tcs in pending)
        {
            tcs.TrySetResult(true);
        }
    }
}

/// <summary>Предсказуемый источник jitter для проверки границ задержек.</summary>
internal sealed class StubRandom : Random
{
    private readonly double _fraction;

    public StubRandom(double fraction)
    {
        _fraction = fraction;
    }

    public override double NextDouble() => _fraction;

    public override int Next(int minValue, int maxValue) => minValue + (int)Math.Floor((maxValue - minValue) * _fraction);
}
