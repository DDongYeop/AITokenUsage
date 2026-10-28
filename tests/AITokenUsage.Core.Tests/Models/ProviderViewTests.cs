using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Tests.Models;

public class ProviderViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot OkSnapshot(string plan, double session) =>
        new(ProviderIds.Claude, ProviderStatus.Ok, plan,
            [new UsageMeter("session", "5h", "현재 세션 (5시간)", session, Now.AddHours(3))],
            Now);

    [Fact]
    public void Ok_UsesCurrentMetersAndPlan_AndIsNotStale()
    {
        UsageSnapshot current = OkSnapshot("Max 5x", 14);
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, LastGood: OkSnapshot("Pro", 99));

        Assert.False(view.IsStale);
        Assert.Same(current.Meters, view.DisplayMeters);
        Assert.Equal("Max 5x", view.DisplayPlan);
        Assert.True(view.HasAnyData);
    }

    [Theory]
    [InlineData(ProviderStatus.Error)]
    [InlineData(ProviderStatus.RateLimited)]
    [InlineData(ProviderStatus.WaitingForCliRefresh)]
    [InlineData(ProviderStatus.SignedOut)]
    public void NotOk_WithLastGood_ShowsLastGoodMetersAndPlan_AndIsStale(ProviderStatus status)
    {
        UsageSnapshot lastGood = OkSnapshot("Team · Max 5x", 50);
        var current = UsageSnapshot.WithStatus(ProviderIds.Claude, status, Now, "요청 제한");
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, lastGood);

        Assert.True(view.IsStale);
        Assert.Same(lastGood.Meters, view.DisplayMeters);
        Assert.Equal("Team · Max 5x", view.DisplayPlan);
        Assert.True(view.HasAnyData);
    }

    [Fact]
    public void NotOk_WithoutLastGood_HasNoData_AndIsNotStale()
    {
        var current = UsageSnapshot.WithStatus(ProviderIds.Codex, ProviderStatus.NotInstalled, Now, "설치 안 됨");
        var view = new ProviderView(ProviderIds.Codex, "Codex", current, LastGood: null);

        Assert.False(view.IsStale);
        Assert.Empty(view.DisplayMeters);
        Assert.Null(view.DisplayPlan);
        Assert.False(view.HasAnyData);
    }

    [Fact]
    public void Ok_WithNoMeters_DoesNotFallBackToLastGood()
    {
        var current = new UsageSnapshot(ProviderIds.Gemini, ProviderStatus.Ok, "Free", [], Now);
        var view = new ProviderView(ProviderIds.Gemini, "Gemini", current, OkSnapshot("Old", 10));

        Assert.False(view.IsStale);
        Assert.Empty(view.DisplayMeters);
        Assert.False(view.HasAnyData);
        Assert.Equal("Free", view.DisplayPlan);
    }

    [Fact]
    public void WithStatus_BuildsEmptySnapshot()
    {
        var snapshot = UsageSnapshot.WithStatus(ProviderIds.Copilot, ProviderStatus.RateLimited, Now,
            "요청 제한 — 14:20 이후 재시도", TimeSpan.FromMinutes(5));

        Assert.Equal(ProviderIds.Copilot, snapshot.ProviderId);
        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Null(snapshot.PlanLabel);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(Now, snapshot.FetchedAt);
        Assert.Equal("요청 제한 — 14:20 이후 재시도", snapshot.StatusMessage);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.RetryAfter);
        Assert.Null(snapshot.ExtraLine);
    }

    [Fact]
    public void ProviderIds_All_IsInCanonicalOrder()
    {
        Assert.Equal(["claude", "codex", "gemini", "copilot"], ProviderIds.All);
    }
}
