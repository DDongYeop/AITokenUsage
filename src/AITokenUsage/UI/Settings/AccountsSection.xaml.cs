using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AITokenUsage.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// The "Add account" area of the AI connections page (multi-account spec §5): [Add Claude account] [Add Codex account],
/// disabled while that CLI is not on PATH or any sign-in runs; the progress line with [Cancel]; the result. The page owns
/// the sign-in session and calls the Show methods. UI thread only.
/// </summary>
public partial class AccountsSection : UserControl, IDisposable
{
    private readonly AppHost _host;
    private bool? _claudeInstalled;
    private bool? _codexInstalled;
    private bool _busy;
    private bool _disposed;

    /// <summary>An account was added in this window: the "restart to show it" notice stays through later sign-ins.</summary>
    private bool _restartPending;

    public AccountsSection(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        InitializeComponent();

        AddClaudeButton.Click += (_, _) => AddRequested?.Invoke(this, ProviderIds.Claude);
        AddCodexButton.Click += (_, _) => AddRequested?.Invoke(this, ProviderIds.Codex);
        CancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        RestartButton.Click += OnRestartClick;
    }

    /// <summary>The kind ("claude" or "codex") the user wants to add.</summary>
    public event EventHandler<string>? AddRequested;

    public event EventHandler? CancelRequested;

    /// <summary>The PATH check finished (see <see cref="IsInstalled"/>); the page passes it on to the account cards.</summary>
    public event EventHandler? InstalledChecked;

    /// <summary>
    /// Whether the CLI of <paramref name="kind"/> ("claude"/"codex") is on PATH; null until the background check finished.
    /// </summary>
    public bool? IsInstalled(string kind) => kind switch
    {
        ProviderIds.Claude => _claudeInstalled,
        ProviderIds.Codex => _codexInstalled,
        _ => null,
    };

    /// <summary>The message for a failed sign-in (also used by the provider cards).</summary>
    public static string FailureText(AccountLoginFailure failure) => failure switch
    {
        AccountLoginFailure.InvalidPath => UiStrings.AccountFailedInvalidPath,
        AccountLoginFailure.FolderFailed => UiStrings.AccountFailedFolder,
        AccountLoginFailure.TerminalFailed => UiStrings.AccountFailedTerminal,
        AccountLoginFailure.TimedOut => UiStrings.AccountFailedTimedOut,
        _ => UiStrings.AccountFailedSave,
    };

    /// <summary>Looks for the claude and codex commands on PATH off the UI thread; the buttons stay off until then.</summary>
    public void CheckInstalled()
    {
        var processes = _host.ProviderContext.Processes;
        _ = Task.Run(() =>
        {
            bool claude = Find(processes, "claude");
            bool codex = Find(processes, "codex");
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _claudeInstalled = claude;
                _codexInstalled = codex;
                ClaudeMissingText.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
                CodexMissingText.Visibility = codex ? Visibility.Collapsed : Visibility.Visible;
                UpdateButtons();
                InstalledChecked?.Invoke(this, EventArgs.Empty);
            }));
        });

        bool Find(Core.Abstractions.IProcessRunner runner, string command)
        {
            try
            {
                return runner.FindOnPath(command) is not null;
            }
            catch (Exception ex)
            {
                _host.Log.Warn($"looking for '{command}' on PATH failed", ex);
                return false;
            }
        }
    }

    /// <summary>True while any sign-in (an add here or a re-login on a card) runs: both add buttons are off.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    /// <summary>An earlier "added … restart" notice stays visible alongside the new progress (see _restartPending).</summary>
    public void ShowProgress()
    {
        ShowError(null);
        ProgressPanel.Visibility = Visibility.Visible;
        UpdateAddedNotice();
    }

    /// <summary>Cancelled: back to the start (with the restart notice if an earlier add is waiting for it).</summary>
    public void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        UpdateAddedNotice();
    }

    public void ShowAdded()
    {
        _restartPending = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ShowError(null);
        RestartButton.IsEnabled = true;
        UpdateAddedNotice();
    }

    public void ShowFailure(AccountLoginFailure failure)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        ShowError(FailureText(failure));
        UpdateAddedNotice();
    }

    private void UpdateAddedNotice() =>
        AddedNotice.Visibility = _restartPending ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose() => _disposed = true;

    private void UpdateButtons()
    {
        AddClaudeButton.IsEnabled = _claudeInstalled == true && !_busy;
        AddCodexButton.IsEnabled = _codexInstalled == true && !_busy;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Same as the language restart (GeneralPage): posted, because shutting down closes this window synchronously.</summary>
    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        RestartButton.IsEnabled = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_disposed)
            {
                return;
            }

            _host.Log.Info("restart requested after adding an account");
            LanguageBootstrap.Restart(_host);

            // Still open: Restart could not start the new instance (already logged); let the user try again.
            if (!_disposed)
            {
                RestartButton.IsEnabled = true;
            }
        }));
    }
}
