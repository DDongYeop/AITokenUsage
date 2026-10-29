using System.Globalization;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>User-facing status messages for the Gemini provider, in the current UI language.</summary>
public static class GeminiMessages
{
    public static string ApiKeyMode => Strings.GeminiApiKeyMode;
    public static string NotInstalled => Strings.GeminiNotInstalled;
    public static string SignInAgain => Strings.GeminiSignInAgain;
    public static string CredentialsUnreadable => Strings.MessageCredentialsUnreadable;
    public static string RefreshFailed => Strings.GeminiRefreshFailed;
    public static string ClientUnavailable => Strings.GeminiClientUnavailable;

    /// <summary>Contains <see cref="Strings.StatusDiscontinuedMarker"/> in every language (WidgetPanelBuilder relies on it).</summary>
    public static string ConsumerShutdown => Strings.GeminiConsumerShutdown;

    public static string NoLicense => Strings.GeminiNoLicense;
    public static string ProjectRequired => Strings.GeminiProjectRequired;
    public static string NotOnboarded => Strings.GeminiNotOnboarded;
    public static string RateLimited => Strings.MessageRateLimitedRetry;
    public static string Network => Strings.MessageNetworkRetry;
    public static string BadResponse => Strings.MessageBadResponse;
    public static string NoQuotaData => Strings.GeminiNoQuotaData;

    public static string ServerError(int httpStatus) =>
        string.Format(CultureInfo.CurrentCulture, Strings.GeminiServerErrorFormat, httpStatus);
}
