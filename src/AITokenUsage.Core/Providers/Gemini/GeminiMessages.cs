namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>User-facing (Korean) status messages for the Gemini provider.</summary>
public static class GeminiMessages
{
    public const string ApiKeyMode = "API 키/Vertex — 사용량 조회 불가";
    public const string NotInstalled = "Gemini CLI 로그인 정보 없음 — 터미널에서 gemini 실행 후 로그인";
    public const string SignInAgain = "터미널에서 gemini 실행해 다시 로그인";
    public const string CredentialsUnreadable = "자격 증명 파일을 읽지 못함 — 잠시 후 다시 시도";
    public const string RefreshFailed = "토큰 갱신 실패 — 잠시 후 다시 시도";
    public const string ClientUnavailable = "gemini-cli 설치를 찾지 못해 로그인을 갱신할 수 없음 — 터미널에서 gemini 실행";
    public const string ConsumerShutdown = "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용";
    public const string NoLicense = "Code Assist 라이선스 없음 — 사용량 조회 불가";
    public const string ProjectRequired = "GOOGLE_CLOUD_PROJECT 설정 필요";
    public const string NotOnboarded = "터미널에서 gemini를 한 번 실행해 설정을 마치세요";
    public const string RateLimited = "요청 제한 — 잠시 후 다시 시도";
    public const string Network = "네트워크 오류 — 잠시 후 다시 시도";
    public const string BadResponse = "응답 형식을 해석할 수 없음";
    public const string NoQuotaData = "사용량 정보가 비어 있음";

    public static string ServerError(int httpStatus) => $"Gemini 서버 오류 (HTTP {httpStatus})";
}
