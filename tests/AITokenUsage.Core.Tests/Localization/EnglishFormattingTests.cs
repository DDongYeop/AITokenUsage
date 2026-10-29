using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Localization;

/// <summary>The English counterparts of the Korean presentation tests (the default test culture is ko-KR).</summary>
[UseCulture("en-US")]
public class EnglishFormattingTests
{
    private static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);
    private static readonly TimeZoneInfo KstZone =
        TimeZoneInfo.CreateCustomTimeZone("KST", KstOffset, "Korea Standard Time", "Korea Standard Time");

    private readonly ResetTimeFormatter _formatter = new(KstZone);

    private static DateTimeOffset At(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, KstOffset);

    [Fact]
    public void FormatRelative_SameDay_ShowsDurationTodayAndTwelveHourClock()
    {
        Assert.Equal("in 3h 12m · Today 3:50 PM", _formatter.FormatRelative(At(10, 28, 15, 50), At(10, 28, 12, 38)));
    }

    [Fact]
    public void FormatRelative_LaterDay_ShowsDaysHoursAndEnglishWeekday()
    {
        // 2026-10-30 is a Friday.
        Assert.Equal("in 1d 8h · Fri 10/30 5:00 AM", _formatter.FormatRelative(At(10, 30, 5, 0), At(10, 28, 21, 0)));
    }

    [Fact]
    public void FormatRelative_LessThanAMinute_ShowsResetsSoon()
    {
        Assert.Equal("Resets soon", _formatter.FormatRelative(At(10, 28, 12, 0, 30), At(10, 28, 12, 0)));
    }

    [Fact]
    public void FormatAgo_UsesCompactEnglishUnits()
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal("just now", _formatter.FormatAgo(now.AddSeconds(-30), now));
        Assert.Equal("3m ago", _formatter.FormatAgo(now.AddMinutes(-3), now));
        Assert.Equal("2h ago", _formatter.FormatAgo(now.AddHours(-2), now));
        Assert.Equal("3d ago", _formatter.FormatAgo(now.AddDays(-3), now));
    }

    [Fact]
    public void FormatShortDateWithDay_PutsTheEnglishWeekdayFirst()
    {
        Assert.Equal("Fri 10/30", _formatter.FormatShortDateWithDay(At(10, 30, 5, 0)));
    }

    [Fact]
    public void ForSettings_SignedOut_ShowsSignInRequired()
    {
        UsageSnapshot snapshot = Status("claude", ProviderStatus.SignedOut);

        Assert.Equal("Sign-in required", ProviderStatusText.ForSettings(View(snapshot), Now, Kst));
    }

    [Fact]
    public void ForSettings_NotInstalled_ShowsNotInstalled()
    {
        UsageSnapshot snapshot = Status("claude", ProviderStatus.NotInstalled);

        Assert.Equal("Not installed", ProviderStatusText.ForSettings(View(snapshot), Now, Kst));
    }

    [Fact]
    public void ForSettings_Ok_ShowsConnectedPlanAndAge()
    {
        UsageMeter session = Meter("session", "5h", 14);

        Assert.Equal("Connected · Pro · 3m ago", ProviderStatusText.ForSettings(View(Ok("claude", session)), Now, Kst));
    }

    [Fact]
    public void ForSettings_RateLimited_ShowsRetryClock()
    {
        UsageSnapshot snapshot = Status("claude", ProviderStatus.RateLimited, null, TimeSpan.FromMinutes(21));

        // Fetched 11:59 local + 21 minutes.
        Assert.Equal("Rate limited — retry after 12:20 PM", ProviderStatusText.ForSettings(View(snapshot), Now, Kst));
    }

    [Fact]
    public void ForCard_Ok_ShowsUpdatedAgo()
    {
        UsageMeter session = Meter("session", "5h", 14);

        Assert.Equal(new[] { "Updated 3m ago" }, ProviderStatusText.ForCard(View(Ok("claude", session)), Now, Kst));
    }

    [Fact]
    public void ForCard_LastGood_ShowsLastGoodValueAge()
    {
        UsageMeter session = Meter("session", "5h", 14);
        var view = View(Status("claude", ProviderStatus.Error, "Network error"), Ok("claude", session));

        Assert.Equal(
            new[] { "Network error", "Last good value · 3m ago" },
            ProviderStatusText.ForCard(view, Now, Kst));
    }

    [Fact]
    public void ForSettings_WaitingForCliRefresh_NamesTheCli()
    {
        UsageSnapshot snapshot = Status("claude", ProviderStatus.WaitingForCliRefresh);

        Assert.Equal("Waiting for Claude Code to refresh its sign-in", ProviderStatusText.ForSettings(View(snapshot), Now, Kst));
    }

    [Fact]
    public void ShortStatus_RateLimited_ShowsRateLimited()
    {
        Assert.Equal("Rate limited", WidgetPanelBuilder.ShortStatus(View(Status("claude", ProviderStatus.RateLimited))));
    }

    [Theory]
    [InlineData(ProviderStatus.NotInstalled, null, "Not installed")]
    [InlineData(ProviderStatus.SignedOut, null, "Sign-in required")]
    [InlineData(ProviderStatus.WaitingForCliRefresh, null, "Waiting for refresh")]
    [InlineData(ProviderStatus.Unsupported, "Personal accounts discontinued (2026-06-18)", "Discontinued")]
    [InlineData(ProviderStatus.Unsupported, "API key sign-in", "Not supported")]
    [InlineData(ProviderStatus.Error, null, "Error")]
    public void ShortStatus_UsesTheGlossary(ProviderStatus status, string? message, string expected)
    {
        Assert.Equal(expected, WidgetPanelBuilder.ShortStatus(View(Status("claude", status, message))));
    }

    [Fact]
    public void Empty_ShowsEnglishTitleAndMessage()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Empty();

        Assert.Equal("AI Usage", model.DisplayName);
        Assert.Equal("Right-click for settings", model.Row1.Value);
    }

    [Fact]
    public void LoadingMessage_IsEnglish()
    {
        Assert.Equal("Loading…", UsageService.LoadingMessage);
    }

    [Fact]
    public void Tooltip_UsesEnglishLabels()
    {
        Assert.Equal("AI Usage", TrayTextFormatter.Tooltip(null, null, ValueMode.Used));

        var signedOut = View(Status("claude", ProviderStatus.SignedOut));
        Assert.Equal("Claude · Sign-in required", TrayTextFormatter.Tooltip(signedOut, null, ValueMode.Used));

        var stale = View(Status("claude", ProviderStatus.Error), Ok("claude", Meter("session", "5h", 14), Meter("weekly", "Wk", 50)));
        var lines = new ProviderSettings { Id = ProviderIds.Claude, Line1Key = "session", Line2Key = "weekly" };
        Assert.Equal("Claude 5h 14% · Wk 50% (stale)", TrayTextFormatter.Tooltip(stale, lines, ValueMode.Used));
    }

    [Fact]
    public void StatusText_UsesTheGlossary()
    {
        Assert.Equal("Sign-in required", TrayTextFormatter.StatusText(ProviderStatus.SignedOut));
        Assert.Equal("Rate limited", TrayTextFormatter.StatusText(ProviderStatus.RateLimited));
        Assert.Equal("Loading…", TrayTextFormatter.StatusText(ProviderStatus.Ok));
    }

    [Fact]
    public void MonitorChoices_UseEnglishLabels()
    {
        var monitors = new[]
        {
            new MonitorDescriptor("A", 0, 0, 1920, 1080, true),
            new MonitorDescriptor("B", 1920, 0, 2560, 1440, false),
        };

        IReadOnlyList<MonitorChoice> choices = MonitorChoices.Build(monitors, "GONE");

        Assert.Equal("Primary monitor", choices[0].Label);
        Assert.Equal("Monitor 1 (1920×1080, Primary)", choices[1].Label);
        Assert.Equal("Monitor 2 (2560×1440, Right)", choices[2].Label);
        Assert.Equal("GONE (disconnected)", choices[3].Label);
    }

    [Fact]
    public void MeterOptionList_MissingSelection_GetsEnglishSuffix()
    {
        IReadOnlyList<MeterOption> options = MeterOptionList.Build([], [], ["weekly"]);

        Assert.Equal("weekly (no current data)", options[0].Label);
    }
}
