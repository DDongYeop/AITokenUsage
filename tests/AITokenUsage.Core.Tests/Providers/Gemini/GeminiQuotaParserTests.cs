using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiQuotaParserTests
{
    [Fact]
    public void Groups_buckets_and_keeps_minimum_fraction_per_group()
    {
        var meters = GeminiQuotaParser.Parse(Fixture.Read("gemini/quota-buckets.json"));

        Assert.Equal(["pro", "flash", "flash-lite"], meters.Select(m => m.Key).ToArray());

        var pro = meters[0];
        Assert.Equal("Pro", pro.ShortLabel);
        Assert.Equal("Pro 일간", pro.LongLabel);
        Assert.Equal(40, pro.UsedPercent!.Value, 6);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero), pro.ResetsAt);

        var flash = meters[1];
        Assert.Equal("Fl", flash.ShortLabel);
        Assert.Equal(10, flash.UsedPercent!.Value, 6);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 22, 30, 0, TimeSpan.Zero), flash.ResetsAt);

        var lite = meters[2];
        Assert.Equal("Lt", lite.ShortLabel);
        Assert.Equal(0, lite.UsedPercent!.Value, 6);
    }

    [Theory]
    [InlineData("gemini-2.5-flash-lite", "flash-lite")]
    [InlineData("gemini-3.1-flash-lite-preview", "flash-lite")]
    [InlineData("gemini-3-flash-preview", "flash")]
    [InlineData("GEMINI-3.8-FLASH", "flash")]
    [InlineData("gemini-3.1-pro-preview-customtools", "pro")]
    [InlineData("gemma-3-27b-it", null)]
    public void Classifies_by_substring_flash_lite_then_flash_then_pro(string modelId, string? expected)
    {
        Assert.Equal(expected, GeminiQuotaParser.Classify(modelId));
    }

    [Fact]
    public void Fraction_outside_zero_to_one_is_clamped()
    {
        var meters = GeminiQuotaParser.Parse(
            """{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":-0.2},{"modelId":"gemini-2.5-flash","remainingFraction":1.4}]}""");

        Assert.Equal(100, meters.Single(m => m.Key == "pro").UsedPercent!.Value, 6);
        Assert.Equal(0, meters.Single(m => m.Key == "flash").UsedPercent!.Value, 6);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"buckets":[]}""")]
    [InlineData("""{"buckets":null}""")]
    [InlineData("[]")]
    public void Missing_buckets_give_no_meters(string json)
    {
        Assert.Empty(GeminiQuotaParser.Parse(json));
    }

    [Fact]
    public void Static_meter_options_match_meter_keys()
    {
        Assert.Equal(["pro", "flash", "flash-lite"], GeminiQuotaParser.MeterOptions.Select(o => o.Key).ToArray());
    }
}
