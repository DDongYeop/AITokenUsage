using System.Windows;
using System.Windows.Controls;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// Base of the settings window pages (v1.1 spec §4.3). Every change is applied and saved immediately through
/// AppHost.ApplySettings; there is no OK/Cancel. The window creates a page on first selection, calls Load once and
/// disposes it when the window closes.
/// </summary>
public abstract class SettingsPage : UserControl, IDisposable
{
    protected SettingsPage(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
    }

    protected AppHost Host { get; }

    /// <summary>True while Load copies settings into the controls: change handlers must not write back.</summary>
    protected bool IsLoading { get; private set; }

    protected bool IsDisposed { get; private set; }

    /// <summary>Copies the current settings into the controls without applying anything.</summary>
    public void Load() => WhileLoading(OnLoad);

    /// <summary>Flushes pending edits (they are applied synchronously) and releases subscriptions. UI thread only.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            OnDispose();
        }
        finally
        {
            IsDisposed = true;
        }

        GC.SuppressFinalize(this);
    }

    protected abstract void OnLoad();

    protected virtual void OnDispose()
    {
    }

    /// <summary>Runs <paramref name="fill"/> with IsLoading set, so control events it raises do not write settings back.</summary>
    protected void WhileLoading(Action fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        bool wasLoading = IsLoading;
        IsLoading = true;
        try
        {
            fill();
        }
        finally
        {
            IsLoading = wasLoading;
        }
    }

    /// <summary>Changes the settings and applies them (ignored while loading).</summary>
    protected void Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsLoading)
        {
            return;
        }

        change(Host.Settings);
        ApplySettings();
    }

    protected void ApplySettings()
    {
        try
        {
            Host.ApplySettings();
        }
        catch (Exception ex)
        {
            Host.Log.Error("Applying settings from the settings window failed.", ex);
        }
    }

    /// <summary>
    /// Applies on Checked and Unchecked so keyboard, UI Automation and mouse changes all take effect
    /// (never on load: Load runs with IsLoading set).
    /// </summary>
    protected void HookToggle(CheckBox box, Action<bool> apply)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(apply);

        RoutedEventHandler handler = (_, _) =>
        {
            if (!IsLoading)
            {
                apply(box.IsChecked == true);
            }
        };
        box.Checked += handler;
        box.Unchecked += handler;
    }

    protected static SettingsChoice? Find(IReadOnlyList<SettingsChoice> choices, object value) =>
        choices.FirstOrDefault(c => Equals(c.Value, value));
}
