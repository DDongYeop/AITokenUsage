namespace AITokenUsage;

/// <summary>Storage locations (spec §3.5).</summary>
public static class AppPaths
{
    public const string AppFolderName = "AITokenUsage";

    public static string RoamingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");

    public static string CacheFile => Path.Combine(LocalDirectory, "cache.json");

    /// <summary>Extra Codex accounts: one CODEX_HOME folder per account ("codex-&lt;key&gt;"), written by the app and the Codex CLI it starts.</summary>
    public static string AccountsDirectory => Path.Combine(LocalDirectory, "accounts");

    /// <summary>Temporary CLAUDE_CONFIG_DIR folders of a running "add Claude account" login; swept at startup.</summary>
    public static string LoginDirectory => Path.Combine(LocalDirectory, "login");

    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");

    public static string LogFile => Path.Combine(LogDirectory, "app.log");
}
