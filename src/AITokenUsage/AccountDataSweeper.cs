using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;

namespace AITokenUsage;

/// <summary>
/// Startup cleanup of the extra accounts' leftovers (spec §6). Deletes only inside the app's own folders and never
/// touches the CLI's own login files (~/.claude, ~/.codex) or other secrets (e.g. the Copilot token).
/// Runs on a background thread; every failure is logged and swallowed.
/// </summary>
internal static class AccountDataSweeper
{
    public static void Sweep(IEnumerable<string> settingsIds, IAppLog log) =>
        Sweep(settingsIds, log, AppPaths.LoginDirectory, AppPaths.LocalDirectory, AppPaths.AccountsDirectory);

    internal static void Sweep(
        IEnumerable<string> settingsIds, IAppLog log, string loginDirectory, string secretsDirectory, string accountsDirectory)
    {
        ArgumentNullException.ThrowIfNull(settingsIds);
        ArgumentNullException.ThrowIfNull(log);

        string[] ids = settingsIds.ToArray();
        try
        {
            SweepLoginDirectory(loginDirectory, log);
        }
        catch (Exception ex)
        {
            log.Warn("account cleanup: the temporary login folder could not be cleaned", ex);
        }

        try
        {
            SweepClaudeSecrets(ids, secretsDirectory, log);
        }
        catch (Exception ex)
        {
            log.Warn("account cleanup: orphaned Claude logins could not be cleaned", ex);
        }

        try
        {
            SweepCodexHomes(ids, accountsDirectory, log);
        }
        catch (Exception ex)
        {
            log.Warn("account cleanup: orphaned Codex folders could not be cleaned", ex);
        }
    }

    /// <summary>The whole folder: it only ever holds the temporary CLAUDE_CONFIG_DIR of a login that was running when the app exited.</summary>
    private static void SweepLoginDirectory(string loginDirectory, IAppLog log)
    {
        if (!Directory.Exists(loginDirectory))
        {
            return;
        }

        if (IsReparsePoint(loginDirectory))
        {
            log.Warn("account cleanup: the login folder is a link; left alone");
            return;
        }

        Directory.Delete(loginDirectory, recursive: true);
        log.Info("account cleanup: removed leftover login folder");
    }

    private static void SweepClaudeSecrets(IReadOnlyCollection<string> settingsIds, string secretsDirectory, IAppLog log)
    {
        if (!Directory.Exists(secretsDirectory))
        {
            return;
        }

        // EnumerateFiles("*.bin") also matches longer extensions such as ".bin2"; keep only the exact one.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(secretsDirectory, "*.bin"))
        {
            if (string.Equals(Path.GetExtension(file), ".bin", StringComparison.OrdinalIgnoreCase))
            {
                byName[Path.GetFileNameWithoutExtension(file)] = file;
            }
        }

        foreach (string name in AccountStorage.OrphanClaudeSecrets(byName.Keys, settingsIds))
        {
            try
            {
                File.Delete(byName[name]);
                log.Info($"account cleanup: removed orphaned login '{name}'");
            }
            catch (Exception ex)
            {
                log.Warn($"account cleanup: could not remove orphaned login '{name}'", ex);
            }
        }
    }

    private static void SweepCodexHomes(IReadOnlyCollection<string> settingsIds, string accountsDirectory, IAppLog log)
    {
        if (!Directory.Exists(accountsDirectory))
        {
            return;
        }

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in Directory.EnumerateDirectories(accountsDirectory))
        {
            byName[Path.GetFileName(directory)] = directory;
        }

        foreach (string name in AccountStorage.OrphanCodexHomes(byName.Keys, settingsIds))
        {
            string path = byName[name];
            try
            {
                if (IsReparsePoint(path))
                {
                    log.Warn($"account cleanup: orphaned folder '{name}' is a link; left alone");
                    continue;
                }

                Directory.Delete(path, recursive: true);
                log.Info($"account cleanup: removed orphaned folder '{name}'");
            }
            catch (Exception ex)
            {
                log.Warn($"account cleanup: could not remove orphaned folder '{name}'", ex);
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
