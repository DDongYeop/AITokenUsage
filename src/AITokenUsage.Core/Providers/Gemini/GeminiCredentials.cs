namespace AITokenUsage.Core.Providers.Gemini;

public enum GeminiCredentialSource { OAuthFile, CredentialManager }

public enum GeminiCredentialStatus { Found, NotFound, Unreadable }

public sealed record GeminiCredentials(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? Expiry,
    string? IdToken,
    GeminiCredentialSource Source)
{
    // Never print tokens (records generate a ToString that would include them).
    public override string ToString() => $"GeminiCredentials {{ Source = {Source}, Expiry = {Expiry:O} }}";
}

public sealed record GeminiCredentialResult(GeminiCredentialStatus Status, GeminiCredentials? Credentials);
