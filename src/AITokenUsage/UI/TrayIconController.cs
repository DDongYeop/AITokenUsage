using System.Runtime.InteropServices;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Rotation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;
using AITokenUsage.Resources;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AITokenUsage.UI;

/// <summary>Tray icon with the current provider's line-1 value, a tooltip and the shared menu. UI thread only.</summary>
public sealed partial class TrayIconController : IDisposable
{
    private readonly AppHost _host;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private TrayIconRenderer.IconSpec? _lastSpec;
    private int _lastSize;
    private Drawing.Icon? _icon;
    private nint _hicon;
    private bool _disposed;

    public TrayIconController(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;

        var refreshItem = new Forms.ToolStripMenuItem(UiStrings.TrayMenuRefreshNow, null, (_, _) => { _ = _host.RefreshNowAsync(); });
        _pauseItem = new Forms.ToolStripMenuItem(UiStrings.TrayMenuPauseRotation, null, (_, _) => TogglePause());
        var exitItem = new Forms.ToolStripMenuItem(UiStrings.TrayMenuExit, null, (_, _) => _host.Shutdown());

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.AddRange(new Forms.ToolStripItem[] { refreshItem, _pauseItem, new Forms.ToolStripSeparator(), exitItem });
        _menu.Items.Insert(0, new Forms.ToolStripMenuItem(UiStrings.TrayMenuSettings, null, (_, _) => _host.OpenSettings()));
        _menu.Opening += OnMenuOpening;

        _notifyIcon = new Forms.NotifyIcon { ContextMenuStrip = _menu, Text = TrayTextFormatter.AppTitle };
        _notifyIcon.MouseClick += OnMouseClick;

        _host.ViewChanged += OnViewChanged;
        _host.SettingsChanged += OnSettingsChanged;
        _host.Rotation.CurrentChanged += OnRotationChanged;

        Update();
        _notifyIcon.Visible = _host.Settings.ShowTrayIcon;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.ViewChanged -= OnViewChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        _host.Rotation.CurrentChanged -= OnRotationChanged;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon?.Dispose();
        if (_hicon != 0)
        {
            DestroyIcon(_hicon);
            _hicon = 0;
        }
    }

    private void OnViewChanged(object? sender, ProviderView view) => Update();

    private void OnRotationChanged(object? sender, RotationChangedEventArgs e) => Update();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = _host.Settings.ShowTrayIcon;
        Update();
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e) =>
        _pauseItem.Checked = _host.Rotation.IsUserPaused;

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            _host.OpenSettings();
        }
    }

    private void TogglePause() => _host.Rotation.SetUserPaused(!_host.Rotation.IsUserPaused);

    private ProviderView? CurrentView()
    {
        string? id = _host.Rotation.CurrentId;
        ProviderView? view = id is null ? null : _host.Usage.GetView(id);
        return view ?? _host.EnabledViewsInOrder().FirstOrDefault();
    }

    private void Update()
    {
        if (_disposed)
        {
            return;
        }

        ProviderView? view = CurrentView();
        ProviderSettings? lines = view is null
            ? null
            : _host.Settings.Providers.FirstOrDefault(p => string.Equals(p.Id, view.ProviderId, StringComparison.Ordinal));
        ValueMode mode = _host.Settings.ValueMode;

        _notifyIcon.Text = TrayTextFormatter.Tooltip(view, lines, mode);

        UsageMeter? line1 = view is null ? null : TrayTextFormatter.SelectLine1(view, lines);
        TrayIconRenderer.IconSpec spec = TrayIconRenderer.CreateSpec(view, line1, mode);
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        if (_lastSpec == spec && _lastSize == size)
        {
            return;
        }

        SetIcon(spec, size);
        _lastSpec = spec;
        _lastSize = size;
    }

    private void SetIcon(TrayIconRenderer.IconSpec spec, int size)
    {
        using Drawing.Bitmap bitmap = TrayIconRenderer.DrawBitmap(spec, size);
        nint hicon = bitmap.GetHicon();
        Drawing.Icon icon = Drawing.Icon.FromHandle(hicon);
        _notifyIcon.Icon = icon;

        // Release the previous icon only after the shell has the new one.
        Drawing.Icon? oldIcon = _icon;
        nint oldHicon = _hicon;
        _icon = icon;
        _hicon = hicon;
        oldIcon?.Dispose();
        if (oldHicon != 0)
        {
            DestroyIcon(oldHicon);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);
}
