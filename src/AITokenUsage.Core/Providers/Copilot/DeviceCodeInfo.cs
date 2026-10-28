namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>Result of POST /login/device/code. Show <see cref="UserCode"/> and open <see cref="VerificationUri"/>.</summary>
public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval)
{
    // DeviceCode is a bearer-like secret for the polling step; keep it out of logs.
    public override string ToString() => $"DeviceCodeInfo {{ UserCode = {UserCode}, VerificationUri = {VerificationUri} }}";
}
