using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;
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

    private static IReadOnlyList<IUsageProvider> Create() =>
        ProviderCatalog.CreateAll(TestContexts.Create(new StubHttpHandler(), new FakeTimeProvider()));
}
