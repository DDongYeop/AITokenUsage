using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AITokenUsage.Core.Localization;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>"General" page: language (restart to apply), refresh interval, start with Windows, tray icon (v1.1 spec §3.2, §4.3).</summary>
public partial class GeneralPage : SettingsPage
{
    /// <summary>Language names are always shown in their own language, whatever the UI language is.</summary>
    private static readonly IReadOnlyList<SettingsChoice> LanguageChoices =
    [
        new(AppSettings.LanguageAuto, Strings.LanguageNameAuto),
        new(AppSettings.LanguageKorean, "한국어"),
        new(AppSettings.LanguageEnglish, "English"),
    ];

    private static readonly IReadOnlyList<SettingsChoice> RefreshChoices =
        ProviderDefaults.RefreshChoicesMinutes
            .Select(minutes => new SettingsChoice(minutes, string.Format(CultureInfo.CurrentCulture, UiStrings.RefreshMinutesFormat, minutes)))
            .ToArray();

    public GeneralPage(AppHost host)
        : base(host)
    {
        InitializeComponent();

        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is SettingsChoice { Value: string language })
            {
                Update(s => s.Language = language);
                UpdateRestartNotice();
            }
        };
        RestartButton.Click += OnRestartClick;
        RefreshBox.SelectionChanged += (_, _) =>
        {
            if (RefreshBox.SelectedItem is SettingsChoice { Value: int minutes })
            {
                Update(s => s.RefreshMinutes = minutes);
            }
        };
        HookToggle(StartWithWindowsBox, on => Update(s => s.StartWithWindows = on));
        HookToggle(ShowTrayBox, on => Update(s => s.ShowTrayIcon = on));
        Host.SettingsChanged += OnHostSettingsChanged;
    }

    protected override void OnLoad()
    {
        LanguageBox.ItemsSource = LanguageChoices;
        RefreshBox.ItemsSource = RefreshChoices;
        ShowSettings();
    }

    protected override void OnDispose() => Host.SettingsChanged -= OnHostSettingsChanged;

    /// <summary>Copies the current settings into the controls; callers run it inside WhileLoading so nothing is written back.</summary>
    private void ShowSettings()
    {
        AppSettings settings = Host.Settings;

        LanguageBox.SelectedItem = Find(LanguageChoices, LanguageResolver.Normalize(settings.Language));
        UpdateRestartNotice();
        RefreshBox.SelectedItem = Find(RefreshChoices, settings.RefreshMinutes);
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        ShowTrayBox.IsChecked = settings.ShowTrayIcon;
    }

    /// <summary>
    /// The app can change settings behind an open window (AppHost adopts the installer's Run value into StartWithWindows),
    /// so keep the controls in step. AppHost raises SettingsChanged on the UI thread; marshal anyway in case that changes.
    /// </summary>
    private void OnHostSettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => OnHostSettingsChanged(sender, e)));
            return;
        }

        if (!IsDisposed)
        {
            WhileLoading(ShowSettings);
        }
    }

    /// <summary>Shown while the saved language would start the app in a language other than the running one.</summary>
    private void UpdateRestartNotice()
    {
        CultureInfo running = LanguageBootstrap.RunningCulture ?? CultureInfo.CurrentUICulture;
        bool required = LanguageResolver.RequiresRestart(Host.Settings.Language, CultureInfo.InstalledUICulture, running);
        RestartNotice.Visibility = required ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        // Restart has no re-entry guard: one click only. It shuts the host down, which closes this window (and disposes
        // this page) synchronously, so run it after the click handler has returned.
        RestartButton.IsEnabled = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            Host.Log.Info("language restart requested from the settings window");
            LanguageBootstrap.Restart(Host);

            // Still open: Restart could not start the new instance (already logged); let the user try again.
            if (!IsDisposed)
            {
                RestartButton.IsEnabled = true;
            }
        }));
    }
}
