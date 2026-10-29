namespace AITokenUsage.Core.Util;

/// <summary>The version shown on the settings "About" page (v1.1 spec §4.3).</summary>
public static class VersionText
{
    /// <summary>
    /// The InformationalVersion without its "+build metadata" suffix (the SDK appends the commit hash there).
    /// Falls back to <paramref name="fallback"/> (major.minor.build), then "?".
    /// </summary>
    public static string FromInformational(string? informationalVersion, Version? fallback)
    {
        string value = informationalVersion ?? "";
        int plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        value = value.Trim();
        if (value.Length > 0)
        {
            return value;
        }

        return fallback is null ? "?" : fallback.ToString(fallback.Build >= 0 ? 3 : 2);
    }
}
