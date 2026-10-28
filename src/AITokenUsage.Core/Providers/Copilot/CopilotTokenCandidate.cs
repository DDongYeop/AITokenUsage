namespace AITokenUsage.Core.Providers.Copilot;

public enum CopilotTokenSourceKind { Saved, Environment, GitHubCli, CopilotCli, LanguageServerFile }

/// <param name="Description">Log-safe origin such as "env:GH_TOKEN" (never the token).</param>
public sealed record CopilotTokenCandidate(CopilotTokenSourceKind Kind, string Description, string Token, string? Login)
{
    // Records print every property by default; keep the token out of logs and exception messages.
    public override string ToString() => $"{Kind} ({Description})";
}
