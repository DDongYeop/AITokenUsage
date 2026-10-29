using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class TrayTextFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private static readonly ProviderSettings ClaudeLines =
        new() { Id = ProviderIds.Claude, Line1Key = "session", Line2Key = "weekly" };

    private static UsageMeter Meter(string key, string shortLabel, double? used, string? textOverride = null) =>
        new(key, shortLabel, shortLabel + " (long)", used, null, null, textOverride);

    private static UsageSnapshot ClaudeSnapshot(double session, double weekly) =>
        new(ProviderIds.Claude, ProviderStatus.Ok, "Team · Max 5x",
            [Meter("session", "5h", session), Meter("weekly", "주", weekly), Meter("weekly:fable", "주·Fa", 3)],
            Now);

    private static ProviderView ClaudeOk(double session, double weekly)
    {
        UsageSnapshot snapshot = ClaudeSnapshot(session, weekly);
        return new ProviderView(ProviderIds.Claude, "Claude", snapshot, snapshot);
    }

    [Fact]
    public void Tooltip_WithSystemLine_AppendsItOnANewLine()
    {
        string tooltip = TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used, "PC CPU 23% · RAM 61%");

        Assert.Equal("Claude 5h 14% · 주 50%\nPC CPU 23% · RAM 61%", tooltip);
    }

    [Fact]
    public void Tooltip_NullOrEmptySystemLine_IsUnchanged()
    {
        string expected = TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used);

        Assert.Equal(expected, TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used, null));
        Assert.Equal(expected, TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used, ""));
    }

    [Fact]
    public void Tooltip_SystemLineThatWouldExceedLimit_IsLeftOut()
    {
        string baseText = TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used);
        int room = TrayTextFormatter.MaxTooltipLength - baseText.Length - 1; // the "\n"

        Assert.Equal(baseText + "\n" + new string('x', room),
            TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used, new string('x', room)));
        Assert.Equal(baseText,
            TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used, new string('x', room + 1)));
    }

    [Fact]
    public void Tooltip_WithoutView_AppendsSystemLineToAppTitle()
    {
        Assert.Equal("AI 사용량\nPC CPU 1% · RAM 2%", TrayTextFormatter.Tooltip(null, null, ValueMode.Used, "PC CPU 1% · RAM 2%"));
    }

    [Fact]
    public void Tooltip_WithoutView_ReturnsAppTitle()
    {
        Assert.Equal("AI 사용량", TrayTextFormatter.Tooltip(null, null, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_TwoMeters_MatchesSpecExample()
    {
        Assert.Equal("Claude 5h 14% · 주 50%", TrayTextFormatter.Tooltip(ClaudeOk(14, 50), ClaudeLines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_RemainingMode_ShowsRemainingPercent()
    {
        Assert.Equal("Claude 5h 86% · 주 70%", TrayTextFormatter.Tooltip(ClaudeOk(14, 30), ClaudeLines, ValueMode.Remaining));
    }

    [Fact]
    public void Tooltip_FollowsConfiguredLineKeys()
    {
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "weekly:fable", Line2Key = "session" };

        Assert.Equal("Claude 주·Fa 3% · 5h 14%", TrayTextFormatter.Tooltip(ClaudeOk(14, 50), lines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_UnknownLine1Key_FallsBackToFirstMeterNotOnLine2()
    {
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "weekly:opus", Line2Key = "weekly" };

        Assert.Equal("Claude 5h 14% · 주 50%", TrayTextFormatter.Tooltip(ClaudeOk(14, 50), lines, ValueMode.Used));
    }

    [Fact]
    public void SelectLine1_Line1KeyMissingAndLine2KeyPresent_MatchesWidgetLine1()
    {
        ProviderView view = ClaudeOk(14, 50);
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "weekly:opus", Line2Key = "session" };

        // The widget (Task 17 WidgetPanelBuilder) picks its lines with exactly this call.
        var (widgetLine1, widgetLine2) = MeterSelector.Select(view.DisplayMeters, lines.Line1Key, lines.Line2Key);

        Assert.Same(widgetLine1, TrayTextFormatter.SelectLine1(view, lines));
        Assert.Same(widgetLine2, TrayTextFormatter.SelectLine2(view, lines));
        Assert.Equal("weekly", TrayTextFormatter.SelectLine1(view, lines)?.Key);
        Assert.Equal("Claude 주 50% · 5h 14%", TrayTextFormatter.Tooltip(view, lines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_SameKeyOnBothLines_IsShownOnce()
    {
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "session", Line2Key = "session" };

        Assert.Equal("Claude 5h 14%", TrayTextFormatter.Tooltip(ClaudeOk(14, 50), lines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_LineKeysAreCaseSensitive()
    {
        // "SESSION" is not a key (Ordinal), so line 2 falls back to the first meter line 1 does not use.
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "session", Line2Key = "SESSION" };

        Assert.Equal("Claude 5h 14% · 주 50%", TrayTextFormatter.Tooltip(ClaudeOk(14, 50), lines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_UnknownLine2Key_FallsBackToFirstMeterNotOnLine1()
    {
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "weekly", Line2Key = "nope" };

        Assert.Equal("Claude 주 50% · 5h 14%", TrayTextFormatter.Tooltip(ClaudeOk(14, 50), lines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_TextOverrideMeter_ShowsTextOnly()
    {
        var snapshot = new UsageSnapshot(ProviderIds.Copilot, ProviderStatus.Ok, "Pro",
            [Meter("monthly", "월", 40), Meter("reset", "↻", null, "↻ 11/1")], Now);
        var view = new ProviderView(ProviderIds.Copilot, "Copilot", snapshot, snapshot);
        var lines = new ProviderSettings { Id = ProviderIds.Copilot, Line1Key = "monthly", Line2Key = "reset" };

        Assert.Equal("Copilot 월 40% · ↻ 11/1", TrayTextFormatter.Tooltip(view, lines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_NoData_ShowsStatusMessage()
    {
        UsageSnapshot current = UsageSnapshot.WithStatus(ProviderIds.Claude, ProviderStatus.SignedOut, Now,
            "로그인 필요 — 터미널에서 claude 실행 후 /login");
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, null);

        Assert.Equal("Claude · 로그인 필요 — 터미널에서 claude 실행 후 /login",
            TrayTextFormatter.Tooltip(view, ClaudeLines, ValueMode.Used));
    }

    [Theory]
    [InlineData(ProviderStatus.NotInstalled, "설치 안 됨")]
    [InlineData(ProviderStatus.SignedOut, "로그인 필요")]
    [InlineData(ProviderStatus.WaitingForCliRefresh, "갱신 대기")]
    [InlineData(ProviderStatus.Unsupported, "지원 안 됨")]
    [InlineData(ProviderStatus.RateLimited, "요청 제한")]
    [InlineData(ProviderStatus.Error, "오류")]
    [InlineData(ProviderStatus.Ok, "불러오는 중…")]
    public void Tooltip_NoDataWithoutMessage_UsesStatusText(ProviderStatus status, string expected)
    {
        UsageSnapshot current = UsageSnapshot.WithStatus(ProviderIds.Codex, status, Now);
        var view = new ProviderView(ProviderIds.Codex, "Codex", current, null);

        Assert.Equal($"Codex · {expected}", TrayTextFormatter.Tooltip(view, null, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_StaleValues_AppendsSuffix()
    {
        UsageSnapshot good = ClaudeSnapshot(14, 50);
        UsageSnapshot current = UsageSnapshot.WithStatus(ProviderIds.Claude, ProviderStatus.RateLimited, Now,
            "요청 제한 — 14:20 이후 재시도", TimeSpan.FromMinutes(5));
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, good);

        Assert.Equal("Claude 5h 14% · 주 50% (이전 값)", TrayTextFormatter.Tooltip(view, ClaudeLines, ValueMode.Used));
    }

    [Fact]
    public void Tooltip_IsTruncatedTo127Characters()
    {
        UsageSnapshot current = UsageSnapshot.WithStatus(ProviderIds.Claude, ProviderStatus.Error, Now, new string('가', 300));
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, null);

        string tooltip = TrayTextFormatter.Tooltip(view, ClaudeLines, ValueMode.Used);

        Assert.Equal(TrayTextFormatter.MaxTooltipLength, tooltip.Length);
        Assert.StartsWith("Claude · 가", tooltip);
        Assert.EndsWith("…", tooltip);
    }

    [Theory]
    [InlineData(14.4, ValueMode.Used, "14")]
    [InlineData(14.5, ValueMode.Used, "15")]
    [InlineData(99.6, ValueMode.Used, "100")]
    [InlineData(120, ValueMode.Used, "100")]
    [InlineData(-3, ValueMode.Used, "0")]
    [InlineData(14, ValueMode.Remaining, "86")]
    [InlineData(0, ValueMode.Remaining, "100")]
    public void IconText_RoundsAndClamps(double used, ValueMode mode, string expected)
    {
        Assert.Equal(expected, TrayTextFormatter.IconText(Meter("session", "5h", used), mode));
    }

    [Fact]
    public void IconText_WithoutPercent_ReturnsEmDash()
    {
        Assert.Equal("—", TrayTextFormatter.NoValue);
        Assert.Equal(MeterSelector.MissingValue, TrayTextFormatter.NoValue);
        Assert.Equal("—", TrayTextFormatter.IconText(null, ValueMode.Used));
        Assert.Equal("—", TrayTextFormatter.IconText(Meter("reset", "↻", null, "↻ 11/1"), ValueMode.Used));
    }

    [Fact]
    public void FormatValue_TextOverrideWins_ElseRoundedPercent_ElseDash()
    {
        Assert.Equal("↻ 11/1", TrayTextFormatter.FormatValue(Meter("reset", "↻", null, "↻ 11/1"), ValueMode.Used));
        Assert.Equal("15%", TrayTextFormatter.FormatValue(Meter("session", "5h", 14.5), ValueMode.Used));
        Assert.Equal("—", TrayTextFormatter.FormatValue(Meter("x", "x", null), ValueMode.Remaining));
    }
}
