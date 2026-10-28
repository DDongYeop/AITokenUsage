using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

public class MeterFormatterTests
{
    [Theory]
    [InlineData(14.0, "14%")]
    [InlineData(14.5, "15%")]
    [InlineData(0.4, "0%")]
    [InlineData(99.6, "100%")]
    [InlineData(130.0, "100%")]
    [InlineData(-5.0, "0%")]
    public void ValueText_UsedMode_RoundsAndClamps(double used, string expected)
    {
        Assert.Equal(expected, MeterFormatter.ValueText(Meter("session", "5h", used), ValueMode.Used));
    }

    [Fact]
    public void RemainingMode_ShowsHundredMinusUsed_ForTextAndBar()
    {
        var meter = Meter("session", "5h", 14);

        Assert.Equal("86%", MeterFormatter.ValueText(meter, ValueMode.Remaining));
        Assert.Equal(0.86, MeterFormatter.BarFraction(meter, ValueMode.Remaining)!.Value, 6);
    }

    [Fact]
    public void BarFraction_UsedMode_IsUsedPercentOverHundred()
    {
        Assert.Equal(0.14, MeterFormatter.BarFraction(Meter("session", "5h", 14), ValueMode.Used)!.Value, 6);
    }

    [Fact]
    public void NaNPercent_IsTreatedAsUnknown()
    {
        var meter = Meter("session", "5h", double.NaN);

        Assert.Equal(MeterFormatter.MissingValue, MeterFormatter.ValueText(meter, ValueMode.Used));
        Assert.Null(MeterFormatter.BarFraction(meter, ValueMode.Used));
    }

    [Fact]
    public void UnknownPercent_ShowsDashAndNoBar()
    {
        var meter = Meter("session", "5h", null);

        Assert.Equal("—", MeterFormatter.ValueText(meter, ValueMode.Remaining));
        Assert.Null(MeterFormatter.BarFraction(meter, ValueMode.Remaining));
        Assert.Null(MeterFormatter.DisplayPercent(meter, ValueMode.Used));
    }

    [Fact]
    public void TextOverride_WinsOverPercent_AndHasNoBar()
    {
        var meter = Meter("monthly", "월", 40, text: "12 크레딧 사용");

        Assert.Equal("12 크레딧 사용", MeterFormatter.ValueText(meter, ValueMode.Used));
        Assert.Null(MeterFormatter.BarFraction(meter, ValueMode.Used));
        Assert.Null(MeterFormatter.DisplayPercent(meter, ValueMode.Remaining));
    }
}
