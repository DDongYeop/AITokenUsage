using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>Meters and the optional extra-usage line parsed from GET /api/oauth/usage.</summary>
public sealed record ClaudeUsageData(IReadOnlyList<UsageMeter> Meters, string? ExtraLine);

/// <summary>
/// Lenient parser for the Claude OAuth usage body (docs/research/claude-usage-api.md §2).
/// limits[] wins, legacy window keys are the fallback, and each limits[] row is parsed in isolation.
/// </summary>
public static partial class ClaudeUsageParser
{
    public const string SessionKey = "session";
    public const string WeeklyKey = "weekly";
    public const string ScopedKeyPrefix = "weekly:";
    public const string SessionShortLabel = "5h";
    public const string SessionLongLabel = "현재 세션 (5시간)";
    public const string WeeklyShortLabel = "주";
    public const string WeeklyLongLabel = "주간 · 모든 모델";

    private const string ScopedShortPrefix = "주·";
    private const string ScopedLongPrefix = "주간 · ";

    private sealed record LimitRow(string Kind, double Percent, DateTimeOffset? ResetsAt, Severity? Severity, string? ScopeName);

    private sealed record Window(double? Utilization, DateTimeOffset? ResetsAt);

    /// <exception cref="JsonException">The body is not JSON or its root is not an object.</exception>
    public static ClaudeUsageData Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Usage body root is not a JSON object.");
        }

        List<LimitRow> rows = ReadLimitRows(root);
        var meters = new List<UsageMeter>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        AddHeadline(meters, usedKeys, rows, root, "session", "five_hour", SessionKey, SessionShortLabel, SessionLongLabel);
        AddHeadline(meters, usedKeys, rows, root, "weekly_all", "seven_day", WeeklyKey, WeeklyShortLabel, WeeklyLongLabel);

        foreach (LimitRow row in rows)
        {
            if (row.Kind.Equals("weekly_scoped", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.ScopeName))
            {
                AddScoped(meters, usedKeys, row.ScopeName.Trim(), row.Percent, row.ResetsAt, row.Severity);
            }
        }

        if (ReadWindow(root, "seven_day_sonnet") is { } sonnet)
        {
            AddScoped(meters, usedKeys, "Sonnet", sonnet.Utilization, sonnet.ResetsAt, null);
        }

        if (ReadWindow(root, "seven_day_opus") is { } opus)
        {
            AddScoped(meters, usedKeys, "Opus", opus.Utilization, opus.ResetsAt, null);
        }

        return new ClaudeUsageData(meters, ReadExtraLine(root));
    }

    /// <summary>"team" + "default_claude_max_5x" → "Team · Max 5x"; "max" + "..._max_20x" → "Max 20x".</summary>
    public static string? FormatPlanLabel(string? subscriptionType, string? rateLimitTier)
    {
        string? plan = string.IsNullOrWhiteSpace(subscriptionType) ? null : Capitalize(subscriptionType.Trim());

        string? multiplier = null;
        if (!string.IsNullOrEmpty(rateLimitTier) && MaxTierRegex().Match(rateLimitTier) is { Success: true } match)
        {
            multiplier = "Max " + match.Groups[1].Value.ToLowerInvariant();
        }

        if (plan is null)
        {
            return multiplier;
        }

        if (multiplier is null)
        {
            return plan;
        }

        return plan.Equals("Max", StringComparison.OrdinalIgnoreCase) ? multiplier : plan + " · " + multiplier;
    }

    /// <summary>True for Anthropic-format errors: {"error":{"type":"..."}}.</summary>
    public static bool IsAnthropicErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Json.Obj(root, "error") is { } error
                && !string.IsNullOrEmpty(Json.Str(error, "type"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Severity? ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "normal" => Severity.Normal,
        "warning" => Severity.Warning,
        "critical" => Severity.Critical,
        _ => null,
    };

    /// <summary>resets_at jitters by about 1 s between calls, so round to the nearest minute (UTC).</summary>
    public static DateTimeOffset RoundToMinute(DateTimeOffset value)
    {
        long utcTicks = value.UtcTicks;
        if (utcTicks > DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerMinute)
        {
            return value.ToUniversalTime();
        }

        long minutes = (utcTicks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute;
        return new DateTimeOffset(minutes * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    public static string Slug(string displayName) =>
        WhitespaceRegex().Replace(displayName.Trim().ToLowerInvariant(), "-");

    private static void AddHeadline(
        List<UsageMeter> meters,
        HashSet<string> usedKeys,
        List<LimitRow> rows,
        JsonElement root,
        string kind,
        string legacyWindowName,
        string key,
        string shortLabel,
        string longLabel)
    {
        LimitRow? row = rows.Find(r => r.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            meters.Add(new UsageMeter(key, shortLabel, longLabel, row.Percent, row.ResetsAt, row.Severity));
            usedKeys.Add(key);
        }
        else if (ReadWindow(root, legacyWindowName) is { } window)
        {
            meters.Add(new UsageMeter(key, shortLabel, longLabel, window.Utilization, window.ResetsAt));
            usedKeys.Add(key);
        }
    }

    private static void AddScoped(
        List<UsageMeter> meters,
        HashSet<string> usedKeys,
        string name,
        double? percent,
        DateTimeOffset? resetsAt,
        Severity? severity)
    {
        string key = ScopedKeyPrefix + Slug(name);
        if (!usedKeys.Add(key))
        {
            return; // the first row with a given slug wins
        }

        meters.Add(new UsageMeter(
            key,
            ScopedShortPrefix + FirstTwoTextElements(name),
            ScopedLongPrefix + name,
            percent,
            resetsAt,
            severity));
    }

    private static List<LimitRow> ReadLimitRows(JsonElement root)
    {
        var rows = new List<LimitRow>();
        if (Json.Arr(root, "limits") is not { } limits)
        {
            return rows; // null: the server predates limits[]
        }

        foreach (JsonElement entry in limits.EnumerateArray())
        {
            try
            {
                if (TryReadLimitRow(entry) is { } row)
                {
                    rows.Add(row);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or JsonException)
            {
                // One malformed row must not drop its siblings.
            }
        }

        return rows;
    }

    private static LimitRow? TryReadLimitRow(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? kind = Json.Str(entry, "kind");
        double? percent = NumberOnly(entry, "percent");
        if (string.IsNullOrWhiteSpace(kind) || percent is null)
        {
            return null;
        }

        return new LimitRow(
            kind,
            percent.Value,
            ReadResetsAt(entry),
            ParseSeverity(Json.Str(entry, "severity")),
            ReadScopeName(entry));
    }

    private static string? ReadScopeName(JsonElement entry)
    {
        if (Json.Obj(entry, "scope") is not { } scope)
        {
            return null;
        }

        string? modelName = Json.Obj(scope, "model") is { } model ? Json.Str(model, "display_name") : null;
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            return modelName;
        }

        return Json.Obj(scope, "surface") is { } surface ? Json.Str(surface, "display_name") : null;
    }

    private static Window? ReadWindow(JsonElement root, string name) =>
        Json.Obj(root, name) is { } window
            ? new Window(NumberOnly(window, "utilization"), ReadResetsAt(window))
            : null;

    /// <summary>Only JSON numbers count as percentages (strings and booleans are ignored).</summary>
    private static double? NumberOnly(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
        && double.IsFinite(number)
            ? number
            : null;

    private static DateTimeOffset? ReadResetsAt(JsonElement obj)
    {
        string? text = Json.Str(obj, "resets_at");
        return text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
                ? RoundToMinute(value)
                : null;
    }

    /// <summary>extra_usage amounts are minor units of currency; divide by 10^decimal_places (default 100).</summary>
    private static string? ReadExtraLine(JsonElement root)
    {
        if (Json.Obj(root, "extra_usage") is not { } extra || Json.Bool(extra, "is_enabled") != true)
        {
            return null;
        }

        int decimals = 2;
        if (NumberOnly(extra, "decimal_places") is { } places && places >= 0 && places <= 6 && places == Math.Floor(places))
        {
            decimals = (int)places;
        }

        double divisor = Math.Pow(10, decimals);
        string? rawCurrency = Json.Str(extra, "currency");
        string currency = string.IsNullOrWhiteSpace(rawCurrency) ? "USD" : rawCurrency.Trim().ToUpperInvariant();

        double used = (NumberOnly(extra, "used_credits") ?? 0) / divisor;
        string line = "추가 사용 " + Money(used, currency, decimals);

        if (NumberOnly(extra, "monthly_limit") is { } limitMinor)
        {
            line += " / " + Money(limitMinor / divisor, currency, decimals);
        }

        if (Json.Bool(extra, "spend_limit_reached") == true)
        {
            line += " · 한도 도달";
        }

        return line;
    }

    private static string Money(double amount, string currency, int decimals)
    {
        string number = amount.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return currency == "USD" ? "$" + number : number + " " + currency;
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static string FirstTwoTextElements(string name)
    {
        var info = new StringInfo(name);
        return info.LengthInTextElements <= 2 ? name : info.SubstringByTextElements(0, 2);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"max_(\d+x)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaxTierRegex();
}
