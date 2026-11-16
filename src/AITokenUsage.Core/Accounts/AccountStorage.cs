namespace AITokenUsage.Core.Accounts;

/// <summary>
/// Where an extra account keeps its files inside the app folder, and which leftovers belong to no account any more (spec §6).
/// Pure name arithmetic: nothing here touches the disk.
/// </summary>
public static class AccountStorage
{
    /// <summary>Claude accounts: the CLAUDE_CONFIG_DIR folder name is this prefix + the key ("claude-k3x9q2").</summary>
    public const string ClaudeHomePrefix = "claude-";

    /// <summary>
    /// Claude accounts of 1.3.0 kept their login as a secret named this prefix + the key ("claude-account-k3x9q2").
    /// Nothing reads those any more; the startup cleanup removes them.
    /// </summary>
    public const string ClaudeSecretPrefix = "claude-account-";

    /// <summary>Codex accounts: the CODEX_HOME folder name is this prefix + the key ("codex-a1b2c3").</summary>
    public const string CodexHomePrefix = "codex-";

    public static string ClaudeHomeName(string accountId) => ClaudeHomePrefix + AccountIds.KeyOf(accountId);

    public static string CodexHomeName(string accountId) => CodexHomePrefix + AccountIds.KeyOf(accountId);

    /// <summary>
    /// Names (without extension) from <paramref name="secretNames"/> that start with <see cref="ClaudeSecretPrefix"/>
    /// followed by a valid key (<see cref="AccountIds.KeyLength"/> characters of [a-z0-9]), whether or not the account is
    /// still in the settings (spec 2026-11-16 §2.5). Input order is kept. A name with any other tail is never returned.
    /// </summary>
    public static IReadOnlyList<string> LegacyClaudeSecrets(IEnumerable<string> secretNames)
    {
        ArgumentNullException.ThrowIfNull(secretNames);
        return secretNames.Where(name => HasKeyAfter(name, ClaudeSecretPrefix)).ToList();
    }

    /// <summary>
    /// Folder names from <paramref name="directoryNames"/> that start with <see cref="ClaudeHomePrefix"/>
    /// followed by a valid key (<see cref="AccountIds.KeyLength"/> characters of [a-z0-9]) that belongs to no claude account
    /// in <paramref name="settingsIds"/>. Input order is kept. A name with any other tail ("claude-notes") is never an orphan.
    /// </summary>
    public static IReadOnlyList<string> OrphanClaudeHomes(IEnumerable<string> directoryNames, IEnumerable<string> settingsIds) =>
        Orphans(directoryNames, settingsIds, ClaudeHomePrefix, "claude");

    /// <summary>
    /// Folder names from <paramref name="directoryNames"/> that start with <see cref="CodexHomePrefix"/>
    /// followed by a valid key (<see cref="AccountIds.KeyLength"/> characters of [a-z0-9]) that belongs to no codex account
    /// in <paramref name="settingsIds"/>. Input order is kept. A name with any other tail ("codex-notes") is never an orphan.
    /// </summary>
    public static IReadOnlyList<string> OrphanCodexHomes(IEnumerable<string> directoryNames, IEnumerable<string> settingsIds) =>
        Orphans(directoryNames, settingsIds, CodexHomePrefix, "codex");

    private static List<string> Orphans(IEnumerable<string> names, IEnumerable<string> settingsIds, string prefix, string kind)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(settingsIds);

        // The same key under another kind does not keep this kind's files alive; file systems ignore case.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in settingsIds)
        {
            if (AccountIds.IsAccountId(id) && id.StartsWith(kind + AccountIds.Separator, StringComparison.Ordinal))
            {
                keys.Add(AccountIds.KeyOf(id));
            }
        }

        return names
            .Where(name => HasKeyAfter(name, prefix) && !keys.Contains(name[prefix.Length..]))
            .ToList();
    }

    private static bool HasKeyAfter(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) && IsValidKey(name.AsSpan(prefix.Length));

    /// <summary>The same shape as the key of an account id: <see cref="AccountIds.KeyLength"/> characters of [a-z0-9].</summary>
    private static bool IsValidKey(ReadOnlySpan<char> key)
    {
        if (key.Length != AccountIds.KeyLength)
        {
            return false;
        }

        foreach (char c in key)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }
}
