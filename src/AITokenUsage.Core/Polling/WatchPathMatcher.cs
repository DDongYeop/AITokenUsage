namespace AITokenUsage.Core.Polling;

/// <summary>Matches a changed file-system path against a provider's watch path.</summary>
public static class WatchPathMatcher
{
    /// <summary>
    /// True when <paramref name="fullPath"/> equals <paramref name="watchPath"/> or starts with watchPath followed by
    /// a directory separator. Case-insensitive; forward and back slashes are equivalent; trailing separators are ignored.
    /// </summary>
    public static bool Matches(string? fullPath, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(watchPath))
        {
            return false;
        }

        string path = Normalize(fullPath);
        string watch = Normalize(watchPath);
        if (watch.Length == 0)
        {
            return false;
        }

        if (string.Equals(path, watch, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Length > watch.Length
            && path[watch.Length] == '\\'
            && path.StartsWith(watch, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
