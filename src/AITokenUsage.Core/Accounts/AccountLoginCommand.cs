using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Accounts;

/// <summary>
/// How the terminal that signs an extra account in is started (spec §2.2 and §3): <c>cmd.exe</c> with
/// <see cref="Arguments"/>, and the account folder passed through the process environment rather than a shell command,
/// so no quoting can change it. <c>/c … || pause</c> closes the window after a successful login and keeps it open on a
/// failure so the error stays readable.
/// </summary>
/// <param name="Arguments">The cmd.exe arguments: <c>/c claude auth login || pause</c> or <c>/c codex login || pause</c>.</param>
/// <param name="Environment">Variables to set in the terminal's environment; a null value removes that variable.</param>
public sealed record AccountLoginCommand(string Arguments, IReadOnlyDictionary<string, string?> Environment)
{
    public const string ClaudeArguments = "/c claude auth login || pause";
    public const string CodexArguments = "/c codex login || pause";

    /// <summary>
    /// <c>claude</c>: removes <c>CLAUDE_SECURESTORAGE_CONFIG_DIR</c> (the user may have it, and Claude Code reads it first)
    /// and sets <c>CLAUDE_CONFIG_DIR</c> to the folder. <c>codex</c>: sets <c>CODEX_HOME</c> to the folder.
    /// Null when the kind is neither, or the folder is blank, not a fully qualified path, or holds a control character.
    /// </summary>
    public static AccountLoginCommand? Create(string kind, string directory)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(directory);

        if (string.IsNullOrWhiteSpace(directory) || directory.Any(char.IsControl) || !Path.IsPathFullyQualified(directory))
        {
            return null;
        }

        return kind switch
        {
            ProviderIds.Claude => new AccountLoginCommand(
                ClaudeArguments,
                CreateEnvironment(("CLAUDE_SECURESTORAGE_CONFIG_DIR", null), ("CLAUDE_CONFIG_DIR", directory))),
            ProviderIds.Codex => new AccountLoginCommand(CodexArguments, CreateEnvironment(("CODEX_HOME", directory))),
            _ => null,
        };
    }

    // Windows environment variable names ignore case.
    private static IReadOnlyDictionary<string, string?> CreateEnvironment(params (string Name, string? Value)[] variables) =>
        variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase).AsReadOnly();
}
