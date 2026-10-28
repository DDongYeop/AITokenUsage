namespace AITokenUsage.Core.Models;

/// <summary>What the UI renders: the latest status combined with the last successful snapshot.</summary>
public sealed record ProviderView(string ProviderId, string DisplayName, UsageSnapshot Current, UsageSnapshot? LastGood)
{
    public bool IsStale => Current.Status != ProviderStatus.Ok && LastGood is not null;

    public IReadOnlyList<UsageMeter> DisplayMeters =>
        Current.Status == ProviderStatus.Ok ? Current.Meters : (LastGood?.Meters ?? Current.Meters);

    public string? DisplayPlan => Current.PlanLabel ?? LastGood?.PlanLabel;

    public bool HasAnyData => DisplayMeters.Count > 0;
}
