using System.Text;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Resolves Claude Code locations exactly like Claude Code 2.1.311 does
/// (docs/research/claude-usage-api.md §3.1).
/// </summary>
public static class ClaudePaths
{
    public const string CredentialsFileName = ".credentials.json";
    public const string SecureStorageDirVariable = "CLAUDE_SECURESTORAGE_CONFIG_DIR";
    public const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";

    public static string DefaultStoreDirectory(IEnvironment env) => Path.Combine(env.UserProfile, ".claude");

    public static string ResolveStoreDirectory(IEnvironment env)
    {
        string? secure = env.GetVariable(SecureStorageDirVariable);
        if (secure is not null)
        {
            // Defined but empty pins the default store, even when CLAUDE_CONFIG_DIR is set.
            return secure.Length == 0 ? DefaultStoreDirectory(env) : secure.Normalize(NormalizationForm.FormC);
        }

        string? configDir = env.GetVariable(ConfigDirVariable);
        return string.IsNullOrEmpty(configDir)
            ? DefaultStoreDirectory(env)
            : configDir.Normalize(NormalizationForm.FormC);
    }

    public static string ResolveCredentialsPath(IEnvironment env) =>
        Path.Combine(ResolveStoreDirectory(env), CredentialsFileName);

    public static string VsCodeExtensionsDirectory(IEnvironment env) =>
        Path.Combine(env.UserProfile, ".vscode", "extensions");
}
