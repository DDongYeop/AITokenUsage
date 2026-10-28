using System.Globalization;

namespace AITokenUsage.Core.Util;

/// <summary>Korean reset/age texts. All wall-clock parts use the injected time zone (TimeZoneInfo.Local in the app).</summary>
public sealed class ResetTimeFormatter
{
    private static readonly string[] KoreanDayNames = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly TimeZoneInfo _zone;

    public ResetTimeFormatter(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _zone = zone;
    }

    /// <summary>
    /// "3시간 12분 후 · 오늘 15:50", "1일 8시간 후 · 10/30(금) 05:00", or "곧 리셋" when less than a minute is left
    /// (including resets in the past). Partial minutes are dropped.
    /// </summary>
    public string FormatRelative(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        TimeSpan remaining = resetsAt - now;
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "곧 리셋";
        }

        DateTimeOffset localReset = TimeZoneInfo.ConvertTime(resetsAt, _zone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, _zone);
        string clock = localReset.ToString("HH:mm", CultureInfo.InvariantCulture);
        string day = localReset.Date == localNow.Date ? "오늘" : FormatDayAndWeekday(localReset);

        return $"{FormatDuration(remaining)} 후 · {day} {clock}";
    }

    /// <summary>"11/1".</summary>
    public string FormatShortDate(DateTimeOffset at)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(at, _zone);
        return string.Create(CultureInfo.InvariantCulture, $"{local.Month}/{local.Day}");
    }

    /// <summary>"11/1(일)".</summary>
    public string FormatShortDateWithDay(DateTimeOffset at) =>
        FormatDayAndWeekday(TimeZoneInfo.ConvertTime(at, _zone));

    /// <summary>"방금", "3분 전", "2시간 전", "3일 전". Future times (clock skew) give "방금".</summary>
    public string FormatAgo(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan elapsed = now - at;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "방금";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}분 전";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}시간 전";
        }

        return $"{(int)elapsed.TotalDays}일 전";
    }

    private static string FormatDayAndWeekday(DateTimeOffset local) =>
        string.Create(CultureInfo.InvariantCulture, $"{local.Month}/{local.Day}({KoreanDayNames[(int)local.DayOfWeek]})");

    private static string FormatDuration(TimeSpan span)
    {
        long totalMinutes = (long)Math.Floor(span.TotalMinutes);
        long days = totalMinutes / (24 * 60);
        long hours = totalMinutes / 60 % 24;
        long minutes = totalMinutes % 60;

        if (days > 0)
        {
            return hours > 0 ? $"{days}일 {hours}시간" : $"{days}일";
        }

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}시간 {minutes}분" : $"{hours}시간";
        }

        return $"{minutes}분";
    }
}
