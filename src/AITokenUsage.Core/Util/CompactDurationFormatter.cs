namespace AITokenUsage.Core.Util;

/// <summary>Widget-sized time-to-reset text, the same in every language (v1.1 spec §2.2). Partial units are dropped.</summary>
public static class CompactDurationFormatter
{
    public static string Format(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } at)
        {
            return "";
        }

        long totalMinutes = (long)Math.Floor((at - now).TotalMinutes);
        if (totalMinutes < 1)
        {
            return "<1m";
        }

        long days = totalMinutes / (24 * 60);
        long hours = totalMinutes / 60 % 24;
        long minutes = totalMinutes % 60;

        if (days >= 10)
        {
            return $"{days}d";
        }

        if (days > 0)
        {
            return hours > 0 ? $"{days}d{hours}h" : $"{days}d";
        }

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h{minutes}m" : $"{hours}h";
        }

        return $"{minutes}m";
    }
}
