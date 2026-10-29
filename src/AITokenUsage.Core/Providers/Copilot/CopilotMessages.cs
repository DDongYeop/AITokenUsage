using System.Globalization;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>User-facing messages for the Copilot provider and sign-in, in the current UI language.</summary>
public static class CopilotMessages
{
    public static string SignInRequired => Strings.CopilotSignInRequired;
    public static string NoSubscription => Strings.CopilotNoSubscription;
    public static string RateLimited => Strings.MessageRateLimitedRetry;
    public static string Network => Strings.MessageNetworkRetry;
    public static string BadResponse => Strings.MessageBadResponse;
    public static string TokenRejected => Strings.CopilotTokenRejected;
    public static string UnknownLogin => Strings.CopilotUnknownLogin;

    public static string ServerError(int httpStatus) =>
        string.Format(CultureInfo.CurrentCulture, Strings.CopilotServerErrorFormat, httpStatus);
}
