using System.Security;
using AITokenUsage.Core.Abstractions;
using Microsoft.Win32;

namespace AITokenUsage;

/// <summary>HKCU\...\Run value "AITokenUsage" = quoted Environment.ProcessPath. Call from a background thread.</summary>
public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AITokenUsage";

    public static string? BuildCommand() =>
        Environment.ProcessPath is { Length: > 0 } path ? "\"" + path + "\"" : null;

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
