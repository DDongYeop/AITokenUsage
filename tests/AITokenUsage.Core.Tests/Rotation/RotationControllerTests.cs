using AITokenUsage.Core.Models;
using AITokenUsage.Core.Rotation;
using AITokenUsage.Core.SystemUsage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Rotation;

public class RotationControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly RotationController _rotation;
    private readonly List<(string? Id, int Direction)> _events = [];

    public RotationControllerTests()
    {
        _rotation = new RotationController(_time);
        _rotation.CurrentChanged += (_, e) => _events.Add((e.CurrentId, e.Direction));
        _rotation.Configure(enabled: true, Interval, skipUnavailable: true);
    }

    private static ProviderView View(string id, ProviderStatus status = ProviderStatus.Ok, bool hasLastGood = false)
    {
        var current = UsageSnapshot.WithStatus(id, status, T0);
        var lastGood = hasLastGood ? UsageSnapshot.WithStatus(id, ProviderStatus.Ok, T0.AddHours(-1)) : null;
        return new ProviderView(id, id, current, lastGood);
    }

    private static ProviderView[] Views(params string[] ids) => ids.Select(id => View(id)).ToArray();

    private void Tick(TimeSpan advance)
    {
        _time.Advance(advance);
        _rotation.OnTick();
    }

    [Fact]
    public void BeforeAnyProviders_CurrentIsNull()
    {
        Assert.Null(_rotation.CurrentId);
        Assert.Equal(-1, _rotation.CurrentIndex);
        Assert.Empty(_rotation.EligibleIds);
        Assert.Empty(_events);
    }

    [Fact]
    public void FirstUpdate_SelectsFirstEligible_WithDirectionZero()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));

        Assert.Equal("claude", _rotation.CurrentId);
        Assert.Equal(0, _rotation.CurrentIndex);
        Assert.Equal(new[] { "claude", "codex", "gemini" }, _rotation.EligibleIds);
        Assert.Equal(("claude", 0), Assert.Single(_events));
    }

    [Fact]
    public void OnTick_AdvancesOnlyWhenIntervalElapsed_AndWrapsAround()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _events.Clear();

        Tick(TimeSpan.FromSeconds(4.75));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(0.25));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(Interval);
        Assert.Equal("gemini", _rotation.CurrentId);
        Tick(Interval);
        Assert.Equal("claude", _rotation.CurrentId);

        Assert.Equal(new (string?, int)[] { ("codex", 1), ("gemini", 1), ("claude", 1) }, _events);
    }

    [Fact]
    public void HoverPause_StopsRotation_AndReleaseRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex"));
        Tick(TimeSpan.FromSeconds(3));

        _rotation.SetHoverPaused(true);
        Tick(TimeSpan.FromSeconds(30));
        Assert.Equal("claude", _rotation.CurrentId);

        _rotation.SetHoverPaused(false);
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void UserPause_StopsRotation_ManualStillWorks_AndResumeRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.SetUserPaused(true);
        Assert.True(_rotation.IsUserPaused);

        Tick(TimeSpan.FromSeconds(60));
        Assert.Equal("claude", _rotation.CurrentId);

        _rotation.Next();
        Assert.Equal("codex", _rotation.CurrentId);

        Tick(TimeSpan.FromSeconds(2));
        _rotation.SetUserPaused(false);
        Assert.False(_rotation.IsUserPaused);
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("gemini", _rotation.CurrentId);
    }

    [Fact]
    public void NextAndPrevious_WrapAround_WithDirections()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _events.Clear();

        _rotation.Previous();
        Assert.Equal("gemini", _rotation.CurrentId);
        Assert.Equal(2, _rotation.CurrentIndex);
        _rotation.Next();
        Assert.Equal("claude", _rotation.CurrentId);
        _rotation.Next();
        Assert.Equal("codex", _rotation.CurrentId);

        Assert.Equal(new (string?, int)[] { ("gemini", -1), ("claude", 1), ("codex", 1) }, _events);
    }

    [Fact]
    public void ManualNext_RestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        Tick(TimeSpan.FromSeconds(4));

        _rotation.Next();
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("gemini", _rotation.CurrentId);
    }

    [Fact]
    public void SkipUnavailable_ExcludesDisconnectedProvidersWithoutCachedData()
    {
        _rotation.UpdateProviders(
        [
            View("a", ProviderStatus.Ok),
            View("b", ProviderStatus.SignedOut),
            View("c", ProviderStatus.NotInstalled, hasLastGood: true),
            View("d", ProviderStatus.Unsupported),
            View("e", ProviderStatus.Error),
            View("f", ProviderStatus.WaitingForCliRefresh),
            View("g", ProviderStatus.RateLimited),
            View("h", ProviderStatus.NotInstalled),
        ]);

        Assert.Equal(new[] { "a", "c", "e", "f", "g" }, _rotation.EligibleIds);
    }

    [Fact]
    public void SkipUnavailableOff_IncludesEveryEnabledProvider()
    {
        _rotation.Configure(enabled: true, Interval, skipUnavailable: false);

        _rotation.UpdateProviders([View("a"), View("b", ProviderStatus.SignedOut), View("c", ProviderStatus.NotInstalled)]);

        Assert.Equal(new[] { "a", "b", "c" }, _rotation.EligibleIds);
    }

    [Fact]
    public void Configure_ReevaluatesEligibilityWithLastProviderList()
    {
        _rotation.UpdateProviders([View("a"), View("b", ProviderStatus.SignedOut)]);
        Assert.Equal(new[] { "a" }, _rotation.EligibleIds);

        _rotation.Configure(enabled: true, Interval, skipUnavailable: false);

        Assert.Equal(new[] { "a", "b" }, _rotation.EligibleIds);
        Assert.Equal("a", _rotation.CurrentId);
    }

    [Fact]
    public void NothingEligible_FallsBackToFirstEnabledProvider()
    {
        _rotation.UpdateProviders([View("codex", ProviderStatus.NotInstalled), View("copilot", ProviderStatus.SignedOut)]);

        Assert.Equal(new[] { "codex" }, _rotation.EligibleIds);
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void NoAiEligible_WithPcPanel_KeepsFirstEnabledAiThenPc()
    {
        _rotation.UpdateProviders(
        [
            View("codex", ProviderStatus.NotInstalled),
            View("copilot", ProviderStatus.SignedOut),
            View(SystemPanel.Id),
        ]);

        Assert.Equal(new[] { "codex", SystemPanel.Id }, _rotation.EligibleIds);
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void SomeAiEligible_WithPcPanel_KeepsUsualEligibility()
    {
        _rotation.UpdateProviders(
        [
            View("codex", ProviderStatus.NotInstalled),
            View("claude"),
            View(SystemPanel.Id),
        ]);

        Assert.Equal(new[] { "claude", SystemPanel.Id }, _rotation.EligibleIds);
    }

    [Fact]
    public void OnlyPcPanel_IsEligible()
    {
        _rotation.UpdateProviders([View(SystemPanel.Id)]);

        Assert.Equal(new[] { SystemPanel.Id }, _rotation.EligibleIds);
    }

    [Fact]
    public void EmptyList_ClearsCurrent_WithDirectionZeroEvent()
    {
        _rotation.UpdateProviders(Views("claude"));
        _events.Clear();

        _rotation.UpdateProviders([]);

        Assert.Null(_rotation.CurrentId);
        Assert.Equal(-1, _rotation.CurrentIndex);
        Assert.Empty(_rotation.EligibleIds);
        Assert.Equal(((string?)null, 0), Assert.Single(_events));
    }

    [Fact]
    public void ListChange_KeepsCurrentWhenStillEligible_WithoutEvent()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.Next();
        _events.Clear();

        _rotation.UpdateProviders(Views("gemini", "claude", "codex"));

        Assert.Equal("codex", _rotation.CurrentId);
        Assert.Equal(2, _rotation.CurrentIndex);
        Assert.Empty(_events);
    }

    [Fact]
    public void ListChange_CurrentBecomesIneligible_MovesToNextInPreviousOrder()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.Next();
        _events.Clear();

        _rotation.UpdateProviders([View("claude"), View("codex", ProviderStatus.SignedOut), View("gemini")]);

        Assert.Equal("gemini", _rotation.CurrentId);
        Assert.Equal(("gemini", 0), Assert.Single(_events));
    }

    [Fact]
    public void ListChange_CurrentDisabled_WrapsToFirstFollowingEligible()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.Previous();
        _events.Clear();

        _rotation.UpdateProviders(Views("claude", "codex"));

        Assert.Equal("claude", _rotation.CurrentId);
        Assert.Equal(("claude", 0), Assert.Single(_events));
    }

    [Fact]
    public void ListChange_ReplacementRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        Tick(TimeSpan.FromSeconds(4));

        _rotation.UpdateProviders(Views("codex", "gemini"));
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("gemini", _rotation.CurrentId);
    }

    [Fact]
    public void SingleProvider_NeverRotates_AndManualMovesDoNothing()
    {
        _rotation.UpdateProviders(Views("claude"));
        _events.Clear();

        Tick(TimeSpan.FromSeconds(60));
        _rotation.Next();
        _rotation.Previous();

        Assert.Equal("claude", _rotation.CurrentId);
        Assert.Empty(_events);
    }

    [Fact]
    public void GrowingFromOneToTwoProviders_DoesNotSwitchImmediately()
    {
        _rotation.UpdateProviders(Views("claude"));
        Tick(TimeSpan.FromSeconds(60));

        _rotation.UpdateProviders(Views("claude", "codex"));
        Tick(TimeSpan.FromSeconds(0.25));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(4.75));
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void DisabledRotation_NeverAutoAdvances_ButManualNextWorks()
    {
        _rotation.Configure(enabled: false, Interval, skipUnavailable: true);
        _rotation.UpdateProviders(Views("claude", "codex"));
        _events.Clear();

        Tick(TimeSpan.FromSeconds(120));
        Assert.Equal("claude", _rotation.CurrentId);

        _rotation.Next();
        Assert.Equal("codex", _rotation.CurrentId);
        Assert.Equal(("codex", 1), Assert.Single(_events));
    }

    [Fact]
    public void Configure_ChangesIntervalAndRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex"));
        Tick(TimeSpan.FromSeconds(4));

        _rotation.Configure(enabled: true, TimeSpan.FromSeconds(10), skipUnavailable: true);
        Tick(TimeSpan.FromSeconds(9));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("codex", _rotation.CurrentId);
    }
}
