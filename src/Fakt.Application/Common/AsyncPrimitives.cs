using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fakt.Application.Common;

/// <summary>Задержка — абстракция для тестов (повторы с backoff без реального ожидания).</summary>
public interface IDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RealDelay : IDelay
{
    public static readonly RealDelay Instance = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Ограниченная асинхронная очередь: запись ждёт свободного места — так чтение файла замедляется,
/// когда следующие стадии конвейера не успевают. Память не растёт пропорционально размеру файла.
/// </summary>
public sealed class AsyncBoundedQueue<T>
{
    private readonly Queue<T> _items = new();
    private readonly SemaphoreSlim _free;
    private readonly SemaphoreSlim _available = new(0, int.MaxValue);
    private readonly object _gate = new();
    private bool _completed;

    public AsyncBoundedQueue(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _free = new SemaphoreSlim(capacity, capacity);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public async Task EnqueueAsync(T item, CancellationToken cancellationToken)
    {
        await _free.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_completed)
            {
                _free.Release();
                throw new InvalidOperationException("Очередь закрыта для записи.");
            }

            _items.Enqueue(item);
        }

        _available.Release();
    }

    /// <summary>Возвращает (true, элемент) или (false, default), когда очередь завершена и пуста.</summary>
    public async Task<(bool Success, T Item)> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    var item = _items.Dequeue();
                    _free.Release();
                    return (true, item);
                }

                if (_completed)
                {
                    // Пробуждаем остальных читателей: очередь больше не пополнится.
                    _available.Release();
                    return (false, default);
                }
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _available.Release();
    }
}
