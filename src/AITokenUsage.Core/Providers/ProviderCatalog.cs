using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Providers.Gemini;

namespace AITokenUsage.Core.Providers;

public static class ProviderCatalog
{
    /// <summary>All providers in default display order: claude, codex, gemini, copilot.</summary>
    public static IReadOnlyList<IUsageProvider> CreateAll(ProviderContext ctx) =>
        CreateAll(ctx, [], string.Empty);

    /// <summary>
    /// The four built-in providers, then one provider per extra account in <paramref name="accountIds"/> order.
    /// Ids that are not account ids and repeated ids are skipped. Accounts keep their folders (CLAUDE_CONFIG_DIR,
    /// CODEX_HOME) under <paramref name="accountsDirectory"/>, which must be non-empty when account ids are given.
    /// </summary>
    public static IReadOnlyList<IUsageProvider> CreateAll(ProviderContext ctx, IEnumerable<string> accountIds, string accountsDirectory)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var providers = new List<IUsageProvider>
        {
            new ClaudeUsageProvider(ctx),
            new CodexUsageProvider(ctx),
            new GeminiUsageProvider(ctx),
            new CopilotUsageProvider(ctx),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in accountIds)
        {
            if (!AccountIds.IsAccountId(id) || !seen.Add(id)) continue;
            providers.Add(ProviderIds.KindOf(id) == ProviderIds.Claude
                ? ClaudeUsageProvider.ForAccount(ctx, id, accountsDirectory)
                : CodexUsageProvider.ForAccount(ctx, id, accountsDirectory));
        }
        return providers;
    }
}
