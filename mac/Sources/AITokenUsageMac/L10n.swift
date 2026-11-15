import Foundation

/// The Mac app's own labels (provider texts come localized from the agent).
enum L10n {
    enum Key {
        case appTitle, refresh, quit, settings, providers, valueMode, used, remaining, rotation, rotationOff
        case refreshInterval, minutes, seconds, language, auto, launchAtLogin, openLogs, openSettingsFile
        case agentMissing, waiting, noProviders, plan, launchAtLoginNeedsApp
        case githubSignIn, githubSignOut, cancel, copyCode, openBrowser, enterCode, verifying, requestingCode
    }

    static func text(_ key: Key, language: String) -> String {
        let ko = language == "ko"
        switch key {
        case .appTitle: return ko ? "AI 사용량" : "AI Usage"
        case .refresh: return ko ? "지금 새로고침" : "Refresh now"
        case .quit: return ko ? "종료" : "Quit"
        case .settings: return ko ? "설정" : "Settings"
        case .providers: return ko ? "표시할 AI" : "AIs to show"
        case .valueMode: return ko ? "수치" : "Value"
        case .used: return ko ? "사용한 %" : "Used %"
        case .remaining: return ko ? "남은 %" : "Remaining %"
        case .rotation: return ko ? "자동 전환" : "Rotate"
        case .rotationOff: return ko ? "끄기" : "Off"
        case .refreshInterval: return ko ? "새로고침 간격" : "Refresh every"
        case .minutes: return ko ? "분" : " min"
        case .seconds: return ko ? "초" : " s"
        case .language: return ko ? "언어" : "Language"
        case .auto: return ko ? "자동" : "Auto"
        case .launchAtLogin: return ko ? "로그인 시 자동 실행" : "Open at login"
        case .launchAtLoginNeedsApp: return ko ? "앱 번들로 실행할 때만 쓸 수 있어요" : "Available when run as an .app"
        case .openLogs: return ko ? "로그 폴더 열기" : "Open log folder"
        case .openSettingsFile: return ko ? "설정 파일 보기" : "Show settings file"
        case .agentMissing: return ko ? "에이전트를 찾을 수 없습니다. tools/build-mac-app.sh로 앱을 빌드하세요." : "Agent not found. Build the app with tools/build-mac-app.sh."
        case .waiting: return ko ? "불러오는 중…" : "Loading…"
        case .noProviders: return ko ? "표시할 AI가 없습니다. 설정에서 켜 주세요." : "No AI is enabled. Turn one on in Settings."
        case .plan: return ko ? "플랜" : "Plan"
        case .githubSignIn: return ko ? "GitHub 로그인" : "Sign in to GitHub"
        case .githubSignOut: return ko ? "GitHub 로그아웃" : "Sign out of GitHub"
        case .cancel: return ko ? "취소" : "Cancel"
        case .copyCode: return ko ? "코드 복사" : "Copy code"
        case .openBrowser: return ko ? "브라우저 열기" : "Open browser"
        case .enterCode: return ko ? "브라우저에서 이 코드를 입력하세요" : "Enter this code in the browser"
        case .verifying: return ko ? "계정 확인 중…" : "Verifying account…"
        case .requestingCode: return ko ? "코드 받는 중…" : "Getting a code…"
        }
    }
}
