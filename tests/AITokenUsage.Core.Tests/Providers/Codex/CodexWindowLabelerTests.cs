using AITokenUsage.Core.Providers.Codex;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexWindowLabelerTests
{
    [Theory]
    [InlineData(299L, false, "5h", "5h", "5시간 한도")]
    [InlineData(300L, true, "5h", "5h", "5시간 한도")]
    [InlineData(285L, false, "5h", "5h", "5시간 한도")]     // 300 × 0.95
    [InlineData(315L, false, "5h", "5h", "5시간 한도")]     // 300 × 1.05
    [InlineData(284L, false, "primary", "한도", "사용 한도")]
    [InlineData(316L, true, "secondary", "보조", "보조 한도")]
    [InlineData(1440L, false, "daily", "일", "일간 한도")]
    [InlineData(10079L, true, "weekly", "주", "주간 한도")]
    [InlineData(10080L, false, "weekly", "주", "주간 한도")]
    [InlineData(43200L, false, "monthly", "월", "월간 한도")]
    [InlineData(525600L, false, "annual", "년", "연간 한도")]
    [InlineData(720L, false, "primary", "한도", "사용 한도")]
    [InlineData(720L, true, "secondary", "보조", "보조 한도")]
    [InlineData(0L, false, "primary", "한도", "사용 한도")]
    [InlineData(null, true, "secondary", "보조", "보조 한도")]
    public void Label_UsesWindowLengthWithFivePercentTolerance(long? minutes, bool isSecondary, string key, string shortLabel, string longLabel)
    {
        var label = CodexWindowLabeler.Label(minutes, isSecondary);

        Assert.Equal(new CodexWindowLabel(key, shortLabel, longLabel), label);
    }

    [Theory]
    [InlineData(18000d, 300L)]
    [InlineData(604800d, 10080L)]
    [InlineData(2592000d, 43200L)]
    [InlineData(61d, 2L)]
    [InlineData(0d, null)]
    [InlineData(-5d, null)]
    [InlineData(null, null)]
    public void MinutesFromSeconds_RoundsUp(double? seconds, long? expected)
    {
        Assert.Equal(expected, CodexWindowLabeler.MinutesFromSeconds(seconds));
    }

    [Fact]
    public void ToMeters_OrdersByWindowLength_NotByPosition()
    {
        var weeklyInPrimary = new CodexWindow(61, 10080, null);
        var fiveHourInSecondary = new CodexWindow(12, 300, null);

        var meters = CodexWindowLabeler.ToMeters(weeklyInPrimary, fiveHourInSecondary);

        Assert.Equal(new[] { "5h", "weekly" }, meters.Select(m => m.Key).ToArray());
        Assert.Equal(12, meters[0].UsedPercent);
        Assert.Equal(61, meters[1].UsedPercent);
    }

    [Fact]
    public void ToMeters_SingleMonthlyWindow_IsShownAlone()
    {
        var meters = CodexWindowLabeler.ToMeters(new CodexWindow(11, 43200, null), null);

        var meter = Assert.Single(meters);
        Assert.Equal("monthly", meter.Key);
        Assert.Equal("월", meter.ShortLabel);
    }

    [Fact]
    public void ToMeters_SkipsWindowsWithoutUsedPercent()
    {
        var meters = CodexWindowLabeler.ToMeters(new CodexWindow(null, 300, null), new CodexWindow(7, 10080, null));

        Assert.Equal("weekly", Assert.Single(meters).Key);
    }

    [Fact]
    public void ToMeters_DuplicateLengths_GetPositionalKey()
    {
        var meters = CodexWindowLabeler.ToMeters(new CodexWindow(1, 10080, null), new CodexWindow(2, 10080, null));

        Assert.Equal(new[] { "weekly", "secondary" }, meters.Select(m => m.Key).ToArray());
    }

    [Fact]
    public void ToMeters_FeaturePrefix_BuildsAdditionalLimitKeysAndLabels()
    {
        var reset = new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero);

        var meters = CodexWindowLabeler.ToMeters(
            new CodexWindow(30, 300, reset), new CodexWindow(100, 10080, null), "gpt_5_3_codex_spark", "GPT-5.3-Codex-Spark");

        Assert.Equal(2, meters.Count);
        Assert.Equal("gpt_5_3_codex_spark:5h", meters[0].Key);
        Assert.Equal("5h·Sp", meters[0].ShortLabel);
        Assert.Equal("GPT-5.3-Codex-Spark · 5시간 한도", meters[0].LongLabel);
        Assert.Equal(reset, meters[0].ResetsAt);
        Assert.Equal("gpt_5_3_codex_spark:weekly", meters[1].Key);
        Assert.Equal("주·Sp", meters[1].ShortLabel);
        Assert.Equal(100, meters[1].UsedPercent);
    }

    [Theory]
    [InlineData("GPT-5.3-Codex-Spark", "Sp")]
    [InlineData("gpt_5_3_codex_spark", "sp")]
    [InlineData("o3", "o3")]
    [InlineData("5", "5")]
    public void Abbreviate_TakesLastWordWithLetters(string name, string expected)
    {
        Assert.Equal(expected, CodexWindowLabeler.Abbreviate(name));
    }
}
