namespace AITokenUsage.Core.Providers.Copilot;

public enum DeviceFlowError { Expired, Denied, Disabled, Network, Unknown }

public sealed class DeviceFlowException(DeviceFlowError error, Exception? innerException = null)
    : Exception($"GitHub device flow failed: {error}", innerException)
{
    public DeviceFlowError Error { get; } = error;

    /// <summary>Korean text for the settings window.</summary>
    public string UserMessage => Error switch
    {
        DeviceFlowError.Expired => "코드가 만료되었습니다. 다시 시도하세요",
        DeviceFlowError.Denied => "GitHub에서 로그인을 취소했습니다",
        DeviceFlowError.Disabled => "이 앱의 GitHub 기기 코드 로그인이 비활성화되어 있습니다",
        DeviceFlowError.Network => "네트워크 오류 — 잠시 후 다시 시도",
        _ => "GitHub 로그인 중 알 수 없는 오류가 발생했습니다",
    };
}
