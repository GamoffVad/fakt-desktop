using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Common;
using Fakt.Application.Llm;
using Fakt.Core.Llm;
using Fakt.UnitTests.TestSupport;
using Xunit;

namespace Fakt.UnitTests.Application;

public sealed class ResilientLlmClientTests
{
    private const string ApiKey = "sk-test-UNIT-resilient-0123456789";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly ScriptedLlmAdapter _adapter = new();
    private readonly CapturingLogger _logger = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc));

    private ResilientLlmClient Client(IDelay delay, RetryPolicy policy = null, BudgetTracker budget = null, int maxConcurrent = 2,
        int requestsPerMinute = 0, int tokensPerMinute = 0)
    {
        var profile = new LlmProfile
        {
            Name = "test",
            ProviderId = "scripted",
            ModelId = "test-model",
            MaxConcurrentRequests = maxConcurrent,
            RequestsPerMinute = requestsPerMinute,
            TokensPerMinute = tokensPerMinute,
            MaxOutputTokens = 1000,
        };
        return new ResilientLlmClient(_adapter, new LlmRuntimeConfig(profile, ApiKey, null),
            policy ?? new RetryPolicy { MaxAttempts = 6 }, budget, _logger, delay, _clock);
    }

    private static LlmJsonRequest Request() => new() { SystemPrompt = "s", UserContent = "u", MaxOutputTokens = 1000, Purpose = "test" };

    private static LlmException Error(LlmErrorKind kind, TimeSpan? retryAfter = null) =>
        new(kind, LlmErrorText.Describe(kind), 500, "req-1", retryAfter);

    [Fact]
    public async Task TransientErrors_AreRetriedWithBoundedExponentialBackoff()
    {
        var delay = new FakeDelay(_clock);
        _adapter.ThenThrow(Error(LlmErrorKind.ServerError)).ThenThrow(Error(LlmErrorKind.Timeout)).ThenReturn(ScriptedLlmAdapter.Ok());
        var client = Client(delay);

        var response = await client.CompleteAsync(Request(), 100, CancellationToken.None);

        Assert.Equal("{\"rows\":[]}", response.Text);
        Assert.Equal(3, _adapter.Calls);
        Assert.Equal(2, delay.Delays.Count);
        Assert.InRange(delay.Delays[0], TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.InRange(delay.Delays[1], TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Assert.Equal(3, client.Budget.Requests);
        Assert.Equal(2, _logger.Entries.Count(e => e.Event == "llm.retry"));
        Assert.Single(_logger.Entries, e => e.Event == "llm.request");
    }

    [Fact]
    public async Task RetryAfter_IsHonoured()
    {
        var delay = new FakeDelay(_clock);
        _adapter.ThenThrow(Error(LlmErrorKind.RateLimited, TimeSpan.FromSeconds(7))).ThenReturn(ScriptedLlmAdapter.Ok());

        await Client(delay).CompleteAsync(Request(), 100, CancellationToken.None);

        var wait = Assert.Single(delay.Delays);
        Assert.InRange(wait, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7.5));
    }

    [Fact]
    public async Task RetryAfterAboveMaximum_IsCappedByPolicy()
    {
        var delay = new FakeDelay(_clock);
        _adapter.ThenThrow(Error(LlmErrorKind.RateLimited, TimeSpan.FromMinutes(10))).ThenReturn(ScriptedLlmAdapter.Ok());
        var policy = new RetryPolicy { MaxAttempts = 3, MaxDelay = TimeSpan.FromSeconds(30) };

        await Client(delay, policy).CompleteAsync(Request(), 100, CancellationToken.None);

        Assert.InRange(Assert.Single(delay.Delays), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30.5));
    }

    [Theory]
    [InlineData(LlmErrorKind.Authentication)]
    [InlineData(LlmErrorKind.PermissionDenied)]
    [InlineData(LlmErrorKind.ModelNotFound)]
    [InlineData(LlmErrorKind.QuotaExceeded)]
    [InlineData(LlmErrorKind.BadRequest)]
    [InlineData(LlmErrorKind.ContextLengthExceeded)]
    [InlineData(LlmErrorKind.UnsupportedParameter)]
    [InlineData(LlmErrorKind.UnsupportedResponseFormat)]
    [InlineData(LlmErrorKind.ContentFiltered)]
    [InlineData(LlmErrorKind.InvalidResponse)]
    [InlineData(LlmErrorKind.Configuration)]
    public async Task NonTransientErrors_AreNotRetried(LlmErrorKind kind)
    {
        var delay = new FakeDelay(_clock);
        _adapter.Fallback = (_, _) => Task.FromException<LlmResponse>(Error(kind));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Client(delay).CompleteAsync(Request(), 100, CancellationToken.None));

        Assert.Equal(kind, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Equal(1, _adapter.Calls);
        Assert.Empty(delay.Delays);
        Assert.Contains(_logger.Entries, e => e.Event == "llm.error" && e.ErrorCode == kind.ToString());
    }

    [Theory]
    [InlineData(LlmErrorKind.RateLimited)]
    [InlineData(LlmErrorKind.ServerError)]
    [InlineData(LlmErrorKind.Overloaded)]
    [InlineData(LlmErrorKind.Network)]
    [InlineData(LlmErrorKind.Timeout)]
    public async Task TransientErrors_GiveUpAfterMaxAttempts(LlmErrorKind kind)
    {
        var delay = new FakeDelay(_clock);
        _adapter.Fallback = (_, _) => Task.FromException<LlmResponse>(Error(kind));

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(delay, new RetryPolicy { MaxAttempts = 3 }).CompleteAsync(Request(), 100, CancellationToken.None));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(3, _adapter.Calls);
        Assert.Equal(2, delay.Delays.Count);
    }

    [Fact]
    public async Task RequestBudget_StopsBeforeCallingProvider()
    {
        _adapter.Fallback = (_, _) => Task.FromResult(ScriptedLlmAdapter.Ok());
        var client = Client(new FakeDelay(_clock), budget: new BudgetTracker(maxRequests: 2, maxTokens: 0));

        await client.CompleteAsync(Request(), 100, CancellationToken.None);
        await client.CompleteAsync(Request(), 100, CancellationToken.None);
        await Assert.ThrowsAsync<BudgetExceededException>(() => client.CompleteAsync(Request(), 100, CancellationToken.None));

        Assert.Equal(2, _adapter.Calls);
    }

    [Fact]
    public async Task RequestBudget_CountsRetries()
    {
        _adapter.Fallback = (_, _) => Task.FromException<LlmResponse>(Error(LlmErrorKind.ServerError));
        var client = Client(new FakeDelay(_clock), budget: new BudgetTracker(maxRequests: 2, maxTokens: 0));

        await Assert.ThrowsAsync<BudgetExceededException>(() => client.CompleteAsync(Request(), 100, CancellationToken.None));

        Assert.Equal(2, _adapter.Calls);
    }

    [Fact]
    public async Task TokenBudget_UsesReportedUsage()
    {
        _adapter.Fallback = (_, _) => Task.FromResult(ScriptedLlmAdapter.Ok(input: 80, output: 30));
        var client = Client(new FakeDelay(_clock), budget: new BudgetTracker(maxRequests: 0, maxTokens: 100));

        await client.CompleteAsync(Request(), 100, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() => client.CompleteAsync(Request(), 100, CancellationToken.None));

        Assert.Equal(80, client.Budget.InputTokens);
        Assert.Equal(30, client.Budget.OutputTokens);
        Assert.Contains("100", ex.Message);
        Assert.Equal(1, _adapter.Calls);
    }

    [Fact]
    public async Task CancelledToken_StopsBeforeCallingProvider()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(new FakeDelay(_clock)).CompleteAsync(Request(), 100, cts.Token));

        Assert.Equal(0, _adapter.Calls);
    }

    [Fact]
    public async Task CancellationDuringBackoff_StopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var delay = new ManualDelay();
        _adapter.Fallback = (_, _) => Task.FromException<LlmResponse>(Error(LlmErrorKind.ServerError));
        var task = Client(delay).CompleteAsync(Request(), 100, cts.Token);

        Assert.True(await delay.WaitForRequestAsync(WaitLimit));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, _adapter.Calls);
    }

    [Fact]
    public async Task ConcurrentRequests_AreLimitedByProfile()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _adapter.Fallback = async (_, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return ScriptedLlmAdapter.Ok();
        };
        var client = Client(new FakeDelay(_clock), maxConcurrent: 2);

        // Вызовы выполняются синхронно до первого ожидания: два входят в адаптер, остальные ждут слота.
        var tasks = Enumerable.Range(0, 6).Select(_ => client.CompleteAsync(Request(), 100, CancellationToken.None)).ToList();

        Assert.Equal(2, _adapter.Inside);
        gate.SetResult(true);
        await Task.WhenAll(tasks);
        Assert.Equal(6, _adapter.Calls);
        Assert.Equal(2, _adapter.MaxConcurrent);
    }

    [Fact]
    public async Task ConcurrencySlot_IsReleasedDuringBackoff()
    {
        var delay = new ManualDelay();
        _adapter
            .ThenThrow(Error(LlmErrorKind.Overloaded))
            .ThenReturn(ScriptedLlmAdapter.Ok("{\"second\":true}"))
            .ThenReturn(ScriptedLlmAdapter.Ok("{\"first\":true}"));
        var client = Client(delay, maxConcurrent: 1);

        var first = client.CompleteAsync(Request(), 100, CancellationToken.None);
        Assert.True(await delay.WaitForRequestAsync(WaitLimit));

        // Пока первый запрос ждёт повтора, единственный слот свободен для другого запроса.
        var second = client.CompleteAsync(Request(), 100, CancellationToken.None);
        Assert.Same(second, await Task.WhenAny(second, Task.Delay(WaitLimit)));
        Assert.Equal("{\"second\":true}", (await second).Text);

        delay.ReleaseAll();
        Assert.Equal("{\"first\":true}", (await first).Text);
        Assert.Equal(1, _adapter.MaxConcurrent);
    }

    [Fact]
    public async Task RateLimit_IsAppliedBeforeEachAttempt()
    {
        var delay = new FakeDelay(_clock);
        _adapter.Fallback = (_, _) => Task.FromResult(ScriptedLlmAdapter.Ok());
        var client = Client(delay, requestsPerMinute: 1);

        await client.CompleteAsync(Request(), 100, CancellationToken.None);
        await client.CompleteAsync(Request(), 100, CancellationToken.None);

        Assert.Equal(new[] { TimeSpan.FromMinutes(1) }, delay.Delays);
        Assert.Equal(2, _adapter.Calls);
    }

    [Fact]
    public async Task Logs_DoNotContainApiKey()
    {
        _adapter.ThenThrow(Error(LlmErrorKind.ServerError)).ThenReturn(ScriptedLlmAdapter.Ok());

        await Client(new FakeDelay(_clock)).CompleteAsync(Request(), 100, CancellationToken.None);

        Assert.All(_logger.Entries, e =>
        {
            Assert.DoesNotContain(ApiKey, e.Message ?? string.Empty);
            Assert.DoesNotContain(ApiKey, string.Join(" ", (e.Data ?? new Dictionary<string, object>()).Values));
        });
    }
}
