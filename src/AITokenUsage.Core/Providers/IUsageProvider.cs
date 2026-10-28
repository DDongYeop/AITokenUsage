using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers;

public interface IUsageProvider
{
    /// <summary>One of <see cref="ProviderIds"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The shortest polling interval this provider tolerates.</summary>
    TimeSpan MinInterval { get; }

    /// <summary>Absolute paths (files or directories) whose changes trigger an immediate refresh.</summary>
    IReadOnlyList<string> WatchPaths { get; }

    /// <summary>Default entries of the settings drop-down. Dynamic entries come from snapshot meters.</summary>
    IReadOnlyList<MeterOption> StaticMeterOptions { get; }

    /// <summary>Returns a snapshot whose status describes expected failures instead of throwing.</summary>
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
}
