using System.Globalization;
using System.Text.Json;

namespace AITokenUsage.Core.Util;

/// <summary>
/// Tolerant JsonElement accessors. Every method returns null when <paramref name="e"/> is not an
/// object, the property is missing, or the value has the wrong kind; none of them throws.
/// </summary>
public static class Json
{
    private const double MinUnixMilliseconds = -62_135_596_800_000d;
    private const double MaxUnixMilliseconds = 253_402_300_799_999d;

    public static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A JSON number, or a string holding an invariant-culture number. NaN and infinities give null.</summary>
    public static double? Num(JsonElement e, string name)
    {
        if (!TryGet(e, name, out JsonElement value))
        {
            return null;
        }

        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out number):
                break;
            case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number):
                break;
            default:
                return null;
        }

        return double.IsFinite(number) ? number : null;
    }

    /// <summary>JSON true/false only.</summary>
    public static bool? Bool(JsonElement e, string name)
    {
        if (!TryGet(e, name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static JsonElement? Obj(JsonElement e, string name) =>
        TryGet(e, name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;

    public static JsonElement? Arr(JsonElement e, string name) =>
        TryGet(e, name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value : null;

    /// <summary>ISO 8601 / RFC 3339 string. A value without an offset is taken as UTC.</summary>
    public static DateTimeOffset? Iso(JsonElement e, string name)
    {
        string? text = Str(e, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;
    }

    /// <summary>Unix time in seconds (number or numeric string, fractions allowed).</summary>
    public static DateTimeOffset? EpochSeconds(JsonElement e, string name) =>
        Num(e, name) is double seconds ? FromUnixMilliseconds(Math.Round(seconds * 1000)) : null;

    /// <summary>Unix time in milliseconds (number or numeric string), e.g. Claude expiresAt or Gemini expiry_date.</summary>
    public static DateTimeOffset? EpochMilliseconds(JsonElement e, string name) =>
        Num(e, name) is double milliseconds ? FromUnixMilliseconds(Math.Round(milliseconds)) : null;

    /// <summary>
    /// <paramref name="origin"/> plus a relative number of seconds, or null when the offset or the result
    /// is out of the DateTimeOffset range (e.g. a bogus 1e18). Never throws.
    /// </summary>
    public static DateTimeOffset? AddSecondsOrNull(DateTimeOffset origin, double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return null;
        }

        try
        {
            return origin.AddSeconds(seconds);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? FromUnixMilliseconds(double milliseconds)
    {
        if (milliseconds < MinUnixMilliseconds || milliseconds > MaxUnixMilliseconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
