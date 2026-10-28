using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class ResetTimeFormatterTests
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    private static readonly TimeZoneInfo KstZone =
        TimeZoneInfo.CreateCustomTimeZone("KST", Kst, "Korea Standard Time", "Korea Standard Time");

    private readonly ResetTimeFormatter _formatter = new(KstZone);

    private static DateTimeOffset At(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, Kst);

    [Fact]
    public void SameDay_ShowsHoursMinutesAndToday()
    {
        Assert.Equal("3시간 12분 후 · 오늘 15:50", _formatter.FormatRelative(At(10, 28, 15, 50), At(10, 28, 12, 38)));
    }

    [Fact]
    public void LaterDay_ShowsDaysHoursAndDateWithKoreanWeekday()
    {
        // 2026-10-30 is a Friday.
        Assert.Equal("1일 8시간 후 · 10/30(금) 05:00", _formatter.FormatRelative(At(10, 30, 5, 0), At(10, 28, 21, 0)));
    }

    [Fact]
    public void CrossingMidnight_ShowsTheNextDate()
    {
        Assert.Equal("1시간 45분 후 · 10/29(목) 01:15", _formatter.FormatRelative(At(10, 29, 1, 15), At(10, 28, 23, 30)));
    }

    [Fact]
    public void UtcInputs_AreConvertedToTheConfiguredZone()
    {
        var resetUtc = new DateTimeOffset(2026, 10, 28, 6, 50, 0, TimeSpan.Zero);  // 15:50 KST
        var nowUtc = new DateTimeOffset(2026, 10, 28, 3, 38, 0, TimeSpan.Zero);    // 12:38 KST

        Assert.Equal("3시간 12분 후 · 오늘 15:50", _formatter.FormatRelative(resetUtc, nowUtc));
    }

    [Fact]
    public void TheZoneDecidesWhatTodayMeans()
    {
        var reset = new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

        Assert.Equal("2시간 후 · 오늘 16:00", new ResetTimeFormatter(TimeZoneInfo.Utc).FormatRelative(reset, now));
        Assert.Equal("2시간 후 · 10/29(목) 01:00", _formatter.FormatRelative(reset, now));
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59)]
    public void PastOrUnderOneMinute_IsResettingSoon(int secondsAhead)
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal("곧 리셋", _formatter.FormatRelative(now.AddSeconds(secondsAhead), now));
    }

    [Theory]
    [InlineData(60, "1분 후 · 오늘 12:01")]
    [InlineData(45 * 60 + 59, "45분 후 · 오늘 12:45")]
    [InlineData(2 * 3600, "2시간 후 · 오늘 14:00")]
    [InlineData(3 * 3600 + 12 * 60 + 59, "3시간 12분 후 · 오늘 15:12")]
    [InlineData(2 * 86400, "2일 후 · 10/30(금) 12:00")]
    [InlineData(2 * 86400 + 3600 + 1800, "2일 1시간 후 · 10/30(금) 13:30")]
    public void Durations_DropPartialUnits(int secondsAhead, string expected)
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal(expected, _formatter.FormatRelative(now.AddSeconds(secondsAhead), now));
    }

    [Theory]
    [InlineData(1, "11/1(일)")]
    [InlineData(2, "11/2(월)")]
    [InlineData(3, "11/3(화)")]
    [InlineData(4, "11/4(수)")]
    [InlineData(5, "11/5(목)")]
    [InlineData(6, "11/6(금)")]
    [InlineData(7, "11/7(토)")]
    public void FormatShortDateWithDay_UsesKoreanWeekdays(int day, string expected)
    {
        Assert.Equal(expected, _formatter.FormatShortDateWithDay(At(11, day, 9, 0)));
    }

    [Fact]
    public void FormatShortDate_IsMonthSlashDayInZone()
    {
        Assert.Equal("11/1", _formatter.FormatShortDate(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
        // 2026-10-31 16:00 UTC is already 11/1 01:00 in Korea.
        Assert.Equal("11/1", _formatter.FormatShortDate(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)));
        Assert.Equal("10/31", new ResetTimeFormatter(TimeZoneInfo.Utc).FormatShortDate(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(-120, "방금")]
    [InlineData(0, "방금")]
    [InlineData(59, "방금")]
    [InlineData(60, "1분 전")]
    [InlineData(3 * 60 + 30, "3분 전")]
    [InlineData(59 * 60 + 59, "59분 전")]
    [InlineData(3600, "1시간 전")]
    [InlineData(2 * 3600 + 59 * 60, "2시간 전")]
    [InlineData(86400, "1일 전")]
    [InlineData(3 * 86400 + 5, "3일 전")]
    public void FormatAgo_PicksTheLargestWholeUnit(int secondsAgo, string expected)
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal(expected, _formatter.FormatAgo(now.AddSeconds(-secondsAgo), now));
    }
}
