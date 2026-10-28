using System.Text.Json;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexUsageParserTests
{
    private static readonly DateTimeOffset Now = CodexTestData.Now;
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.FromUnixTimeSeconds(1793163600);  // 2026-10-28 05:00Z
    private static readonly DateTimeOffset WeeklyReset = DateTimeOffset.FromUnixTimeSeconds(1793415600);    // 2026-10-31 03:00Z

    [Fact]
    public void ParseWham_Plus_FiveHourAndWeekly()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("codex/wham-plus.json"), Now);

        Assert.Equal("plus", result.PlanType);
        Assert.Equal(2, result.Meters.Count);
        var fiveHour = result.Meters[0];
        Assert.Equal(("5h", "5h", "5시간 한도"), (fiveHour.Key, fiveHour.ShortLabel, fiveHour.LongLabel));
        Assert.Equal(42, fiveHour.UsedPercent);
        Assert.Equal(FiveHourReset, fiveHour.ResetsAt);
        Assert.Null(fiveHour.ServerSeverity);
        var weekly = result.Meters[1];
        Assert.Equal(("weekly", "주", "주간 한도"), (weekly.Key, weekly.ShortLabel, weekly.LongLabel));
        Assert.Equal(5, weekly.UsedPercent);
        Assert.Equal(WeeklyReset, weekly.ResetsAt);
    }

    [Fact]
    public void ParseWham_ProWithSpark_AddsAdditionalLimitMeters_AndSkipsBrokenEntries()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("codex/wham-pro-spark.json"), Now);

        Assert.Equal("pro", result.PlanType);
        Assert.Equal(
            new[] { "5h", "weekly", "gpt_5_3_codex_spark:5h", "gpt_5_3_codex_spark:weekly" },
            result.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(33.5, result.Meters[1].UsedPercent);
        var sparkWeekly = result.Meters[3];
        Assert.Equal("주·Sp", sparkWeekly.ShortLabel);
        Assert.Equal("GPT-5.3-Codex-Spark · 주간 한도", sparkWeekly.LongLabel);
        Assert.Equal(100, sparkWeekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793761200), sparkWeekly.ResetsAt);
    }

    [Fact]
    public void ParseWham_Free_SingleThirtyDayPrimary()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("codex/wham-free.json"), Now);

        Assert.Equal("free", result.PlanType);
        var meter = Assert.Single(result.Meters);
        Assert.Equal(("monthly", "월", "월간 한도"), (meter.Key, meter.ShortLabel, meter.LongLabel));
        Assert.Equal(11, meter.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795748400), meter.ResetsAt);
    }

    [Fact]
    public void ParseWham_PrimaryIsWeekly_IsLabeledByLength()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("codex/wham-primary-weekly.json"), Now);

        Assert.Equal(new[] { "5h", "weekly" }, result.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(12, result.Meters[0].UsedPercent);
        Assert.Equal(FiveHourReset, result.Meters[0].ResetsAt);
        Assert.Equal(61, result.Meters[1].UsedPercent);
        Assert.Equal(WeeklyReset, result.Meters[1].ResetsAt);
    }

    [Fact]
    public void ParseWham_UsesResetAfterSeconds_WhenResetAtMissing()
    {
        const string json = """
            {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":"7.5","limit_window_seconds":18000,"reset_after_seconds":600}}}
            """;

        var meter = Assert.Single(CodexUsageParser.ParseWham(json, Now).Meters);

        Assert.Equal(7.5, meter.UsedPercent);
        Assert.Equal(Now.AddMinutes(10), meter.ResetsAt);
    }

    [Fact]
    public void ParseWham_NullRateLimit_ReturnsNoMeters()
    {
        var result = CodexUsageParser.ParseWham("""{"plan_type":"go","rate_limit":null,"additional_rate_limits":null}""", Now);

        Assert.Equal("go", result.PlanType);
        Assert.Empty(result.Meters);
    }

    [Theory]
    [InlineData("<html><body>Just a moment...</body></html>")]
    [InlineData("[]")]
    [InlineData("")]
    public void ParseWham_Throws_ForNonObjectBody(string body)
    {
        Assert.ThrowsAny<JsonException>(() => CodexUsageParser.ParseWham(body, Now));
    }

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("plus", "Plus")]
    [InlineData("team", "Team")]
    [InlineData("free", "Free")]
    [InlineData("self_serve_business_usage_based", "Self_serve_business_usage_based")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FormatPlan_CapitalizesRawPlanType(string? planType, string? expected)
    {
        Assert.Equal(expected, CodexUsageParser.FormatPlan(planType));
    }
}
