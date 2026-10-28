using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class CopilotUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov1Utc = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Paid_legacy_premium_requests()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-paid-legacy.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("octocat", usage.Login);
        Assert.Equal("Pro", usage.PlanLabel);
        Assert.Equal(["monthly", "reset"], usage.Meters.Select(m => m.Key).ToArray());

        var monthly = usage.Meters[0];
        Assert.Equal("월", monthly.ShortLabel);
        Assert.Equal("월간 프리미엄 요청", monthly.LongLabel);
        Assert.Equal(76.3, monthly.UsedPercent!.Value, 6);
        Assert.Equal(Nov1Utc, monthly.ResetsAt);
        Assert.Null(monthly.TextOverride);

        var reset = usage.Meters[1];
        Assert.Null(reset.UsedPercent);
        Assert.Equal("↻ 11/1", reset.TextOverride);
        Assert.Equal(Nov1Utc, reset.ResetsAt);
    }

    [Fact]
    public void Free_legacy_fields_give_chat_and_completions()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-free-legacy.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Free", usage.PlanLabel);
        Assert.Equal(["monthly", "reset", "completions"], usage.Meters.Select(m => m.Key).ToArray());
        Assert.Equal("월간 채팅", usage.Meters[0].LongLabel);
        Assert.Equal(38, usage.Meters[0].UsedPercent!.Value, 6);
        Assert.Equal(Nov1Utc, usage.Meters[0].ResetsAt);
        Assert.Equal("완성", usage.Meters[2].ShortLabel);
        Assert.Equal(17.5, usage.Meters[2].UsedPercent!.Value, 6);
    }

    [Fact]
    public void Business_token_billing_shows_credits_used_text_instead_of_zero_percent()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-business-tbb.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Business", usage.PlanLabel);
        var monthly = usage.Meters.Single(m => m.Key == "monthly");
        Assert.Null(monthly.UsedPercent);
        Assert.Equal("31 크레딧 사용", monthly.TextOverride);
        Assert.Equal("월간 크레딧", monthly.LongLabel);
        Assert.Equal("↻ 9/1", usage.Meters.Single(m => m.Key == "reset").TextOverride);
        Assert.DoesNotContain(usage.Meters, m => m.Key == "completions");
    }

    [Fact]
    public void Unlimited_org_seat_shows_unlimited_text()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-unlimited-org.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Enterprise", usage.PlanLabel);
        var monthly = usage.Meters.Single(m => m.Key == "monthly");
        Assert.Null(monthly.UsedPercent);
        Assert.Equal("무제한", monthly.TextOverride);
    }

    [Theory]
    [InlineData(-15, 100)]
    [InlineData(120, 0)]
    [InlineData(50, 50)]
    public void Percent_remaining_is_clamped(double percentRemaining, double expectedUsed)
    {
        var json = """{"copilot_plan":"individual","quota_snapshots":{"premium_interactions":{"entitlement":300,"percent_remaining":PCT,"unlimited":false}}}"""
            .Replace("PCT", percentRemaining.ToString(CultureInfo.InvariantCulture));

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(expectedUsed, usage.Meters.Single(m => m.Key == "monthly").UsedPercent!.Value, 6);
    }

    [Fact]
    public void Free_prefers_chat_snapshot_over_premium()
    {
        var json = """
            {"access_type_sku":"free_limited_copilot","quota_snapshots":{
              "chat":{"entitlement":50,"percent_remaining":80,"unlimited":false},
              "premium_interactions":{"entitlement":10,"percent_remaining":10,"unlimited":false},
              "completions":{"entitlement":2000,"percent_remaining":90,"unlimited":false}}}
            """;

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(20, usage.Meters.Single(m => m.Key == "monthly").UsedPercent!.Value, 6);
        Assert.Equal(10, usage.Meters.Single(m => m.Key == "completions").UsedPercent!.Value, 6);
    }

    [Fact]
    public void Free_falls_back_to_premium_when_chat_is_null()
    {
        var json = """{"access_type_sku":"free_limited_copilot","quota_snapshots":{"chat":null,"premium_interactions":{"entitlement":10,"percent_remaining":40,"unlimited":false}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        var monthly = usage.Meters.Single(m => m.Key == "monthly");
        Assert.Equal(60, monthly.UsedPercent!.Value, 6);
        Assert.Equal("월간 프리미엄 요청", monthly.LongLabel);
    }

    [Fact]
    public void Token_based_billing_labels_the_meter_as_credits()
    {
        var json = """{"copilot_plan":"individual_pro","token_based_billing":true,"quota_snapshots":{"premium_interactions":{"entitlement":7000,"percent_remaining":75,"unlimited":false}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal("Pro+", usage.PlanLabel);
        Assert.Equal("월간 크레딧", usage.Meters.Single(m => m.Key == "monthly").LongLabel);
    }

    [Fact]
    public void Malformed_snapshot_entries_are_ignored_and_numeric_strings_accepted()
    {
        var json = """{"copilot_plan":"individual","quota_snapshots":{"chat":"oops","premium_interactions":{"entitlement":"300","percent_remaining":"40"}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(60, usage.Meters.Single(m => m.Key == "monthly").UsedPercent!.Value, 6);
    }

    [Fact]
    public void Zero_entitlement_without_credits_has_no_monthly_meter()
    {
        var json = """{"copilot_plan":"business","quota_snapshots":{"premium_interactions":{"entitlement":0,"percent_remaining":100}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(["reset"], usage.Meters.Select(m => m.Key).ToArray());
    }

    [Theory]
    [InlineData("""{"quota_reset_date_utc":"2026-11-01T00:00:00Z","quota_snapshots":{"premium_interactions":{"quota_reset_at":1793880000}}}""", "2026-11-05T12:00:00+00:00")]
    [InlineData("""{"quota_reset_date_utc":"2026-11-02T06:30:00.000Z","quota_reset_date":"2026-11-03"}""", "2026-11-02T06:30:00+00:00")]
    [InlineData("""{"quota_reset_date":"2026-08-01T00:00:00Z","limited_user_reset_date":"2026-12-01"}""", "2026-08-01T00:00:00+00:00")]
    [InlineData("""{"quota_reset_date":"2026-12-01"}""", "2026-12-01T00:00:00+00:00")]
    [InlineData("""{"limited_user_reset_date":"2026-11-15"}""", "2026-11-15T00:00:00+00:00")]
    [InlineData("""{"quota_reset_date":"not a date"}""", "2026-11-01T00:00:00+00:00")]
    [InlineData("""{}""", "2026-11-01T00:00:00+00:00")]
    public void Reset_precedence(string json, string expectedIso)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement? primary = root.TryGetProperty("quota_snapshots", out var snaps) ? snaps.GetProperty("premium_interactions") : null;

        var reset = CopilotUsageParser.ResolveReset(root, primary, Now);

        Assert.Equal(DateTimeOffset.Parse(expectedIso), reset);
    }

    [Fact]
    public void Reset_text_uses_the_display_time_zone()
    {
        var minusFive = TimeZoneInfo.CreateCustomTimeZone("Test-05", TimeSpan.FromHours(-5), "Test-05", "Test-05");
        var plusNine = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var json = Fixture.Read("copilot/user-paid-legacy.json");

        Assert.Equal("↻ 10/31", CopilotUsageParser.Parse(json, Now, minusFive).Meters.Single(m => m.Key == "reset").TextOverride);
        Assert.Equal("↻ 11/1", CopilotUsageParser.Parse(json, Now, plusNine).Meters.Single(m => m.Key == "reset").TextOverride);
    }

    [Theory]
    [InlineData("""{"access_type_sku":"free_limited_copilot","copilot_plan":"individual"}""", "Free")]
    [InlineData("""{"access_type_sku":"free_educational_quota"}""", "Student")]
    [InlineData("""{"copilot_plan":"individual"}""", "Pro")]
    [InlineData("""{"copilot_plan":"individual_pro"}""", "Pro+")]
    [InlineData("""{"copilot_plan":"individual_max"}""", "Max")]
    [InlineData("""{"copilot_plan":"individual_edu"}""", "Student")]
    [InlineData("""{"copilot_plan":"business"}""", "Business")]
    [InlineData("""{"copilot_plan":"enterprise"}""", "Enterprise")]
    [InlineData("""{"can_signup_for_limited":true}""", null)]
    public void Plan_labels(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, CopilotUsageParser.PlanLabel(doc.RootElement));
    }

    [Fact]
    public void No_plan_and_no_quota_gives_no_meters()
    {
        var usage = CopilotUsageParser.Parse("""{"can_signup_for_limited":true}""", Now, TimeZoneInfo.Utc);

        Assert.Null(usage.PlanLabel);
        Assert.Empty(usage.Meters);
    }
}
