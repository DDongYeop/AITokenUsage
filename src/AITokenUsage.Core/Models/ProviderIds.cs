namespace AITokenUsage.Core.Models;

public static class ProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";
    public const string Copilot = "copilot";
    public const string Antigravity = "antigravity";

    /// <summary>Built-in providers in default display order.</summary>
    public static readonly IReadOnlyList<string> All = [Claude, Codex, Gemini, Copilot, Antigravity];

    /// <summary>
    /// The AI kind of a panel id: the text before the account separator ("claude#k3x9q2" → "claude");
    /// ids without one are returned as they are ("claude", "system").
    /// </summary>
    public static string KindOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        int separator = id.IndexOf(Accounts.AccountIds.Separator);
        return separator < 0 ? id : id[..separator];
    }
}
