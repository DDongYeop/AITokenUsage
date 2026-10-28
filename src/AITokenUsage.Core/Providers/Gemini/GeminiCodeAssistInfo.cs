namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>The parts of a loadCodeAssist response the widget needs.</summary>
/// <param name="HasCurrentTier">currentTier object present.</param>
/// <param name="TierId">paidTier.id ?? currentTier.id.</param>
/// <param name="PaidTierName">paidTier.name.</param>
/// <param name="Project">cloudaicompanionProject (string, or object id/projectId).</param>
/// <param name="HasUnsupportedClient">Any ineligibleTiers[].reasonCode/reasonMessage contains UNSUPPORTED_CLIENT.</param>
public sealed record GeminiCodeAssistInfo(
    bool HasCurrentTier,
    string? TierId,
    string? PaidTierName,
    string? Project,
    bool HasUnsupportedClient)
{
    /// <summary>Consumer "Login with Google" shutdown (2026-06-18).</summary>
    public bool IsConsumerShutdown(bool hasHostedDomain) =>
        !HasCurrentTier && HasUnsupportedClient && string.IsNullOrEmpty(PaidTierName) && !hasHostedDomain;

    /// <summary>retrieveUserQuota 403 SUBSCRIPTION_REQUIRED counts as the shutdown only in this case.</summary>
    public bool IsShutdownLicenseDenial(bool hasHostedDomain) =>
        HasUnsupportedClient
        && !string.Equals(TierId, "standard-tier", StringComparison.Ordinal)
        && string.IsNullOrEmpty(PaidTierName)
        && !hasHostedDomain;
}
