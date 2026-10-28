using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

public class BackoffPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private readonly BackoffPolicy _policy = new();

    private static UsageSnapshot Snap(ProviderStatus status, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus("claude", status, Now, null, retryAfter);

    [Fact]
    public void Ok_ReturnsBaseInterval()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.Ok), FiveMinutes, 0));
    }

    [Theory]
    [InlineData(ProviderStatus.NotInstalled)]
    [InlineData(ProviderStatus.SignedOut)]
    [InlineData(ProviderStatus.WaitingForCliRefresh)]
    [InlineData(ProviderStatus.Unsupported)]
    public void OtherStatuses_ReturnBaseInterval(ProviderStatus status)
    {
        Assert.Equal(TimeSpan.FromMinutes(10), _policy.NextDelay(Snap(status), TimeSpan.FromMinutes(10), 3));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(50, 60)]
    public void RateLimited_WithoutRetryAfter_StartsAtFiveMinutesAndDoublesToSixtyMinuteCap(int failures, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            _policy.NextDelay(Snap(ProviderStatus.RateLimited), TimeSpan.FromMinutes(2), failures));
    }

    [Fact]
    public void RateLimited_ZeroRetryAfter_UsesFiveMinuteFloor()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.RateLimited, TimeSpan.Zero), FiveMinutes, 1));
    }

    [Fact]
    public void RateLimited_RetryAfterBelowFloor_UsesFiveMinutes()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.RateLimited, TimeSpan.FromSeconds(30)), FiveMinutes, 1));
    }

    [Fact]
    public void RateLimited_LongRetryAfter_IsHonouredThenDoubledAndCapped()
    {
        var snapshot = Snap(ProviderStatus.RateLimited, TimeSpan.FromMinutes(20));
        Assert.Equal(TimeSpan.FromMinutes(20), _policy.NextDelay(snapshot, FiveMinutes, 1));
        Assert.Equal(TimeSpan.FromMinutes(40), _policy.NextDelay(snapshot, FiveMinutes, 2));
        Assert.Equal(TimeSpan.FromMinutes(60), _policy.NextDelay(snapshot, FiveMinutes, 3));
    }

    [Fact]
    public void RateLimited_ZeroFailureCount_IsTreatedAsFirstFailure()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.RateLimited), FiveMinutes, 0));
    }

    [Theory]
    [InlineData(5, 1, 5)]
    [InlineData(5, 2, 10)]
    [InlineData(5, 3, 20)]
    [InlineData(5, 4, 30)]
    [InlineData(1, 1, 2)]
    [InlineData(1, 2, 4)]
    [InlineData(15, 2, 30)]
    public void Error_StartsAtMaxOfBaseAndTwoMinutes_DoublesToThirtyMinuteCap(int baseMinutes, int failures, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            _policy.NextDelay(Snap(ProviderStatus.Error), TimeSpan.FromMinutes(baseMinutes), failures));
    }

    [Theory]
    [InlineData(0.0, 270)]
    [InlineData(0.25, 285)]
    [InlineData(0.5, 300)]
    [InlineData(1.0, 330)]
    [InlineData(-3.0, 270)]
    [InlineData(7.0, 330)]
    public void Jitter_MapsUnitIntervalToPlusMinusTenPercent(double unit, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffPolicy.Jitter(FiveMinutes, unit));
    }
}
