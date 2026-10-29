using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Localization;

/// <summary>The English counterparts of the Korean provider tests (the default test culture is ko-KR).</summary>
[UseCulture("en-US")]
public class EnglishProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claude_live_fixture_has_english_labels()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-live.json"));

        Assert.Equal("5h", data.Meters[0].ShortLabel);
        Assert.Equal("Current session (5h)", data.Meters[0].LongLabel);
        Assert.Equal("Wk", data.Meters[1].ShortLabel);
        Assert.Equal("Weekly · All models", data.Meters[1].LongLabel);
        Assert.Equal("Wk·Fa", data.Meters[2].ShortLabel);
        Assert.Equal("Weekly · Fable", data.Meters[2].LongLabel);
    }

    [Fact]
    public void Claude_static_meter_options_and_extra_usage_line_are_english()
    {
        Assert.Equal(
            new[] { "Current session (5h)", "Weekly · All models" },
            new[] { ClaudeUsageParser.SessionLongLabel, ClaudeUsageParser.WeeklyLongLabel });

        ClaudeUsageData extra = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-extra-enabled.json"));
        Assert.Equal("Extra usage $12.40 / $50.00", extra.ExtraLine);

        ClaudeUsageData reached = ClaudeUsageParser.Parse(
            """{"limits":[],"extra_usage":{"is_enabled":true,"monthly_limit":5000,"used_credits":5000,"currency":"USD","spend_limit_reached":true}}""");
        Assert.Equal("Extra usage $50.00 / $50.00 · Limit reached", reached.ExtraLine);
    }

    [Fact]
    public void Codex_messages_and_window_labels_are_english()
    {
        Assert.Equal("API key sign-in — no plan limits", CodexUsageProvider.MessageApiKey);
        Assert.Equal("Sign-in required — run codex login in a terminal", CodexUsageProvider.MessageSignedOut);

        var meters = CodexWindowLabeler.ToMeters(
            new CodexWindow(77, 300, Now), new CodexWindow(8, 10080, Now));
        Assert.Equal("5h", meters[0].ShortLabel);
        Assert.Equal("5-hour limit", meters[0].LongLabel);
        Assert.Equal("Wk", meters[1].ShortLabel);
        Assert.Equal("Weekly limit", meters[1].LongLabel);

        Assert.Equal("Mo", CodexWindowLabeler.Label(43200, false).ShortLabel);
        Assert.Equal("Monthly limit", CodexWindowLabeler.Label(43200, false).LongLabel);
    }

    [Fact]
    public void Codex_labels_follow_the_current_culture_instead_of_a_cached_value()
    {
        Assert.Equal("Wk", CodexWindowLabeler.Label(10080, false).ShortLabel);

        var korean = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
        System.Globalization.CultureInfo.CurrentUICulture = korean;
        Assert.Equal("주", CodexWindowLabeler.Label(10080, false).ShortLabel);
    }

    [Fact]
    public void Copilot_paid_fixture_has_english_labels_and_unchanged_reset_text()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-paid-legacy.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Mo", usage.Meters[0].ShortLabel);
        Assert.Equal("Monthly premium requests", usage.Meters[0].LongLabel);
        Assert.Equal("↻ 11/1", usage.Meters[1].TextOverride);
    }

    [Fact]
    public void Copilot_free_fixture_has_english_completions_label()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-free-legacy.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Monthly chat", usage.Meters[0].LongLabel);
        Assert.Equal("Cmp", usage.Meters[2].ShortLabel);
        Assert.Equal("Code completions (monthly)", usage.Meters[2].LongLabel);
    }

    [Fact]
    public void Copilot_unlimited_and_credit_texts_are_english()
    {
        var unlimited = CopilotUsageParser.Parse(Fixture.Read("copilot/user-unlimited-org.json"), Now, TimeZoneInfo.Utc);
        Assert.Equal("Unlimited", unlimited.Meters.Single(m => m.Key == "monthly").TextOverride);

        var business = CopilotUsageParser.Parse(Fixture.Read("copilot/user-business-tbb.json"), Now, TimeZoneInfo.Utc);
        Assert.Equal("31 credits used", business.Meters.Single(m => m.Key == "monthly").TextOverride);
    }

    [Fact]
    public void Copilot_messages_are_english()
    {
        Assert.Equal("Sign in to GitHub in Settings", CopilotMessages.SignInRequired);
        Assert.Equal("GitHub response error (HTTP 502)", CopilotMessages.ServerError(502));
        Assert.Equal("The code expired. Try again", new DeviceFlowException(DeviceFlowError.Expired).UserMessage);
        Assert.Equal("Network error — try again shortly", new DeviceFlowException(DeviceFlowError.Network).UserMessage);
    }

    [Fact]
    public void Copilot_meter_options_are_english()
    {
        Assert.Equal(
            new[] { "Monthly usage", "Reset date", "Code completions (Free)" },
            CopilotUsageParser.MeterOptions.Select(o => o.Label).ToArray());
    }

    [Fact]
    public void Gemini_messages_are_english_and_keep_the_discontinued_marker()
    {
        Assert.Contains(Strings.StatusDiscontinuedMarker, GeminiMessages.ConsumerShutdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discontinued", GeminiMessages.ConsumerShutdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Gemini server error (HTTP 500)", GeminiMessages.ServerError(500));
        Assert.Equal("Rate limited — try again shortly", GeminiMessages.RateLimited);
    }

    [Fact]
    public void Gemini_meter_labels_and_options_are_english()
    {
        var meters = GeminiQuotaParser.Parse(Fixture.Read("gemini/quota-buckets.json"));
        Assert.Equal("Pro daily", meters[0].LongLabel);
        Assert.Equal("Flash daily", meters[1].LongLabel);
        Assert.Equal("Flash-Lite daily", meters[2].LongLabel);
        Assert.Equal("Pro", meters[0].ShortLabel);
        Assert.Equal("Fl", meters[1].ShortLabel);
        Assert.Equal("Lt", meters[2].ShortLabel);

        Assert.Equal(
            new[] { "Pro daily", "Flash daily", "Flash-Lite daily" },
            GeminiQuotaParser.MeterOptions.Select(o => o.Label).ToArray());
    }

    [Fact]
    public void Korean_discontinued_message_also_contains_the_marker()
    {
        var korean = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
        System.Globalization.CultureInfo.CurrentUICulture = korean;

        Assert.Contains(Strings.StatusDiscontinuedMarker, GeminiMessages.ConsumerShutdown, StringComparison.OrdinalIgnoreCase);
    }
}
