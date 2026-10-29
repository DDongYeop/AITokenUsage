using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// Windows 11 style settings window (v1.1 spec §4.1): left navigation and one page per entry, Fluent theme following the
/// system light/dark mode. Every change is applied and saved immediately; there is no OK/Cancel.
/// </summary>
public partial class SettingsWindow : Window
{
#if DEBUG
    /// <summary>Debug builds only: "--settings-page=&lt;index&gt;" picks the first page (screen captures without real input).</summary>
    private const string InitialPageArgument = "--settings-page=";
#endif

    private static SettingsWindow? s_current;

    private readonly AppHost _host;
    private readonly IReadOnlyList<SettingsNavItem> _navItems;
    private readonly SettingsPage?[] _pages;

    public SettingsWindow(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        InitializeComponent();

        _navItems =
        [
            new("\uE71B", UiStrings.NavProviders, h => new ProvidersPage(h)),
            new("\uE7F4", UiStrings.NavWidget, h => new WidgetPage(h)),
            new("\uE895", UiStrings.NavRotation, h => new RotationPage(h)),
            new("\uE713", UiStrings.NavGeneral, h => new GeneralPage(h)),
            new("\uE946", UiStrings.NavAbout, h => new AboutPage(h)),
        ];
        _pages = new SettingsPage?[_navItems.Count];

        NavList.ItemsSource = _navItems;
        NavList.SelectionChanged += OnNavSelectionChanged;
        NavList.SelectedIndex = InitialPageIndex(_navItems.Count);

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Opens the single settings window, or restores and activates the one already open. UI thread only.</summary>
    public static void ShowOrActivate(AppHost host)
    {
        if (s_current is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new SettingsWindow(host);
        s_current = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Closes the open settings window, if any. Closing disposes the pages, which applies a pending slider change
    /// synchronously, so call this before the host starts shutting down (AppHost.ApplySettings is a no-op afterwards).
    /// UI thread only.
    /// </summary>
    public static void CloseCurrent() => s_current?.Close();

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = NavList.SelectedIndex;
        if (index < 0 || index >= _navItems.Count)
        {
            return;
        }

        // Pages are built on first visit and reused, so their state (e.g. a pending slider change) survives switching.
        SettingsPage? page = _pages[index];
        if (page is null)
        {
            page = _navItems[index].CreatePage(_host);
            page.Load();
            _pages[index] = page;
        }

        PageTitle.Text = _navItems[index].Label;
        PageHost.Content = page;
        PageScroller.ScrollToTop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        LogThemeBrushFallbacks();
    }

    /// <summary>
    /// SettingsStyles.xaml defines system-colour fallbacks under the Fluent brush keys at application level; the window's
    /// Fluent dictionary should shadow every one of them. Logs the keys that resolve to a fallback (a missing Fluent key).
    /// </summary>
    private void LogThemeBrushFallbacks()
    {
        var fallbacks = new List<string>();
        int total = 0;
        foreach (ResourceDictionary dictionary in Application.Current?.Resources.MergedDictionaries ?? [])
        {
            foreach (object key in dictionary.Keys)
            {
                if (key is string name && dictionary[key] is Brush fallback)
                {
                    total++;
                    if (ReferenceEquals(TryFindResource(name), fallback))
                    {
                        fallbacks.Add(name);
                    }
                }
            }
        }

        if (fallbacks.Count == 0)
        {
            _host.Log.Info($"settings window opened: {total} theme brushes from Fluent");
        }
        else
        {
            _host.Log.Warn($"settings window opened: Fluent brushes missing, using system colours for {string.Join(", ", fallbacks)}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        NavList.SelectionChanged -= OnNavSelectionChanged;
        foreach (SettingsPage? page in _pages)
        {
            try
            {
                page?.Dispose();
            }
            catch (Exception ex)
            {
                _host.Log.Warn($"settings window: disposing {page?.GetType().Name} failed", ex);
            }
        }

        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    private static int InitialPageIndex(int count)
    {
#if DEBUG
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith(InitialPageArgument, StringComparison.Ordinal)
                && int.TryParse(arg.AsSpan(InitialPageArgument.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && index < count)
            {
                return index;
            }
        }
#endif

        return 0;
    }
}
