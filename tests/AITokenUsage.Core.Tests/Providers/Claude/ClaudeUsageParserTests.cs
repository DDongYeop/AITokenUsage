using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeUsageParserTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static UsageMeter Meter(ClaudeUsageData data, string key) => Assert.Single(data.Meters, m => m.Key == key);

    [Fact]
    public void Live_shape_maps_session_weekly_and_scoped_rows()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-live.json"));

        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, data.Meters.Select(m => m.Key));

        UsageMeter session = data.Meters[0];
        Assert.Equal("5h", session.ShortLabel);
        Assert.Equal("현재 세션 (5시간)", session.LongLabel);
        Assert.Equal(14.0, session.UsedPercent);
        Assert.Equal(Utc(2026, 10, 28, 6, 50), session.ResetsAt);
        Assert.Equal(Severity.Normal, session.ServerSeverity);
        Assert.Null(session.TextOverride);

        UsageMeter weekly = data.Meters[1];
        Assert.Equal("주", weekly.ShortLabel);
        Assert.Equal("주간 · 모든 모델", weekly.LongLabel);
        Assert.Equal(50.0, weekly.UsedPercent);
        Assert.Equal(Utc(2026, 10, 29, 20, 0), weekly.ResetsAt);
        Assert.Equal(Severity.Normal, weekly.ServerSeverity);

        UsageMeter fable = data.Meters[2];
        Assert.Equal("주·Fa", fable.ShortLabel);
        Assert.Equal("주간 · Fable", fable.LongLabel);
        Assert.Equal(35.0, fable.UsedPercent);
        Assert.Equal(Utc(2026, 10, 29, 20, 0), fable.ResetsAt);

        Assert.Null(data.ExtraLine);
    }

    [Fact]
    public void Null_limits_fall_back_to_legacy_window_keys()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-limits-null.json"));

        Assert.Equal(new[] { "session", "weekly", "weekly:sonnet" }, data.Meters.Select(m => m.Key));

        UsageMeter session = Meter(data, "session");
        Assert.Equal(26.5, session.UsedPercent);
        Assert.Equal(Utc(2026, 10, 28, 8, 10), session.ResetsAt);
        Assert.Null(session.ServerSeverity);

        UsageMeter weekly = Meter(data, "weekly");
        Assert.Equal(74.0, weekly.UsedPercent);
        Assert.Equal(Utc(2026, 11, 1, 5, 0), weekly.ResetsAt);

        UsageMeter sonnet = Meter(data, "weekly:sonnet");
        Assert.Equal("주·So", sonnet.ShortLabel);
        Assert.Equal("주간 · Sonnet", sonnet.LongLabel);
        Assert.Equal(12.0, sonnet.UsedPercent);
        Assert.Null(sonnet.ServerSeverity);

        Assert.Null(data.ExtraLine);
    }

    [Fact]
    public void Empty_limits_fall_back_to_legacy_window_keys()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-limits-empty.json"));

        Assert.Equal(new[] { "session", "weekly" }, data.Meters.Select(m => m.Key));
        Assert.Equal(5.0, Meter(data, "session").UsedPercent);
        Assert.Equal(Utc(2026, 10, 28, 9, 0), Meter(data, "session").ResetsAt);
        Assert.Equal(20.0, Meter(data, "weekly").UsedPercent);
        Assert.Null(Meter(data, "weekly").ResetsAt);
    }

    [Fact]
    public void Body_without_any_window_has_no_meters()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(
            """{"five_hour":null,"seven_day":null,"seven_day_sonnet":null,"seven_day_opus":null,"limits":[]}""");

        Assert.Empty(data.Meters);
        Assert.Null(data.ExtraLine);
    }

    [Fact]
    public void Scoped_rows_become_weekly_slug_meters_and_limits_win_over_legacy_keys()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-scoped.json"));

        Assert.Equal(
            new[] { "session", "weekly", "weekly:fable", "weekly:fable-5.1", "weekly:claude-design", "weekly:opus" },
            data.Meters.Select(m => m.Key));

        UsageMeter session = Meter(data, "session");
        Assert.Equal(62.0, session.UsedPercent);
        Assert.Equal(Severity.Warning, session.ServerSeverity);
        Assert.Equal(Utc(2026, 10, 28, 6, 50), session.ResetsAt);

        UsageMeter weekly = Meter(data, "weekly");
        Assert.Equal(91.0, weekly.UsedPercent);
        Assert.Equal(Severity.Critical, weekly.ServerSeverity);

        UsageMeter fable = Meter(data, "weekly:fable");
        Assert.Equal(35.0, fable.UsedPercent);
        Assert.Equal("주·Fa", fable.ShortLabel);
        Assert.Equal("주간 · Fable", fable.LongLabel);
        Assert.Equal(Severity.Normal, fable.ServerSeverity);

        UsageMeter fable51 = Meter(data, "weekly:fable-5.1");
        Assert.Equal(12.5, fable51.UsedPercent);
        Assert.Equal("주·Fa", fable51.ShortLabel);
        Assert.Equal("주간 · Fable 5.1", fable51.LongLabel);

        UsageMeter design = Meter(data, "weekly:claude-design");
        Assert.Equal(8.0, design.UsedPercent);
        Assert.Equal("주·Cl", design.ShortLabel);
        Assert.Equal("주간 · Claude Design", design.LongLabel);
        Assert.Null(design.ResetsAt);

        UsageMeter opus = Meter(data, "weekly:opus");
        Assert.Equal(40.0, opus.UsedPercent);
        Assert.Equal("주·Op", opus.ShortLabel);
        Assert.Equal("주간 · Opus", opus.LongLabel);
        Assert.Null(opus.ServerSeverity);
    }

    [Fact]
    public void Malformed_limits_rows_are_skipped_without_dropping_good_rows()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-malformed-limits.json"));

        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, data.Meters.Select(m => m.Key));

        // The session row has a string percent, so the legacy five_hour window is used.
        UsageMeter session = Meter(data, "session");
        Assert.Equal(39.0, session.UsedPercent);
        Assert.Null(session.ServerSeverity);
        Assert.Equal(Utc(2026, 10, 28, 6, 50), session.ResetsAt);

        // The weekly_all row is valid; its bad severity and bad date become null individually.
        UsageMeter weekly = Meter(data, "weekly");
        Assert.Equal(54.0, weekly.UsedPercent);
        Assert.Null(weekly.ServerSeverity);
        Assert.Null(weekly.ResetsAt);

        UsageMeter fable = Meter(data, "weekly:fable");
        Assert.Equal(20.5, fable.UsedPercent);
        Assert.Equal(Severity.Warning, fable.ServerSeverity);
        Assert.Equal(Utc(2026, 10, 29, 20, 0), fable.ResetsAt);
    }

    [Fact]
    public void Extra_usage_enabled_is_shown_in_dollars()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-extra-enabled.json"));

        Assert.Equal("추가 사용 $12.40 / $50.00", data.ExtraLine);
        Assert.Equal(100.0, Meter(data, "session").UsedPercent);
        Assert.Equal(Severity.Critical, Meter(data, "session").ServerSeverity);
    }

    [Theory]
    [InlineData("""{"is_enabled":true,"monthly_limit":100000,"used_credits":0,"currency":null,"decimal_places":null}""", "추가 사용 $0.00 / $1,000.00")]
    [InlineData("""{"is_enabled":true,"monthly_limit":5000,"used_credits":1240,"currency":"EUR","decimal_places":2}""", "추가 사용 12.40 EUR / 50.00 EUR")]
    [InlineData("""{"is_enabled":true,"monthly_limit":null,"used_credits":1240}""", "추가 사용 $12.40")]
    [InlineData("""{"is_enabled":true,"monthly_limit":5000,"used_credits":5000,"currency":"USD","spend_limit_reached":true}""", "추가 사용 $50.00 / $50.00 · 한도 도달")]
    [InlineData("""{"is_enabled":true,"monthly_limit":500,"used_credits":124,"currency":"JPY","decimal_places":0}""", "추가 사용 124 JPY / 500 JPY")]
    public void Extra_usage_converts_minor_units(string extraUsageJson, string expected)
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse("{\"limits\":[],\"extra_usage\":" + extraUsageJson + "}");

        Assert.Equal(expected, data.ExtraLine);
    }

    [Theory]
    [InlineData("""{"is_enabled":false,"monthly_limit":5000,"used_credits":1240}""")]
    [InlineData("""{"monthly_limit":5000,"used_credits":1240}""")]
    [InlineData("null")]
    public void Extra_usage_not_enabled_has_no_line(string extraUsageJson)
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse("{\"limits\":[],\"extra_usage\":" + extraUsageJson + "}");

        Assert.Null(data.ExtraLine);
    }

    [Theory]
    [InlineData("2026-10-28T06:49:59.595952+00:00", "2026-10-28T06:50:00+00:00")]
    [InlineData("2026-10-28T06:50:00.478089+00:00", "2026-10-28T06:50:00+00:00")]
    [InlineData("2026-10-28T06:50:29.999+00:00", "2026-10-28T06:50:00+00:00")]
    [InlineData("2026-10-28T06:50:30+00:00", "2026-10-28T06:51:00+00:00")]
    [InlineData("2026-10-28T15:49:59.9+09:00", "2026-10-28T06:50:00+00:00")]
    public void Resets_at_is_rounded_to_the_nearest_minute(string input, string expected)
    {
        DateTimeOffset rounded = ClaudeUsageParser.RoundToMinute(DateTimeOffset.Parse(input, CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), rounded);
        Assert.Equal(TimeSpan.Zero, rounded.Offset);
    }

    [Theory]
    [InlineData("team", "default_claude_max_5x", "Team · Max 5x")]
    [InlineData("max", "default_claude_max_20x", "Max 20x")]
    [InlineData("pro", "default_claude_pro", "Pro")]
    [InlineData("enterprise", null, "Enterprise")]
    [InlineData(null, "default_claude_max_5x", "Max 5x")]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void Plan_label_capitalizes_subscription_and_appends_max_multiplier(string? subscriptionType, string? tier, string? expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.FormatPlanLabel(subscriptionType, tier));
    }

    [Theory]
    [InlineData("normal", Severity.Normal)]
    [InlineData("warning", Severity.Warning)]
    [InlineData("CRITICAL", Severity.Critical)]
    [InlineData("purple", null)]
    [InlineData(null, null)]
    public void Severity_strings_map_to_enum(string? value, Severity? expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.ParseSeverity(value));
    }

    [Theory]
    [InlineData("Fable", "fable")]
    [InlineData("Fable 5.1", "fable-5.1")]
    [InlineData("  Claude   Design ", "claude-design")]
    public void Slug_is_lower_case_with_dashes(string displayName, string expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.Slug(displayName));
    }

    [Fact]
    public void Detects_anthropic_error_bodies()
    {
        Assert.True(ClaudeUsageParser.IsAnthropicErrorBody(Fixture.Read("claude/error-403-anthropic.json")));
        Assert.True(ClaudeUsageParser.IsAnthropicErrorBody(Fixture.Read("claude/error-429.json")));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("<html><body>403 Forbidden</body></html>"));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("error code: 1010"));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody(""));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody(null));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("""{"error":"forbidden"}"""));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("""{"error":{"message":"no type"}}"""));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Non_object_body_throws_json_exception(string body)
    {
        Assert.ThrowsAny<JsonException>(() => ClaudeUsageParser.Parse(body));
    }
}
