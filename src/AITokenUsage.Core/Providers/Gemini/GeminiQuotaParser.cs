using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// retrieveUserQuota → meters. Buckets are grouped by modelId substring (flash-lite, then flash, then pro);
/// each group keeps its minimum remainingFraction; used % = (1 − f) × 100.
/// </summary>
public static class GeminiQuotaParser
{
    public static IReadOnlyList<MeterOption> MeterOptions =>
    [
        new("pro", Strings.GeminiProDaily),
        new("flash", Strings.GeminiFlashDaily),
        new("flash-lite", Strings.GeminiFlashLiteDaily),
    ];

    private static (string Key, string ShortLabel, string LongLabel)[] Groups() =>
    [
        ("pro", "Pro", Strings.GeminiProDaily),
        ("flash", "Fl", Strings.GeminiFlashDaily),
        ("flash-lite", "Lt", Strings.GeminiFlashLiteDaily),
    ];

    public static string? Classify(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("flash-lite"))
        {
            return "flash-lite";
        }

        if (id.Contains("flash"))
        {
            return "flash";
        }

        return id.Contains("pro") ? "pro" : null;
    }

    /// <summary>Throws JsonException when the body is not JSON. Malformed buckets are skipped one by one.</summary>
    public static IReadOnlyList<UsageMeter> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Json.Arr(root, "buckets") is not { } buckets)
        {
            return [];
        }

        var lowest = new Dictionary<string, (double Fraction, DateTimeOffset? ResetsAt)>(StringComparer.Ordinal);
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var modelId = Json.Str(bucket, "modelId");
            var fraction = Json.Num(bucket, "remainingFraction");
            if (string.IsNullOrWhiteSpace(modelId) || fraction is null || double.IsNaN(fraction.Value))
            {
                continue;
            }

            var group = Classify(modelId);
            if (group is null)
            {
                continue;
            }

            var clamped = Math.Clamp(fraction.Value, 0, 1);
            if (!lowest.TryGetValue(group, out var current) || clamped < current.Fraction)
            {
                lowest[group] = (clamped, Json.Iso(bucket, "resetTime"));
            }
        }

        var meters = new List<UsageMeter>();
        foreach (var (key, shortLabel, longLabel) in Groups())
        {
            if (lowest.TryGetValue(key, out var value))
            {
                var used = Math.Round((1 - value.Fraction) * 100, 2);
                meters.Add(new UsageMeter(key, shortLabel, longLabel, used, value.ResetsAt));
            }
        }

        return meters;
    }
}
