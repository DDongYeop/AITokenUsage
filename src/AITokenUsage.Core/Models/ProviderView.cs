namespace AITokenUsage.Core.Models;

/// <summary>What the UI renders: the latest status combined with the last successful snapshot.</summary>
public sealed record ProviderView(string ProviderId, string DisplayName, UsageSnapshot Current, UsageSnapshot? LastGood)
{
    /// <summary>"팀"/"개인"/"#2" when several panels of the same AI are shown (see AccountTags); null otherwise.</summary>
    public string? AccountTag { get; init; }

    /// <summary>Name for cards, tooltips and lists: "Claude", or "Claude · 팀" when tagged.</summary>
    public string Title => AccountTag is null ? DisplayName : DisplayName + " · " + AccountTag;

    /// <summary>Name for the widget panel: the tag alone when tagged (the icon already tells the AI), else the display name.</summary>
    public string ShortName => AccountTag ?? DisplayName;

    public bool IsStale => Current.Status != ProviderStatus.Ok && LastGood is not null;

    public IReadOnlyList<UsageMeter> DisplayMeters =>
        Current.Status == ProviderStatus.Ok ? Current.Meters : (LastGood?.Meters ?? Current.Meters);

    public string? DisplayPlan => Current.PlanLabel ?? LastGood?.PlanLabel;

    public bool HasAnyData => DisplayMeters.Count > 0;
}
