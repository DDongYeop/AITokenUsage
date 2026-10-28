namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>User-facing (Korean) messages for the Copilot provider and sign-in.</summary>
public static class CopilotMessages
{
    public const string SignInRequired = "설정에서 GitHub 로그인";
    public const string NoSubscription = "Copilot 구독 없음";
    public const string RateLimited = "요청 제한 — 잠시 후 다시 시도";
    public const string Network = "네트워크 오류 — 잠시 후 다시 시도";
    public const string BadResponse = "응답 형식을 해석할 수 없음";
    public const string TokenRejected = "이 GitHub 계정으로는 Copilot 사용량을 조회할 수 없습니다";
    public const string UnknownLogin = "알 수 없는 계정";

    public static string ServerError(int httpStatus) => $"GitHub 응답 오류 (HTTP {httpStatus})";
}
