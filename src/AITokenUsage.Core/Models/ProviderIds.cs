namespace AITokenUsage.Core.Models;

public static class ProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";
    public const string Copilot = "copilot";

    /// <summary>Canonical order: claude, codex, gemini, copilot.</summary>
    public static readonly IReadOnlyList<string> All = [Claude, Codex, Gemini, Copilot];
}
