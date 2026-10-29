using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Copilot;

public enum DeviceFlowError { Expired, Denied, Disabled, Network, Unknown }

public sealed class DeviceFlowException(DeviceFlowError error, Exception? innerException = null)
    : Exception($"GitHub device flow failed: {error}", innerException)
{
    public DeviceFlowError Error { get; } = error;

    /// <summary>Localized text for the settings window.</summary>
    public string UserMessage => Error switch
    {
        DeviceFlowError.Expired => Strings.CopilotDeviceExpired,
        DeviceFlowError.Denied => Strings.CopilotDeviceDenied,
        DeviceFlowError.Disabled => Strings.CopilotDeviceDisabled,
        DeviceFlowError.Network => Strings.MessageNetworkRetry,
        _ => Strings.CopilotDeviceUnknown,
    };
}
