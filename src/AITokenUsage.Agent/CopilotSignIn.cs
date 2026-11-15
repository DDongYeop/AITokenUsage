using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Polling;
using AITokenUsage.Core.Providers.Copilot;

namespace AITokenUsage.Agent;

/// <summary>
/// The GitHub device-flow sign-in of the Windows settings page (CopilotSignInPanel), driven by stdin commands and
/// reported as {"type":"copilotSignIn"} lines: phase "code" (userCode, verificationUri), "verifying", "done",
/// "cancelled" or "error" (message). The Mac app shows the code and opens the browser.
/// </summary>
public sealed class CopilotSignIn(CopilotUsageProvider copilot, GitHubDeviceFlow flow, UsageService usage, IAppLog log)
{
    private CancellationTokenSource? _cts;

    public bool TryHandle(string command)
    {
        switch (command)
        {
            case "copilot-signin":
                Start();
                return true;
            case "copilot-cancel":
                Cancel();
                return true;
            case "copilot-signout":
                Cancel();
                try
                {
                    copilot.SignOut();
                    log.Info("Copilot GitHub sign-out completed.");
                }
                catch (Exception ex)
                {
                    log.Error("Copilot sign-out failed.", ex);
                }

                _ = usage.RefreshNowAsync(copilot.Id);
                return true;
            default:
                return false;
        }
    }

    public void Cancel()
    {
        try
        {
            Interlocked.Exchange(ref _cts, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The flow finished between the exchange and the cancel.
        }
    }

    private void Start()
    {
        var cts = new CancellationTokenSource();
        try
        {
            Interlocked.Exchange(ref _cts, cts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _ = Task.Run(() => RunAsync(cts));
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        try
        {
            while (true)
            {
                DeviceCodeInfo code = await flow.StartAsync(cts.Token).ConfigureAwait(false);
                Report("code", userCode: code.UserCode, verificationUri: code.VerificationUri.ToString());
                try
                {
                    string token = await flow.WaitForTokenAsync(code, cts.Token).ConfigureAwait(false);
                    Report("verifying");
                    await copilot.SignInWithTokenAsync(token, cts.Token).ConfigureAwait(false);
                    break;
                }
                catch (DeviceFlowException ex) when (ex.Error == DeviceFlowError.Expired)
                {
                    log.Info("GitHub device code expired; requesting a new code.");
                }
            }

            log.Info("Copilot GitHub sign-in completed.");
            Report("done");
            await usage.RefreshNowAsync(copilot.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            log.Info("Copilot GitHub sign-in cancelled.");
            Report("cancelled");
        }
        catch (DeviceFlowException ex)
        {
            log.Warn($"GitHub device flow failed: {ex.Error}.");
            Report("error", message: ex.UserMessage);
        }
        catch (CopilotSignInException ex)
        {
            log.Warn("Copilot sign-in was rejected.");
            Report("error", message: ex.Message);
        }
        catch (Exception ex)
        {
            log.Error("Copilot sign-in failed.", ex);
            Report("error", message: ex.GetType().Name);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
        }
    }

    private static void Report(string phase, string? userCode = null, string? verificationUri = null, string? message = null) =>
        Program.WriteLine(new SignInEvent("copilotSignIn", phase, userCode, verificationUri, message));

    private sealed record SignInEvent(string Type, string Phase, string? UserCode, string? VerificationUri, string? Message);
}
