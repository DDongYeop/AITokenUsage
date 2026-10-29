using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class CompactDurationFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-120, "<1m")]
    [InlineData(0, "<1m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(59 * 60 + 59, "59m")]
    [InlineData(3600, "1h")]
    [InlineData(4 * 3600 + 12 * 60 + 30, "4h12m")]
    [InlineData(23 * 3600 + 59 * 60, "23h59m")]
    [InlineData(24 * 3600, "1d")]
    [InlineData(2 * 86400 + 3 * 3600 + 59 * 60, "2d3h")]
    [InlineData(9 * 86400 + 23 * 3600, "9d23h")]
    [InlineData(10 * 86400, "10d")]
    [InlineData(12 * 86400 + 5 * 3600, "12d")]
    public void Formats_remaining_time(int seconds, string expected) =>
        Assert.Equal(expected, CompactDurationFormatter.Format(Now.AddSeconds(seconds), Now));

    [Fact]
    public void Unknown_reset_is_empty() => Assert.Equal("", CompactDurationFormatter.Format(null, Now));

    [Fact]
    [Localization.UseCulture("en-US")]
    public void Same_output_in_english() =>
        Assert.Equal("2d3h", CompactDurationFormatter.Format(Now.AddHours(51), Now));
}
