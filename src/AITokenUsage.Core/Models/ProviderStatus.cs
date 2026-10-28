namespace AITokenUsage.Core.Models;

public enum ProviderStatus
{
    Ok,
    NotInstalled,
    SignedOut,
    WaitingForCliRefresh,
    Unsupported,
    RateLimited,
    Error,
}
