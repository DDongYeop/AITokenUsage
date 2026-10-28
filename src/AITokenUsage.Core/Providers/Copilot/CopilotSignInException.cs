namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>Sign-in failed; <see cref="Exception.Message"/> is a Korean user-facing message (never contains the token).</summary>
public sealed class CopilotSignInException(string message, Exception? innerException = null)
    : Exception(message, innerException);
