namespace AITokenUsage.Agent;

/// <summary>
/// macOS storage locations: settings, cache and the app's own secrets in ~/Library/Application Support/AITokenUsage,
/// logs in ~/Library/Logs/AITokenUsage. AITOKENUSAGE_HOME overrides the root (tests, side-by-side runs).
/// </summary>
public static class MacPaths
{
    public const string AppFolderName = "AITokenUsage";
    public const string HomeVariable = "AITOKENUSAGE_HOME";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DataDirectory =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Home, "Library", "Application Support", AppFolderName);

    public static string LogDirectory =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } custom
            ? Path.Combine(custom, "logs")
            : Path.Combine(Home, "Library", "Logs", AppFolderName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string CacheFile => Path.Combine(DataDirectory, "cache.json");

    public static string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

    public static string AccountsDirectory => Path.Combine(DataDirectory, "accounts");
}
