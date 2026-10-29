using System.Globalization;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Util;

/// <summary>
/// Localized reset/age texts (Strings resources, weekday names and clock format of the current culture).
/// All wall-clock parts use the injected time zone (TimeZoneInfo.Local in the app).
/// </summary>
public sealed class ResetTimeFormatter
{
    private readonly TimeZoneInfo _zone;

    public ResetTimeFormatter(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _zone = zone;
    }

    /// <summary>
    /// ko: "3시간 12분 후 · 오늘 15:50", "1일 8시간 후 · 10/30(금) 05:00", "곧 리셋";
    /// en: "in 3h 12m · Today 3:50 PM", "in 1d 8h · Fri 10/30 5:00 AM", "Resets soon".
    /// The soon text is used when less than a minute is left (including resets in the past). Partial minutes are dropped.
    /// </summary>
    public string FormatRelative(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        TimeSpan remaining = resetsAt - now;
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return Strings.ResetSoon;
        }

        DateTimeOffset localReset = TimeZoneInfo.ConvertTime(resetsAt, _zone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, _zone);
        string clock = FormatClock(localReset);
        string day = localReset.Date == localNow.Date ? Strings.ResetToday : FormatDayAndWeekday(localReset);

        return string.Format(CultureInfo.CurrentCulture, Strings.ResetRelativeFormat, FormatDuration(remaining), day, clock);
    }

    /// <summary>"11/1".</summary>
    public string FormatShortDate(DateTimeOffset at)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(at, _zone);
        return string.Create(CultureInfo.InvariantCulture, $"{local.Month}/{local.Day}");
    }

    /// <summary>ko "11/1(일)", en "Sun 11/1".</summary>
    public string FormatShortDateWithDay(DateTimeOffset at) =>
        FormatDayAndWeekday(TimeZoneInfo.ConvertTime(at, _zone));

    /// <summary>
    /// ko "방금", "3분 전", "2시간 전", "3일 전"; en "just now", "3m ago", "2h ago", "3d ago".
    /// Future times (clock skew) give the "just now" text.
    /// </summary>
    public string FormatAgo(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan elapsed = now - at;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Strings.AgoJustNow;
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Format(Strings.AgoMinutes, (int)elapsed.TotalMinutes);
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return Format(Strings.AgoHours, (int)elapsed.TotalHours);
        }

        return Format(Strings.AgoDays, (int)elapsed.TotalDays);
    }

    /// <summary>Clock time in the current culture's style (ko "15:50", en "3:50 PM"); the caller converts to the zone first.</summary>
    internal static string FormatClock(DateTimeOffset local) =>
        local.ToString(Strings.TimeFormat, CultureInfo.CurrentCulture);

    private static string FormatDayAndWeekday(DateTimeOffset local) =>
        string.Format(
            CultureInfo.InvariantCulture,
            Strings.DateWithDayFormat,
            local.Month,
            local.Day,
            CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(int)local.DayOfWeek]);

    private static string FormatDuration(TimeSpan span)
    {
        long totalMinutes = (long)Math.Floor(span.TotalMinutes);
        long days = totalMinutes / (24 * 60);
        long hours = totalMinutes / 60 % 24;
        long minutes = totalMinutes % 60;

        if (days > 0)
        {
            return hours > 0 ? Format(Strings.DurationDaysHours, days, hours) : Format(Strings.DurationDays, days);
        }

        if (hours > 0)
        {
            return minutes > 0 ? Format(Strings.DurationHoursMinutes, hours, minutes) : Format(Strings.DurationHours, hours);
        }

        return Format(Strings.DurationMinutes, minutes);
    }

    private static string Format(string format, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);
}
