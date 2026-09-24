using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Llm;
using Fakt.UnitTests.TestSupport;
using Xunit;

namespace Fakt.UnitTests.Application;

public sealed class RateLimiterTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc));
    private readonly FakeDelay _delay;

    public RateLimiterTests()
    {
        _delay = new FakeDelay(_clock);
    }

    private RateLimiter Limiter(int rpm, int tpm) => new(rpm, tpm, _clock, _delay);

    [Fact]
    public async Task Unlimited_NeverWaits()
    {
        var limiter = Limiter(0, 0);

        for (var i = 0; i < 1000; i++)
        {
            await limiter.AcquireAsync(100_000, CancellationToken.None);
        }

        Assert.True(limiter.IsUnlimited);
        Assert.Empty(_delay.Delays);
    }

    [Fact]
    public async Task RequestsPerMinute_WaitsUntilOldestRequestLeavesWindow()
    {
        var limiter = Limiter(2, 0);

        await limiter.AcquireAsync(1, CancellationToken.None);
        await limiter.AcquireAsync(1, CancellationToken.None);
        Assert.Empty(_delay.Delays);

        await limiter.AcquireAsync(1, CancellationToken.None);

        Assert.Equal(new[] { TimeSpan.FromMinutes(1) }, _delay.Delays);
    }

    [Fact]
    public async Task RequestsPerMinute_IsASlidingWindow()
    {
        var start = _clock.UtcNow;
        var limiter = Limiter(2, 0);

        await limiter.AcquireAsync(1, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await limiter.AcquireAsync(1, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await limiter.AcquireAsync(1, CancellationToken.None);

        // Третий запрос ждёт, пока первый (t=0) не выйдет из окна 60 с: 60 − 20 = 40 с.
        Assert.Equal(new[] { TimeSpan.FromSeconds(40) }, _delay.Delays);
        Assert.Equal(start.AddSeconds(60), _clock.UtcNow);
    }

    [Fact]
    public async Task TokensPerMinute_WaitsWhenBudgetOfWindowIsUsed()
    {
        var limiter = Limiter(0, 1000);

        await limiter.AcquireAsync(600, CancellationToken.None);
        await limiter.AcquireAsync(600, CancellationToken.None);

        Assert.Equal(new[] { TimeSpan.FromMinutes(1) }, _delay.Delays);

        // В новом окне 600 уже учтены; ещё 400 помещаются без ожидания.
        await limiter.AcquireAsync(400, CancellationToken.None);
        Assert.Single(_delay.Delays);
    }

    [Fact]
    public async Task RequestLargerThanTokenLimit_PassesAloneInWindow()
    {
        var limiter = Limiter(0, 1000);

        await limiter.AcquireAsync(5000, CancellationToken.None);
        Assert.Empty(_delay.Delays);

        await limiter.AcquireAsync(1, CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromMinutes(1) }, _delay.Delays);
    }

    [Fact]
    public async Task VeryShortWait_IsRoundedUpTo50Milliseconds()
    {
        var limiter = Limiter(1, 0);
        await limiter.AcquireAsync(1, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(59_990));

        await limiter.AcquireAsync(1, CancellationToken.None);

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50) }, _delay.Delays);
    }

    [Fact]
    public async Task Cancellation_StopsWaiting()
    {
        var limiter = Limiter(1, 0);
        await limiter.AcquireAsync(1, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(1, cts.Token));
    }

    [Fact]
    public async Task SequentialCallers_NeverExceedRequestsPerMinuteInAnyWindow()
    {
        var limiter = Limiter(3, 0);
        var granted = new System.Collections.Generic.List<DateTime>();

        for (var i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync(1, CancellationToken.None);
            granted.Add(_clock.UtcNow);
            _clock.Advance(TimeSpan.FromSeconds(7));
        }

        foreach (var t in granted)
        {
            Assert.True(granted.Count(x => x >= t && x < t.AddMinutes(1)) <= 3);
        }
    }
}

public sealed class BudgetTrackerTests
{
    [Fact]
    public void RequestLimit_IsEnforced()
    {
        var budget = new BudgetTracker(maxRequests: 2, maxTokens: 0);

        budget.EnsureAvailable();
        budget.RecordRequest();
        budget.EnsureAvailable();
        budget.RecordRequest();

        var ex = Assert.Throws<BudgetExceededException>(() => budget.EnsureAvailable());
        Assert.Contains("(2)", ex.Message);
    }

    [Fact]
    public void TokenLimit_CountsInputAndOutput()
    {
        var budget = new BudgetTracker(maxRequests: 0, maxTokens: 100);

        budget.RecordUsage(60, null);
        budget.EnsureAvailable();
        budget.RecordUsage(null, 40);

        Assert.Equal(60, budget.InputTokens);
        Assert.Equal(40, budget.OutputTokens);
        Assert.Throws<BudgetExceededException>(() => budget.EnsureAvailable());
    }

    [Fact]
    public void ZeroLimits_MeanUnlimited()
    {
        var budget = new BudgetTracker(0, 0);
        for (var i = 0; i < 10_000; i++)
        {
            budget.RecordRequest();
        }

        budget.RecordUsage(int.MaxValue, int.MaxValue);
        budget.EnsureAvailable();
    }

    [Fact]
    public void AlreadyUsedAmounts_CountTowardsLimits()
    {
        Assert.Throws<BudgetExceededException>(() => new BudgetTracker(10, 0, alreadyUsedRequests: 10).EnsureAvailable());
        Assert.Throws<BudgetExceededException>(() => new BudgetTracker(0, 500, alreadyUsedTokens: 500).EnsureAvailable());
        new BudgetTracker(10, 500, alreadyUsedRequests: 9, alreadyUsedTokens: 499).EnsureAvailable();
    }

    [Fact]
    public async Task Counters_AreThreadSafe()
    {
        var budget = new BudgetTracker(0, 0);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                budget.RecordRequest();
                budget.RecordUsage(2, 1);
            }
        })));

        Assert.Equal(8000, budget.Requests);
        Assert.Equal(16000, budget.InputTokens);
        Assert.Equal(8000, budget.OutputTokens);
    }
}
