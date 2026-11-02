using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Tests.Localization;
using AITokenUsage.Core.Util;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

public class AccountTagsTests
{
    private static ProviderView V(string id, string? plan, string name = "Claude")
    {
        var snapshot = new UsageSnapshot(id, ProviderStatus.Ok, plan, [Meter("session", "5h", 14)], Now);
        return new ProviderView(id, name, snapshot, snapshot);
    }

    private static void AssertTags(IReadOnlyList<ProviderView> views, params string?[] expected) =>
        Assert.True(
            expected.SequenceEqual(views.Select(v => v.AccountTag)),
            "tags: " + string.Join(", ", views.Select(v => v.AccountTag ?? "(none)")));

    [Fact]
    public void Apply_SingleAccountPerKind_AddsNoTagAndKeepsTheSameInstances()
    {
        ProviderView[] views = [V("claude", "Pro"), V("codex", "Plus", "Codex"), V("gemini", null, "Gemini")];

        IReadOnlyList<ProviderView> result = AccountTags.Apply(views);

        Assert.Equal(3, result.Count);
        for (int i = 0; i < views.Length; i++)
        {
            Assert.Same(views[i], result[i]);
        }
    }

    [Fact]
    public void Apply_TeamAndPersonal_AreTaggedByPlan()
    {
        ProviderView[] views = [V("claude", "Team · Max 5x"), V("claude#k3x9q2", "Max 5x")];

        AssertTags(AccountTags.Apply(views), ["팀", "개인"]);
    }

    [Fact]
    public void Apply_UnknownPlans_GetPositionNumbers()
    {
        ProviderView[] views = [V("claude", null), V("claude#k3x9q2", null)];

        AssertTags(AccountTags.Apply(views), ["#1", "#2"]);
    }

    [Fact]
    public void Apply_TeamAndUnknown_MixesTagKinds()
    {
        ProviderView[] views = [V("claude", "Team"), V("claude#k3x9q2", null)];

        AssertTags(AccountTags.Apply(views), ["팀", "#2"]);
    }

    [Fact]
    public void Apply_RepeatedTag_SecondOnwardGetsItsPosition()
    {
        ProviderView[] views = [V("claude", "Max 5x"), V("claude#k3x9q2", "Pro"), V("claude#a1b2c3", "Team")];

        AssertTags(AccountTags.Apply(views), ["개인", "개인 2", "팀"]);
    }

    [Fact]
    public void Apply_RepeatedTagNumberIsThePositionWithinTheKind_NotTheOverallIndex()
    {
        ProviderView[] views =
        [
            V("claude", "Team"),
            V("gemini", "Pro", "Gemini"),
            V("claude#k3x9q2", "Max 5x"),
            V("copilot", null, "Copilot"),
            V("claude#a1b2c3", "Pro"),
        ];

        IReadOnlyList<ProviderView> result = AccountTags.Apply(views);

        AssertTags(result, ["팀", null, "개인", null, "개인 3"]);
        Assert.Same(views[1], result[1]);
        Assert.Same(views[3], result[3]);
    }

    [Fact]
    public void Apply_KindsAreCountedSeparately()
    {
        ProviderView[] views =
        [
            V("claude", "Team"),
            V("codex", "Plus", "Codex"),
            V("claude#k3x9q2", "Pro"),
        ];

        // codex has a single panel, so it stays untagged although two kinds are present.
        AssertTags(AccountTags.Apply(views), ["팀", null, "개인"]);
    }

    [Fact]
    public void Apply_NumbersAreRelativeToTheListGiven()
    {
        ProviderView[] enabled = [V("claude#k3x9q2", null), V("claude#a1b2c3", null)];

        AssertTags(AccountTags.Apply(enabled), ["#1", "#2"]);
    }

    [Fact]
    public void Apply_GeminiCopilotAndSystemAreNeverTagged()
    {
        ProviderView[] views =
        [
            V("gemini", "Pro", "Gemini"),
            V("gemini", "Pro", "Gemini"),
            V("copilot", "Pro", "Copilot"),
            V("copilot", "Pro", "Copilot"),
            V("system", null, "PC"),
            V("system", null, "PC"),
        ];

        IReadOnlyList<ProviderView> result = AccountTags.Apply(views);

        Assert.All(result, v => Assert.Null(v.AccountTag));
        for (int i = 0; i < views.Length; i++)
        {
            Assert.Same(views[i], result[i]);
        }
    }

    [Fact]
    public void Apply_ReturnsANewListAndDoesNotMutateTheInput()
    {
        ProviderView[] views = [V("claude", "Team"), V("claude#k3x9q2", "Pro")];

        IReadOnlyList<ProviderView> result = AccountTags.Apply(views);

        Assert.NotSame(views, result);
        Assert.All(views, v => Assert.Null(v.AccountTag));
    }

    [Fact]
    public void Apply_UsesDisplayPlanSoLastGoodPlanCountsWhenCurrentFailed()
    {
        UsageSnapshot good = new("claude", ProviderStatus.Ok, "Team", [Meter("session", "5h", 1)], Now);
        UsageSnapshot failed = Status("claude", ProviderStatus.Error);
        ProviderView[] views = [new ProviderView("claude", "Claude", failed, good), V("claude#k3x9q2", "Pro")];

        AssertTags(AccountTags.Apply(views), ["팀", "개인"]);
    }

    [Fact]
    public void Apply_EmptyList_ReturnsEmpty_AndNullThrows()
    {
        Assert.Empty(AccountTags.Apply([]));
        Assert.Throws<ArgumentNullException>(() => AccountTags.Apply(null!));
    }

    [Fact]
    public void TitleAndShortName_FollowTheTag()
    {
        ProviderView plain = V("claude", "Pro");
        ProviderView tagged = plain with { AccountTag = "팀" };

        Assert.Equal("Claude", plain.Title);
        Assert.Equal("Claude", plain.ShortName);
        Assert.Equal("Claude · 팀", tagged.Title);
        Assert.Equal("팀", tagged.ShortName);
    }

    [Fact]
    public void WidgetPanel_UsesShortName_AndCardAndTooltipUseTitle()
    {
        ProviderView[] views = [V("claude", "Team"), V("claude#k3x9q2", "Pro")];
        ProviderView second = AccountTags.Apply(views)[1];

        WidgetPanelModel panel = WidgetPanelBuilder.Build(second, "session", "weekly", ValueMode.Used, 2, 1, Now);
        DetailCardModel card = DetailCardBuilder.Build(second, ValueMode.Used, Now, Kst);
        string tooltip = TrayTextFormatter.Tooltip(second, null, ValueMode.Used);

        Assert.Equal("개인", panel.DisplayName);
        Assert.Equal("Claude · 개인", card.DisplayName);
        Assert.StartsWith("Claude · 개인 5h 14%", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetPanelAndTooltip_NoDataView_AlsoUseTheTag()
    {
        ProviderView view = View(Status("claude#k3x9q2", ProviderStatus.SignedOut)) with { AccountTag = "개인" };

        WidgetPanelModel panel = WidgetPanelBuilder.Build(view, "session", "weekly", ValueMode.Used, 2, 0, Now);
        string tooltip = TrayTextFormatter.Tooltip(view, null, ValueMode.Used);

        Assert.Equal("개인", panel.DisplayName);
        Assert.StartsWith("Claude · 개인 · ", tooltip, StringComparison.Ordinal);
    }
}

[UseCulture("en-US")]
public class AccountTagsEnglishTests
{
    private static ProviderView V(string id, string? plan)
    {
        var snapshot = new UsageSnapshot(id, ProviderStatus.Ok, plan, [Meter("session", "5h", 14)], Now);
        return new ProviderView(id, "Claude", snapshot, snapshot);
    }

    [Fact]
    public void Apply_UsesEnglishTags()
    {
        IReadOnlyList<ProviderView> result =
            AccountTags.Apply([V("claude", "Team"), V("claude#k3x9q2", "Max 5x"), V("claude#a1b2c3", "Pro")]);

        Assert.Equal(["Team", "Solo", "Solo 3"], result.Select(v => v.AccountTag));
        Assert.Equal("Claude · Team", result[0].Title);
    }

    [Fact]
    public void Reauth_MessageIsEnglish()
    {
        Assert.Equal("Sign in again in Settings", Strings.AccountReauth);
    }
}
