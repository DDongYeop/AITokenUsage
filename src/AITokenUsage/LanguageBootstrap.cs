using System.Diagnostics;
using System.Globalization;
using AITokenUsage.Core.Localization;

namespace AITokenUsage;

/// <summary>Applies the UI language once at startup and restarts the app when the user changes it (v1.1 spec §3.2).</summary>
public static class LanguageBootstrap
{
    public const string RestartedArgument = "--restarted";

    public static CultureInfo Apply(string? languageSetting)
    {
        CultureInfo culture = LanguageResolver.Resolve(languageSetting, CultureInfo.InstalledUICulture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }

    /// <summary>Starts a new instance that waits for our mutex, then shuts this one down through AppHost.Shutdown (which saves pending settings).</summary>
    public static void Restart(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (Environment.ProcessPath is not { Length: > 0 } path)
        {
            host.Log.Warn("restart skipped: process path is unknown");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, ArgumentList = { RestartedArgument } })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            host.Log.Warn("restart failed to start a new instance", ex);
            return;
        }

        // The normal exit path: closes the settings window, disposes the host (taskbar, polling, final save), then Application.Shutdown.
        host.Shutdown();
    }
}
