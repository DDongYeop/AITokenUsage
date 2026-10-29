using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Resources;

namespace AITokenUsage.UI;

/// <summary>GitHub device-flow sign-in for Copilot (spec §2.5, §5.4). Never shows or logs the token.</summary>
public partial class CopilotSignInPanel : UserControl, IDisposable
{
    private readonly AppHost _host;
    private readonly CopilotUsageProvider _copilot;
    private CancellationTokenSource? _flowCts;
    private DeviceCodeInfo? _code;
    private bool _disposed;

    public CopilotSignInPanel(AppHost host, CopilotUsageProvider copilot)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(copilot);

        _host = host;
        _copilot = copilot;
        InitializeComponent();

        SignInButton.Click += OnSignInClick;
        CopyButton.Click += OnCopyClick;
        OpenButton.Click += OnOpenClick;
        CancelButton.Click += (_, _) => _flowCts?.Cancel();
        SignOutButton.Click += OnSignOutClick;
        Refresh();
    }

    /// <summary>Shows sign-in, progress or "GitHub: login" depending on the current state.</summary>
    public void Refresh()
    {
        bool inProgress = _flowCts is not null;
        string? login = _copilot.SignedInLogin;

        ProgressPanel.Visibility = inProgress ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = !inProgress && login is not null ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = !inProgress && login is null ? Visibility.Visible : Visibility.Collapsed;
        LoginText.Text = login is null ? "" : $"GitHub: {login}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flowCts?.Cancel();
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (_flowCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _flowCts = cts;
        ShowError(null);
        ShowWaitingForCode();
        Refresh();

        var flow = new GitHubDeviceFlow(_host.ProviderContext.Http, _host.ProviderContext.Time);
        bool browserOpened = false;
        bool signedIn = false;

        try
        {
            while (!signedIn)
            {
                DeviceCodeInfo code = await Task.Run(() => flow.StartAsync(cts.Token), cts.Token);
                ShowCode(code);
                if (!browserOpened)
                {
                    browserOpened = true;
                    OpenBrowser(code.VerificationUri);
                }

                try
                {
                    string token = await Task.Run(() => flow.WaitForTokenAsync(code, cts.Token), cts.Token);
                    ProgressText.Text = UiStrings.CopilotVerifyingAccount;
                    await Task.Run(() => _copilot.SignInWithTokenAsync(token, cts.Token), cts.Token);
                    signedIn = true;
                }
                catch (DeviceFlowException ex) when (ex.Error == DeviceFlowError.Expired)
                {
                    _host.Log.Info("GitHub device code expired; requesting a new code.");
                }
            }

            _host.Log.Info("Copilot GitHub sign-in completed.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _host.Log.Info("Copilot GitHub sign-in cancelled.");
        }
        catch (DeviceFlowException ex)
        {
            _host.Log.Warn($"GitHub device flow failed: {ex.Error}.");
            ShowError(ex.UserMessage);
        }
        catch (CopilotSignInException ex)
        {
            _host.Log.Warn("Copilot sign-in was rejected.");
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            _host.Log.Error("Copilot sign-in failed.", ex);
            ShowError(UiStrings.CopilotSignInError);
        }
        finally
        {
            _flowCts = null;
            _code = null;
            cts.Dispose();
            if (!_disposed)
            {
                Refresh();
            }
        }

        if (signedIn && !_disposed)
        {
            await RefreshUsageAsync();
        }
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        SignOutButton.IsEnabled = false;
        ShowError(null);
        try
        {
            await Task.Run(_copilot.SignOut);
            _host.Log.Info("Copilot GitHub sign-out completed.");
        }
        catch (Exception ex)
        {
            _host.Log.Error("Copilot sign-out failed.", ex);
            ShowError(UiStrings.CopilotSignOutError);
        }
        finally
        {
            SignOutButton.IsEnabled = true;
            if (!_disposed)
            {
                Refresh();
            }
        }

        await RefreshUsageAsync();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_code is null)
        {
            return;
        }

        try
        {
            // One attempt, no retry delay: WPF's Clipboard.SetText retries 10 x 100 ms (up to ~1 s) on the UI thread,
            // which is shared with explorer's input queue while the widget is attached. The clipboard needs STA, so it
            // stays on this thread; a locked clipboard just reports failure and the user can click again.
            System.Windows.Forms.Clipboard.SetDataObject(_code.UserCode, copy: true, retryTimes: 0, retryDelay: 0);
            CopyButton.Content = UiStrings.CopilotCopiedButton;
        }
        catch (ExternalException ex)
        {
            _host.Log.Warn("Copying the GitHub device code to the clipboard failed.", ex);
            ShowError(UiStrings.CopilotClipboardError);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_code is not null)
        {
            OpenBrowser(_code.VerificationUri);
        }
    }

    /// <summary>ShellExecute can block (default-browser lookup, cold start), so it runs off the UI thread; a URL needs no STA.</summary>
    private void OpenBrowser(Uri uri)
    {
        string url = uri.AbsoluteUri;
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                _host.Log.Warn("Opening the GitHub device page failed.", ex);
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed)
                    {
                        ShowError(string.Format(CultureInfo.CurrentCulture, UiStrings.CopilotBrowserOpenErrorFormat, url));
                    }
                });
            }
        });
    }

    private async Task RefreshUsageAsync()
    {
        try
        {
            await _host.RefreshNowAsync();
        }
        catch (Exception ex)
        {
            _host.Log.Error("Refresh after Copilot sign-in change failed.", ex);
        }
    }

    private void ShowWaitingForCode()
    {
        ProgressText.Text = UiStrings.CopilotWaitingForCode;
        UserCodeText.Text = "";
        CopyButton.Content = UiStrings.CopilotCopyButton;
        CopyButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
    }

    private void ShowCode(DeviceCodeInfo code)
    {
        _code = code;
        ProgressText.Text = string.Format(CultureInfo.CurrentCulture, UiStrings.CopilotEnterCodeFormat, code.VerificationUri.AbsoluteUri);
        UserCodeText.Text = code.UserCode;
        CopyButton.Content = UiStrings.CopilotCopyButton;
        CopyButton.IsEnabled = true;
        OpenButton.IsEnabled = true;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
