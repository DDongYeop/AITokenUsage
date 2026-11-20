using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Tests.Fakes;
using AITokenUsage.Core.Tests.Localization;
using AITokenUsage.Core.Util;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

[UseCulture("en-US")]
public class ResetPassPresentationTests
{
    private const string PassKey = "reset-passes";
    private static readonly UsageMeter Weekly = Meter("weekly", "7d", 14);

    [Theory]
    [InlineData("claude", true)]
    [InlineData("claude#abc123", false)]
    [InlineData("codex", false)]
    [InlineData("codex#abc123", true)]
    public void Selected_option_survives_save_and_drives_widget_card_and_tray(string id, bool first)
    {
        var view = View(Ok(id, Weekly) with
        {
            ResetPasses = new(4, [
                new("Bonus", new DateTimeOffset(2026, 12, 31, 19, 5, 0, TimeSpan.Zero)),
                new("Bonus", null, NoExpiry: true),
                new(null, null)]),
        });
        var options = MeterOptionList.ForView([], view, ["weekly"]);
        var choice = Assert.Single(options, o => o.Label == "Remaining reset passes");
        Assert.Equal(PassKey, choice.Key);
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.Combine("settings.json"), new NullLog());
        var settings = new AppSettings { Providers = [new() { Id = id,
            Line1Key = first ? choice.Key : Weekly.Key, Line2Key = first ? Weekly.Key : choice.Key }] };
        store.Save(settings);
        var lines = Assert.Single(store.Load().Providers, p => p.Id == id);

        var widget = WidgetPanelBuilder.Build(view, lines.Line1Key, lines.Line2Key, ValueMode.Remaining, 1, 0, Now);
        var pass = first ? widget.Row1 : widget.Row2!;
        Assert.Equal(WidgetRowKind.Text, pass.Kind);
        Assert.Equal("Passes 4", pass.Value);
        Assert.Equal("86%", (first ? widget.Row2! : widget.Row1).Value);
        Assert.Contains("Passes 4", TrayTextFormatter.Tooltip(view, lines, ValueMode.Remaining));
        Assert.Equal(first ? "—" : "86", TrayTextFormatter.IconText(TrayTextFormatter.SelectLine1(view, lines), ValueMode.Remaining));

        var card = DetailCardBuilder.Build(view, ValueMode.Used, Now, Kst);
        var detail = Assert.Single(card.Rows, r => r.Label == choice.Label);
        Assert.Equal("4", detail.Value);
        string[] entries = detail.ResetText.Split(Environment.NewLine);
        Assert.Equal(4, entries.Length);
        Assert.StartsWith("Bonus · 1/1/2027", entries[0]);
        Assert.Contains("4:05", entries[0]);
        Assert.Equal("Bonus · No expiration", entries[1]);
        Assert.Equal("Pass 3 · Expiration unknown", entries[2]);
        Assert.Equal("Passes without details: 1", entries[3]);
    }

    [Theory]
    [InlineData(ProviderStatus.Ok)]
    [InlineData(ProviderStatus.Error)]
    public void Missing_balance_preserves_saved_choice_and_falls_back_to_quota(ProviderStatus status)
    {
        var good = Ok("claude", Weekly) with { ResetPasses = new(7, []) };
        var current = good with { Status = status, ResetPasses = status == ProviderStatus.Ok ? null : good.ResetPasses };
        var view = View(current, good);
        var availableChoice = Assert.Single(MeterOptionList.ForView([], view, []), o => o.Key == PassKey);
        Assert.EndsWith(MeterOptionList.MissingSuffix, availableChoice.Label);
        var saved = Assert.Single(MeterOptionList.ForView([], view, [PassKey]), o => o.Key == PassKey);
        Assert.Equal("Remaining reset passes" + MeterOptionList.MissingSuffix, saved.Label);
        var widget = WidgetPanelBuilder.Build(view, PassKey, "weekly", ValueMode.Used, 1, 0, Now);
        Assert.Equal("14%", widget.Row1.Value);
        Assert.Null(widget.Row2);
        Assert.Single(DetailCardBuilder.Build(view, ValueMode.Used, Now, Kst).Rows);
    }

    [Fact]
    public void Known_zero_without_quota_is_displayable_on_line_two()
    {
        var view = View(Ok("codex") with { ResetPasses = new(0, []) });
        var widget = WidgetPanelBuilder.Build(view, "5h", PassKey, ValueMode.Used, 1, 0, Now);
        Assert.Equal("Passes 0", widget.Row1.Value);
        Assert.Null(widget.Row2);
        var row = Assert.Single(DetailCardBuilder.Build(view, ValueMode.Used, Now, Kst).Rows);
        Assert.Equal("0", row.Value);
        Assert.Empty(row.ResetText);
    }

    [Theory]
    [InlineData("claude", "weekly", "missing")]
    [InlineData("codex", "", "")]
    [InlineData("gemini", PassKey, "missing")]
    public void Pass_never_fills_a_missing_quota_or_appears_for_an_unsupported_provider(string id, string first, string second)
    {
        var view = View(Ok(id, Weekly) with { ResetPasses = new(3, []) });
        var panel = WidgetPanelBuilder.Build(view, first, second, ValueMode.Used, 1, 0, Now);
        Assert.Equal("14%", panel.Row1.Value);
        Assert.Null(panel.Row2);
        var rows = DetailCardBuilder.Build(view, ValueMode.Used, Now, Kst).Rows;
        Assert.Equal(id != "gemini", rows.Any(row => row.Label == "Remaining reset passes"));
        if (id == "gemini")
            Assert.DoesNotContain(MeterOptionList.ForView([], view, []), o => o.Key == PassKey);
    }
}
