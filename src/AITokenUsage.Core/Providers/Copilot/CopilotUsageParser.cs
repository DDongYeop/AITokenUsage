using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>
/// copilot_internal/user → plan label + meters. Keys: "monthly" (primary), "reset" (text row "↻ M/d"),
/// "completions" (Free only). Each quota_snapshots entry is read on its own so one odd entry cannot fail the rest.
/// </summary>
public static class CopilotUsageParser
{
    public static readonly IReadOnlyList<MeterOption> MeterOptions =
    [
        new("monthly", "월간 사용량"),
        new("reset", "리셋 날짜"),
        new("completions", "코드 완성 (Free)"),
    ];

    private const string FreeSku = "free_limited_copilot";
    private const string StudentSku = "free_educational_quota";
    private const double MaxEpochSeconds = 253_402_300_799;

    /// <summary>Throws JsonException when the body is not a JSON object.</summary>
    public static CopilotUsage Parse(string json, DateTimeOffset now, TimeZoneInfo displayZone)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("copilot_internal/user response is not a JSON object.");
        }

        var isFree = Json.Str(root, "access_type_sku") == FreeSku;
        var tokenBased = Json.Bool(root, "token_based_billing") == true;
        var snapshots = Json.Obj(root, "quota_snapshots");
        var chat = Snapshot(snapshots, "chat");
        var premium = Snapshot(snapshots, "premium_interactions");
        var primary = isFree ? chat ?? premium : premium;
        var resetsAt = ResolveReset(root, primary, now);
        var planLabel = PlanLabel(root);

        var monthlyLabel = tokenBased ? "월간 크레딧" : isFree && chat is not null ? "월간 채팅" : "월간 프리미엄 요청";
        var monthly = primary is { } snapshot
            ? SnapshotMeter("monthly", "월", monthlyLabel, snapshot, resetsAt)
            : isFree ? LegacyMeter(root, "chat", "monthly", "월", "월간 채팅", resetsAt) : null;

        var meters = new List<UsageMeter>();
        if (monthly is not null)
        {
            meters.Add(monthly);
        }

        if (monthly is not null || planLabel is not null)
        {
            meters.Add(new UsageMeter("reset", "↻", "리셋", null, resetsAt, TextOverride: ResetText(resetsAt, displayZone)));
        }

        if (isFree)
        {
            var completions = Snapshot(snapshots, "completions") is { } c
                ? SnapshotMeter("completions", "완성", "코드 완성 (월)", c, resetsAt)
                : LegacyMeter(root, "completions", "completions", "완성", "코드 완성 (월)", resetsAt);
            if (completions is not null)
            {
                meters.Add(completions);
            }
        }

        return new CopilotUsage(Json.Str(root, "login"), planLabel, meters);
    }

    public static string? PlanLabel(JsonElement root)
    {
        switch (Json.Str(root, "access_type_sku"))
        {
            case FreeSku:
                return "Free";
            case StudentSku:
                return "Student";
        }

        return Json.Str(root, "copilot_plan") switch
        {
            "individual" => "Pro",
            "individual_pro" => "Pro+",
            "individual_max" => "Max",
            "individual_edu" => "Student",
            "business" => "Business",
            "enterprise" => "Enterprise",
            _ => null,
        };
    }

    /// <summary>
    /// snapshot.quota_reset_at (unix s) → quota_reset_date_utc → quota_reset_date → limited_user_reset_date
    /// (ISO first, else yyyy-MM-dd as UTC midnight) → next 1st of the month 00:00 UTC.
    /// </summary>
    public static DateTimeOffset ResolveReset(JsonElement root, JsonElement? primarySnapshot, DateTimeOffset now)
    {
        if (primarySnapshot is { } snapshot && Json.Num(snapshot, "quota_reset_at") is double seconds
            && seconds is > 0 and < MaxEpochSeconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        foreach (var name in new[] { "quota_reset_date_utc", "quota_reset_date", "limited_user_reset_date" })
        {
            if (ReadDate(root, name) is { } date)
            {
                return date;
            }
        }

        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    private static UsageMeter? SnapshotMeter(string key, string shortLabel, string longLabel, JsonElement snapshot, DateTimeOffset resetsAt)
    {
        var entitlement = Json.Num(snapshot, "entitlement");
        var creditsUsed = Json.Num(snapshot, "credits_used");
        var unlimited = Json.Bool(snapshot, "unlimited") == true || entitlement == -1;

        if (unlimited)
        {
            var text = Json.Bool(snapshot, "has_quota") == false ? "조직 한도 도달"
                : creditsUsed is { } pooled ? CreditsText(pooled)
                : "무제한";
            return new UsageMeter(key, shortLabel, longLabel, null, resetsAt, TextOverride: text);
        }

        if (entitlement == 0)
        {
            // Business token-based billing placeholder: never show "0% used"; show credits if present.
            return creditsUsed is { } used
                ? new UsageMeter(key, shortLabel, longLabel, null, resetsAt, TextOverride: CreditsText(used))
                : null;
        }

        var percentRemaining = Json.Num(snapshot, "percent_remaining");
        var usedPercent = percentRemaining is { } remaining
            ? 100 - Math.Clamp(remaining, 0, 100)
            : UsedFromCounts(snapshot, entitlement);
        return usedPercent is { } value
            ? new UsageMeter(key, shortLabel, longLabel, Math.Round(value, 2), resetsAt)
            : null;
    }

    private static double? UsedFromCounts(JsonElement snapshot, double? entitlement)
    {
        if (entitlement is not > 0)
        {
            return null;
        }

        var remaining = Json.Num(snapshot, "quota_remaining") ?? Json.Num(snapshot, "remaining");
        return remaining is { } left
            ? Math.Clamp((entitlement.Value - left) / entitlement.Value * 100, 0, 100)
            : null;
    }

    /// <summary>Legacy Free: monthly_quotas (allowance) and limited_user_quotas (remaining).</summary>
    private static UsageMeter? LegacyMeter(JsonElement root, string quotaName, string key, string shortLabel, string longLabel, DateTimeOffset resetsAt)
    {
        if (Json.Obj(root, "monthly_quotas") is not { } monthly || Json.Obj(root, "limited_user_quotas") is not { } remaining)
        {
            return null;
        }

        var total = Json.Num(monthly, quotaName);
        var left = Json.Num(remaining, quotaName);
        if (total is not > 0 || left is null)
        {
            return null;
        }

        var used = 100 - Math.Clamp(left.Value / total.Value * 100, 0, 100);
        return new UsageMeter(key, shortLabel, longLabel, Math.Round(used, 2), resetsAt);
    }

    private static JsonElement? Snapshot(JsonElement? snapshots, string name) =>
        snapshots is { } s ? Json.Obj(s, name) : null;

    private static DateTimeOffset? ReadDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds is > 0 and < MaxEpochSeconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }

        var text = value.GetString()!.Trim();
        const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, utc, out var iso))
        {
            return iso;
        }

        return DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, utc, out var day) ? day : null;
    }

    private static string CreditsText(double credits) =>
        $"{credits.ToString("0.##", CultureInfo.InvariantCulture)} 크레딧 사용";

    private static string ResetText(DateTimeOffset resetsAt, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(resetsAt, zone);
        return $"↻ {local.Month}/{local.Day}";
    }
}
