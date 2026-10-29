using System.Security;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Settings;
using Microsoft.Win32;

namespace AITokenUsage;

/// <summary>HKCU\...\Run value "AITokenUsage" = quoted Environment.ProcessPath. Call from a background thread.</summary>
public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AITokenUsage";

    public static string? BuildCommand() =>
        Environment.ProcessPath is { Length: > 0 } path ? "\"" + path + "\"" : null;

    /// <summary>The current Run value, or null when it is missing, not a string, or cannot be read.</summary>
    public static string? ReadCurrent()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Startup reconciliation (spec §5.3). When the setting is off but the Run value already points at this exe
    /// (the installer wrote it), the registry is left alone and true is returned so the caller turns the setting on.
    /// Otherwise behaves like <see cref="Apply"/> and returns false. Takes a bool, not AppSettings, because it runs on
    /// a background thread that must not read the object the UI edits.
    /// </summary>
    public static bool Sync(bool settingEnabled, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (StartupSync.Decide(ReadCurrent(), Environment.ProcessPath, settingEnabled) == StartupSyncDecision.AdoptRegistryEnabled)
        {
            return true;
        }

        Apply(settingEnabled, log);
        return false;
    }

    public static void Apply(bool enabled, IAppLog log)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            object? current = key.GetValue(ValueName);
            if (enabled)
            {
                string? command = BuildCommand();
                if (command is null)
                {
                    log.Warn("startup registration skipped: process path is unknown");
                    return;
                }

                if (!string.Equals(current as string, command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                    log.Info("startup registration enabled");
                }
            }
            else if (current is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log.Info("startup registration removed");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            log.Warn("could not update the startup registration", ex);
        }
    }
}
