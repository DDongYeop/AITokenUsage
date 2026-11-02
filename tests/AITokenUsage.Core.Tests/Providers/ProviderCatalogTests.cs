using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void Creates_all_providers_in_spec_order()
    {
        var providers = Create();

        Assert.Equal(ProviderIds.All.ToArray(), providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Copilot_entry_exposes_sign_in_members()
    {
        var copilot = Assert.IsType<CopilotUsageProvider>(Create().Single(p => p.Id == ProviderIds.Copilot));

        Assert.Null(copilot.SignedInLogin);
    }

    [Fact]
    public void Every_provider_offers_its_default_line_keys()
    {
        foreach (var provider in Create())
        {
            var (line1, line2) = ProviderDefaults.DefaultLines(provider.Id);
            var keys = provider.StaticMeterOptions.Select(o => o.Key).ToList();

            Assert.Contains(line1, keys);
            Assert.Contains(line2, keys);
        }
    }

    [Fact]
    public void Every_provider_has_a_name_and_a_minimum_interval()
    {
        foreach (var provider in Create())
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));
            Assert.True(provider.MinInterval >= TimeSpan.FromMinutes(1), provider.Id);
            Assert.NotNull(provider.WatchPaths);
        }
    }

    [Fact]
    public void Accounts_follow_the_four_defaults_in_the_given_order()
    {
        var providers = CreateWith(["codex#b1b1b1", "claude#a1a1a1", "codex#c1c1c1"]);

        Assert.Equal(
            [.. ProviderIds.All, "codex#b1b1b1", "claude#a1a1a1", "codex#c1c1c1"],
            providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Account_ids_create_the_matching_provider_kind()
    {
        var providers = CreateWith(["claude#a1a1a1", "codex#b1b1b1"]);

        var claude = Assert.IsType<ClaudeUsageProvider>(providers.Single(p => p.Id == "claude#a1a1a1"));
        var codex = Assert.IsType<CodexUsageProvider>(providers.Single(p => p.Id == "codex#b1b1b1"));
        Assert.True(codex.IsAccount);
        Assert.Equal([Path.Combine(AccountsDir, "codex-b1b1b1", "auth.json")], codex.WatchPaths);
        Assert.NotNull(claude);
    }

    [Fact]
    public void Invalid_and_duplicate_account_ids_are_skipped()
    {
        var providers = CreateWith(["gemini#a1a1a1", "codex", "codex#short", "codex#b1b1b1", "codex#b1b1b1", "claude", "CODEX#d1d1d1"]);

        Assert.Equal([.. ProviderIds.All, "codex#b1b1b1"], providers.Select(p => p.Id).ToArray());
    }

    private const string AccountsDir = @"C:\app\accounts";

    private static IReadOnlyList<IUsageProvider> CreateWith(IEnumerable<string> accountIds) =>
        ProviderCatalog.CreateAll(TestContexts.Create(new StubHttpHandler(), new FakeTimeProvider()), accountIds, AccountsDir);

    private static IReadOnlyList<IUsageProvider> Create() =>
        ProviderCatalog.CreateAll(TestContexts.Create(new StubHttpHandler(), new FakeTimeProvider()));
}
