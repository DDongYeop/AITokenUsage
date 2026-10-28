using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

public sealed record CodexUsageResult(string? PlanType, IReadOnlyList<UsageMeter> Meters);

/// <summary>Parses GET {base}/wham/usage. Unknown fields are ignored; a broken item only drops itself.</summary>
public static class CodexUsageParser
{
    /// <exception cref="JsonException">The body is not a JSON object (e.g. an HTML challenge page).</exception>
    public static CodexUsageResult ParseWham(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("wham/usage body is not a JSON object.");

        var meters = new List<UsageMeter>();
        if (Json.Obj(root, "rate_limit") is { } main)
            meters.AddRange(CodexWindowLabeler.ToMeters(Window(main, "primary_window", now), Window(main, "secondary_window", now)));

        if (Json.Arr(root, "additional_rate_limits") is { } additional)
        {
            foreach (var item in additional.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var feature = NonEmpty(Json.Str(item, "metered_feature"));
                if (feature is null || string.Equals(feature, "codex", StringComparison.OrdinalIgnoreCase)) continue;
                if (Json.Obj(item, "rate_limit") is not { } limit) continue;
                var name = NonEmpty(Json.Str(item, "limit_name")) ?? feature;
                meters.AddRange(CodexWindowLabeler.ToMeters(
                    Window(limit, "primary_window", now), Window(limit, "secondary_window", now), feature, name));
            }
        }

        return new CodexUsageResult(NonEmpty(Json.Str(root, "plan_type")), meters);
    }

    /// <summary>Raw plan_type with the first letter upper-cased ("pro" → "Pro"). Empty or "unknown" → null.</summary>
    public static string? FormatPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType)) return null;
        var trimmed = planType.Trim();
        if (trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static CodexWindow? Window(JsonElement parent, string name, DateTimeOffset now)
    {
        if (Json.Obj(parent, name) is not { } window) return null;
        var used = Json.Num(window, "used_percent");
        if (used is null) return null;

        var minutes = CodexWindowLabeler.MinutesFromSeconds(Json.Num(window, "limit_window_seconds"));
        // Out-of-range values (e.g. a millisecond epoch) degrade to "no reset time"; the meter is kept.
        DateTimeOffset? resetsAt = Json.Num(window, "reset_at") is > 0 ? Json.EpochSeconds(window, "reset_at") : null;
        if (resetsAt is null && Json.Num(window, "reset_after_seconds") is double after && after > 0)
            resetsAt = Json.AddSecondsOrNull(now, after);
        return new CodexWindow(used, minutes, resetsAt);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
