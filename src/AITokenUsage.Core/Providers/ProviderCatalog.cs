using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Providers.Gemini;

namespace AITokenUsage.Core.Providers;

public static class ProviderCatalog
{
    /// <summary>All providers in default display order: claude, codex, gemini, copilot.</summary>
    public static IReadOnlyList<IUsageProvider> CreateAll(ProviderContext ctx) =>
    [
        new ClaudeUsageProvider(ctx),
        new CodexUsageProvider(ctx),
        new GeminiUsageProvider(ctx),
        new CopilotUsageProvider(ctx),
    ];
}
