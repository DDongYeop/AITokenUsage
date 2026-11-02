using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Accounts;

/// <summary>The terminal command that signs an extra account in (spec §2.2 and §3).</summary>
public static class AccountLoginCommand
{
    /// <summary>Characters that would break out of the quoted <c>set "NAME=value"</c> in cmd.exe, so a path holding one is refused.</summary>
    public const string ForbiddenCharacters = "\"%^&|<>;";

    /// <summary>
    /// <c>claude</c>: <c>set "CLAUDE_SECURESTORAGE_CONFIG_DIR=" &amp;&amp; set "CLAUDE_CONFIG_DIR=&lt;dir&gt;" &amp;&amp; claude auth login</c>
    /// (the first set clears a variable the user may have, which Claude Code reads first).
    /// <c>codex</c>: <c>set "CODEX_HOME=&lt;dir&gt;" &amp;&amp; codex login</c>.
    /// Null when the kind is neither, the directory is blank, or it holds a <see cref="ForbiddenCharacters"/> character
    /// or a control character (a line break would end the command).
    /// </summary>
    public static string? Build(string kind, string directory)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(directory);

        if (string.IsNullOrWhiteSpace(directory) || directory.Any(c => char.IsControl(c) || ForbiddenCharacters.Contains(c)))
        {
            return null;
        }

        return kind switch
        {
            ProviderIds.Claude =>
                $"set \"CLAUDE_SECURESTORAGE_CONFIG_DIR=\" && set \"CLAUDE_CONFIG_DIR={directory}\" && claude auth login",
            ProviderIds.Codex => $"set \"CODEX_HOME={directory}\" && codex login",
            _ => null,
        };
    }
}
