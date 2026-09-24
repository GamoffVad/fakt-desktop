using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Common;

namespace Fakt.Application.Llm;

/// <summary>
/// Ограничение запросов и токенов в минуту на стороне клиента (скользящее окно 60 с).
/// Лимиты провайдера по-прежнему сообщаются кодом 429 — они обрабатываются повтором с Retry-After.
/// </summary>
public sealed class RateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly int _requestsPerMinute;
    private readonly int _tokensPerMinute;
    private readonly IClock _clock;
    private readonly IDelay _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<(DateTime At, int Tokens)> _history = new();

    public RateLimiter(int requestsPerMinute, int tokensPerMinute, IClock clock = null, IDelay delay = null)
    {
        _requestsPerMinute = Math.Max(0, requestsPerMinute);
        _tokensPerMinute = Math.Max(0, tokensPerMinute);
        _clock = clock ?? SystemClock.Instance;
        _delay = delay ?? RealDelay.Instance;
    }

    public bool IsUnlimited => _requestsPerMinute == 0 && _tokensPerMinute == 0;

    public async Task AcquireAsync(int tokens, CancellationToken cancellationToken)
    {
        if (IsUnlimited)
        {
            return;
        }

        // Запрос крупнее всего минутного лимита токенов пропускается один в окне, иначе он ждал бы вечно.
        var needed = _tokensPerMinute > 0 ? Math.Min(tokens, _tokensPerMinute) : tokens;
        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock.UtcNow;
                while (_history.Count > 0 && now - _history.Peek().At >= Window)
                {
                    _history.Dequeue();
                }

                var requestsOk = _requestsPerMinute == 0 || _history.Count < _requestsPerMinute;
                var tokensUsed = _history.Sum(h => h.Tokens);
                var tokensOk = _tokensPerMinute == 0 || tokensUsed + needed <= _tokensPerMinute;
                if (requestsOk && tokensOk)
                {
                    _history.Enqueue((now, needed));
                    return;
                }

                wait = _history.Count > 0 ? Window - (now - _history.Peek().At) : TimeSpan.FromMilliseconds(100);
            }
            finally
            {
                _gate.Release();
            }

            await _delay.DelayAsync(wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Предел бюджета задания: при достижении обработка ставится на паузу, а не продолжается молча.</summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException(string message) : base(message)
    {
    }
}

public sealed class BudgetTracker
{
    private readonly long _maxRequests;
    private readonly long _maxTokens;
    private long _requests;
    private long _inputTokens;
    private long _outputTokens;

    public BudgetTracker(long maxRequests, long maxTokens, long alreadyUsedRequests = 0, long alreadyUsedTokens = 0)
    {
        _maxRequests = Math.Max(0, maxRequests);
        _maxTokens = Math.Max(0, maxTokens);
        _requests = alreadyUsedRequests;
        _inputTokens = alreadyUsedTokens;
    }

    public long Requests => Interlocked.Read(ref _requests);

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public void EnsureAvailable()
    {
        if (_maxRequests > 0 && Requests >= _maxRequests)
        {
            throw new BudgetExceededException($"Достигнут предел запросов задания ({_maxRequests}). Обработка поставлена на паузу; увеличьте предел в «Администрирование → Обработка» или продолжите позже.");
        }

        if (_maxTokens > 0 && InputTokens + OutputTokens >= _maxTokens)
        {
            throw new BudgetExceededException($"Достигнут предел токенов задания ({_maxTokens}). Обработка поставлена на паузу.");
        }
    }

    public void RecordRequest() => Interlocked.Increment(ref _requests);

    public void RecordUsage(int? input, int? output)
    {
        if (input.HasValue)
        {
            Interlocked.Add(ref _inputTokens, input.Value);
        }

        if (output.HasValue)
        {
            Interlocked.Add(ref _outputTokens, output.Value);
        }
    }
}
