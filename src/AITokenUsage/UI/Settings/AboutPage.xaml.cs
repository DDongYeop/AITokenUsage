using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>"About" page: app name and version, the unofficial-API notice, the log folder and the settings file (v1.1 spec §4.3).</summary>
public partial class AboutPage : SettingsPage
{
    public AboutPage(AppHost host)
        : base(host)
    {
        InitializeComponent();
        OpenLogFolderButton.Click += OnOpenLogFolderClick;
    }

    protected override void OnLoad()
    {
        AppCard.Header = TrayTextFormatter.AppTitle;
        string version = string.Format(CultureInfo.CurrentCulture, UiStrings.AboutVersionFormat, AppVersion());
        AppCard.Description = AppCopyright() is { Length: > 0 } copyright ? version + Environment.NewLine + copyright : version;
        LogsCard.Description = AppPaths.LogDirectory;
        SettingsFileCard.Description = string.Format(
            CultureInfo.CurrentCulture, UiStrings.AboutSettingsFileDescriptionFormat, Host.SettingsStore.FilePath);
    }

    /// <summary>InformationalVersion without the "+commit" suffix the SDK appends.</summary>
    private static string AppVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AboutPage).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return VersionText.FromInformational(informational, assembly.GetName().Version);
    }

    /// <summary>The Copyright set in Directory.Build.props ("Copyright © 2026 DDongYeop").</summary>
    private static string? AppCopyright() =>
        (Assembly.GetEntryAssembly() ?? typeof(AboutPage).Assembly).GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = AppPaths.LogDirectory;
        IAppLog log = Host.Log;

        // Explorer can take a moment to start; keep the UI thread free.
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                log.Warn("opening the log folder failed", ex);
            }
        });
    }
}
