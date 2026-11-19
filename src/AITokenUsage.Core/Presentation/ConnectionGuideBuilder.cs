using System.Globalization;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Presentation;

/// <summary>Builds the "how to connect" steps for a provider in a given status (spec §4.4). Pure function.</summary>
public static class ConnectionGuideBuilder
{
    // Commands are code constants: never translated, never built from user input.
    // Node.js comes from winget on Windows and Homebrew on macOS (the Mac menu bar app shows the same guide).
    private static string NodeInstallCommand => OperatingSystem.IsWindows() ? "winget install OpenJS.NodeJS.LTS" : "brew install node";
    private static string AntigravityInstallCommand => OperatingSystem.IsWindows()
        ? "irm https://antigravity.google/cli/install.ps1 | iex"
        : "curl -fsSL https://antigravity.google/cli/install.sh | bash";
    private const string AntigravityCommand = "agy";
    private const string ClaudeInstallCommand = "npm install -g @anthropic-ai/claude-code";
    private const string CodexInstallCommand = "npm install -g @openai/codex";
    private const string GeminiInstallCommand = "npm install -g @google/gemini-cli";
    private const string ClaudeCommand = "claude";
    private const string CodexLoginCommand = "codex login";
    private const string GeminiCommand = "gemini";

    /// <summary>
    /// Null when there is nothing to guide: connected, rate-limited, errors, extra accounts, and combinations the spec does not cover.
    /// </summary>
    public static ConnectionGuide? Build(string providerId, ProviderStatus status, string? statusMessage)
    {
        // Extra accounts get their actions (sign in again, delete) from the settings card instead of a guide.
        if (AccountIds.IsAccountId(providerId))
        {
            return null;
        }

        return providerId switch
        {
            ProviderIds.Claude => status switch
            {
                ProviderStatus.NotInstalled => Install("Claude Code", ClaudeInstallCommand, Strings.GuideStepLoginClaude, ClaudeCommand),
                ProviderStatus.SignedOut => Login(Strings.GuideStepLoginClaude, ClaudeCommand),
                ProviderStatus.WaitingForCliRefresh => Login(Strings.GuideStepRefreshClaude, ClaudeCommand),
                _ => null,
            },
            ProviderIds.Codex => status switch
            {
                ProviderStatus.NotInstalled => Install("Codex", CodexInstallCommand, Strings.GuideStepLoginCodex, CodexLoginCommand),
                ProviderStatus.SignedOut => Login(Strings.GuideStepLoginCodex, CodexLoginCommand),
                _ => null,
            },
            ProviderIds.Gemini => status switch
            {
                ProviderStatus.NotInstalled => Install("Gemini CLI", GeminiInstallCommand, Strings.GuideStepLoginGemini, GeminiCommand),
                ProviderStatus.SignedOut => Login(Strings.GuideStepLoginGemini, GeminiCommand),
                ProviderStatus.Unsupported => new ConnectionGuide(
                    Array.Empty<GuideStep>(),
                    string.IsNullOrWhiteSpace(statusMessage) ? Strings.GuideGeminiDiscontinued : statusMessage),
                _ => null,
            },
            ProviderIds.Antigravity => status switch
            {
                ProviderStatus.NotInstalled => new ConnectionGuide(
                    [
                        new GuideStep(Strings.GuideStepInstallAntigravity, AntigravityInstallCommand, GuideAction.None),
                        new GuideStep(Strings.GuideStepLoginAntigravity, null, GuideAction.OpenTerminal, AntigravityCommand),
                        RecheckStep(),
                    ], null),
                ProviderStatus.SignedOut => Login(Strings.GuideStepLoginAntigravity, AntigravityCommand),
                _ => null,
            },
            // The sign-in UI itself is the App's CopilotSignInPanel; the guide only offers the re-check.
            ProviderIds.Copilot => status is ProviderStatus.SignedOut or ProviderStatus.NotInstalled
                ? new ConnectionGuide(new[] { RecheckStep() }, null)
                : null,
            _ => null,
        };
    }

    private static ConnectionGuide Install(string cliName, string installCommand, string loginText, string loginCommand) =>
        new(
            new[]
            {
                new GuideStep(Strings.GuideStepNode, NodeInstallCommand, GuideAction.None),
                new GuideStep(string.Format(CultureInfo.CurrentCulture, Strings.GuideStepInstall, cliName), installCommand, GuideAction.None),
                new GuideStep(loginText, null, GuideAction.OpenTerminal, loginCommand),
                RecheckStep(),
            },
            null);

    private static ConnectionGuide Login(string text, string terminalCommand) =>
        new(
            new[]
            {
                new GuideStep(text, null, GuideAction.OpenTerminal, terminalCommand),
                RecheckStep(),
            },
            null);

    private static GuideStep RecheckStep() => new(Strings.GuideStepRecheck, null, GuideAction.Recheck);
}
