using System;
using Fakt.Application.Llm;
using Fakt.UnitTests.TestSupport;
using Xunit;

namespace Fakt.UnitTests.Application;

public sealed class RetryPolicyTests
{
    private static readonly RetryPolicy Policy = new() { BaseDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(90) };

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(6, 64)]
    [InlineData(7, 90)]
    [InlineData(20, 90)]
    public void Backoff_IsExponentialWithFullJitterAndCap(int attempt, int capSeconds)
    {
        var cap = TimeSpan.FromSeconds(capSeconds);

        Assert.Equal(TimeSpan.Zero, Policy.DelayFor(attempt, null, new StubRandom(0)));
        Assert.Equal(TimeSpan.FromMilliseconds(cap.TotalMilliseconds / 2), Policy.DelayFor(attempt, null, new StubRandom(0.5)));
        var nearMax = Policy.DelayFor(attempt, null, new StubRandom(0.999));
        Assert.True(nearMax <= cap && nearMax >= TimeSpan.FromMilliseconds(cap.TotalMilliseconds * 0.99), nearMax.ToString());
    }

    [Fact]
    public void Backoff_WithRealRandom_StaysWithinBounds()
    {
        var random = new Random(12345);
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var cap = TimeSpan.FromMilliseconds(Math.Min(90_000, 2000 * Math.Pow(2, attempt - 1)));
            for (var i = 0; i < 200; i++)
            {
                var delay = Policy.DelayFor(attempt, null, random);
                Assert.True(delay >= TimeSpan.Zero && delay <= cap, $"attempt {attempt}: {delay}");
            }
        }
    }

    [Fact]
    public void RetryAfter_HasPriorityAndGetsSmallJitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), Policy.DelayFor(5, TimeSpan.FromSeconds(7), new StubRandom(0)));
        Assert.Equal(TimeSpan.FromSeconds(7.25), Policy.DelayFor(5, TimeSpan.FromSeconds(7), new StubRandom(0.5)));
        Assert.True(Policy.DelayFor(1, TimeSpan.FromSeconds(7), new StubRandom(0.999)) < TimeSpan.FromSeconds(7.5));
    }

    [Fact]
    public void RetryAfterAboveMaximum_IsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), Policy.DelayFor(1, TimeSpan.FromMinutes(10), new StubRandom(0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveRetryAfter_FallsBackToBackoff(int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(1), Policy.DelayFor(1, TimeSpan.FromSeconds(seconds), new StubRandom(0.5)));
    }
}
