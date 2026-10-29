using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>One rate-limit window from any Codex source (wham, app-server, rollout).</summary>
public sealed record CodexWindow(double? UsedPercent, long? WindowMinutes, DateTimeOffset? ResetsAt);

public sealed record CodexWindowLabel(string Key, string ShortLabel, string LongLabel);

/// <summary>
/// Labels windows by their length (±5%), never by primary/secondary position,
/// and turns them into UsageMeters ordered from the shortest window.
/// </summary>
public static class CodexWindowLabeler
{
    public static CodexWindowLabel FiveHour => new("5h", "5h", Strings.CodexWindow5hLong);
    public static CodexWindowLabel Daily => new("daily", Strings.LabelDayShort, Strings.CodexWindowDailyLong);
    public static CodexWindowLabel Weekly => new("weekly", Strings.LabelWeekShort, Strings.CodexWindowWeeklyLong);
    public static CodexWindowLabel Monthly => new("monthly", Strings.LabelMonthShort, Strings.CodexWindowMonthlyLong);
    public static CodexWindowLabel Annual => new("annual", Strings.LabelYearShort, Strings.CodexWindowAnnualLong);
    public static CodexWindowLabel Primary => new("primary", Strings.LabelPrimaryShort, Strings.CodexWindowPrimaryLong);
    public static CodexWindowLabel Secondary => new("secondary", Strings.LabelSecondaryShort, Strings.CodexWindowSecondaryLong);

    private static (long Minutes, CodexWindowLabel Label)[] Known() =>
    [
        (300, FiveHour),
        (1440, Daily),
        (10080, Weekly),
        (43200, Monthly),
        (525600, Annual),
    ];

    /// <summary>wham/usage limit_window_seconds to minutes: (s + 59) / 60; s ≤ 0 → null.</summary>
    public static long? MinutesFromSeconds(double? seconds)
    {
        if (seconds is not double value || value <= 0) return null;
        var whole = (long)Math.Ceiling(value);
        return (whole + 59) / 60;
    }

    public static CodexWindowLabel Label(long? minutes, bool isSecondary)
    {
        if (minutes is long m && m > 0)
        {
            foreach (var (expected, label) in Known())
            {
                // m within ±5% of expected, in integer math: 0.95e ≤ m ≤ 1.05e
                if (m * 100 >= expected * 95 && m * 100 <= expected * 105)
                    return label;
            }
        }
        return isSecondary ? Secondary : Primary;
    }

    /// <summary>
    /// Builds meters for a primary/secondary pair. Windows without UsedPercent are skipped.
    /// With <paramref name="featureKey"/> (additional limits) keys become "&lt;feature&gt;:&lt;key&gt;".
    /// </summary>
    public static IReadOnlyList<UsageMeter> ToMeters(
        CodexWindow? primary, CodexWindow? secondary, string? featureKey = null, string? featureName = null)
    {
        var entries = new List<(long SortMinutes, int Index, UsageMeter Meter)>(2);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        AddWindow(primary, isSecondary: false, index: 0);
        AddWindow(secondary, isSecondary: true, index: 1);
        return entries.OrderBy(e => e.SortMinutes).ThenBy(e => e.Index).Select(e => e.Meter).ToList();

        void AddWindow(CodexWindow? window, bool isSecondary, int index)
        {
            if (window?.UsedPercent is not double used) return;
            var label = Label(window.WindowMinutes, isSecondary);
            if (!usedKeys.Add(label.Key))
            {
                label = isSecondary ? Secondary : Primary;
                if (!usedKeys.Add(label.Key)) return;
            }

            var meter = featureKey is null
                ? new UsageMeter(label.Key, label.ShortLabel, label.LongLabel, used, window.ResetsAt)
                : new UsageMeter(
                    $"{featureKey}:{label.Key}",
                    $"{label.ShortLabel}·{Abbreviate(featureName ?? featureKey)}",
                    $"{featureName ?? featureKey} · {label.LongLabel}",
                    used,
                    window.ResetsAt);
            entries.Add((window.WindowMinutes ?? long.MaxValue, index, meter));
        }
    }

    /// <summary>"GPT-5.3-Codex-Spark" → "Sp": first two letters of the last word that has a letter.</summary>
    public static string Abbreviate(string name)
    {
        var words = name.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = words.Length - 1; i >= 0; i--)
        {
            if (words[i].Any(char.IsLetter))
                return words[i].Length <= 2 ? words[i] : words[i][..2];
        }
        return name.Length <= 2 ? name : name[..2];
    }
}
