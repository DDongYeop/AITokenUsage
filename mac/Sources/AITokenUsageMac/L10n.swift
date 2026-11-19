import Foundation

/// The Mac app's labels, taken from the Windows app's UiStrings(.ko).resx so both read the same.
/// Provider texts (statuses, meter names, guide steps) come localized from the agent.
enum L10n {
    enum Key {
        case appTitle, settingsTitle, menuSettings, menuRefresh, menuPauseRotation, menuQuit
        case navProviders, navWidget, navRotation, navGeneral, navAbout
        case on, off
        case providersDescription, showOnWidget, line1, line2, moveUp, moveDown
        case copy, copied, signInTerminal, openTerminal, recheck, terminalFailed
        case githubSignIn, githubSignOut, cancel, openBrowser, enterCodeFormat, verifying, waitingForCode, unofficialApi
        case valueMode, valueUsed, valueRemaining, showResetTime, showResetTimeDescription
        case iconStyle, iconStyleDescription, iconClassic, iconService, iconCustom, iconChoose, iconRemove, iconError, iconFormats
        case rotationEnabled, rotationInterval, secondsFormat, pauseOnOpen, skipUnavailable
        case language, languageAuto, restartNotice, restartNow, refreshInterval, minutesFormat, launchAtLogin, launchAtLoginNeedsApp
        case versionFormat, unofficialApiHeader, unofficialApiDescription, logs, openLogFolder, settingsFile, settingsFileFormat
        case agentMissing, waiting, noProviders
    }

    static func text(_ key: Key, language: String) -> String {
        let pair = strings(key)
        return language == "ko" ? pair.0 : pair.1
    }

    static func format(_ key: Key, language: String, _ argument: CVarArg) -> String {
        String(format: text(key, language: language), argument)
    }

    private static func strings(_ key: Key) -> (String, String) {
        switch key {
        case .appTitle: return ("AI 사용량", "AI Usage")
        case .settingsTitle: return ("AI 사용량 설정", "AI Usage Settings")
        case .menuSettings: return ("설정…", "Settings…")
        case .menuRefresh: return ("지금 새로고침", "Refresh now")
        case .menuPauseRotation: return ("자동 전환 일시정지", "Pause rotation")
        case .menuQuit: return ("종료", "Quit")
        case .navProviders: return ("AI 연결", "AI connections")
        case .navWidget: return ("위젯 표시", "Widget")
        case .navRotation: return ("자동 전환", "Rotation")
        case .navGeneral: return ("일반", "General")
        case .navAbout: return ("정보", "About")
        case .on: return ("켬", "On")
        case .off: return ("끔", "Off")
        case .providersDescription: return ("위젯에 표시할 AI와 순서를 고르세요", "Choose which AIs the widget shows and in what order")
        case .showOnWidget: return ("위젯에 표시", "Show on widget")
        case .line1: return ("1줄 표시", "Line 1")
        case .line2: return ("2줄 표시", "Line 2")
        case .moveUp: return ("위로 이동", "Move up")
        case .moveDown: return ("아래로 이동", "Move down")
        case .copy: return ("복사", "Copy")
        case .copied: return ("복사됨", "Copied")
        case .signInTerminal: return ("터미널에서 로그인", "Sign in from terminal")
        case .openTerminal: return ("터미널 열기", "Open terminal")
        case .recheck: return ("다시 확인", "Check again")
        case .terminalFailed: return ("터미널을 열지 못했어요. 명령을 복사해 직접 실행해 주세요", "Couldn't open a terminal. Copy the command and run it yourself")
        case .githubSignIn: return ("GitHub 로그인", "Sign in to GitHub")
        case .githubSignOut: return ("로그아웃", "Sign out")
        case .cancel: return ("취소", "Cancel")
        case .openBrowser: return ("브라우저 열기", "Open browser")
        case .enterCodeFormat: return ("브라우저에서 %@ 페이지를 열고 아래 코드를 입력하세요.", "Open %@ in your browser and enter the code below.")
        case .verifying: return ("GitHub 계정을 확인하는 중…", "Verifying your GitHub account…")
        case .waitingForCode: return ("GitHub 로그인 코드를 받는 중…", "Requesting a GitHub sign-in code…")
        case .unofficialApi: return ("비공식 API를 사용합니다", "Uses an unofficial API")
        case .iconStyle: return ("AI 아이콘 스타일", "Provider icon style")
        case .iconStyleDescription: return ("서비스는 이 기기에 설치된 앱의 아이콘을 사용해요. 아이콘을 찾을 수 없으면 클래식으로 표시해요.", "Service uses an icon from an app installed on this device. Classic appears if no icon is available.")
        case .iconClassic: return ("클래식", "Classic")
        case .iconService: return ("서비스", "Service")
        case .iconCustom: return ("사용자 지정", "Custom")
        case .iconFormats: return ("PNG, JPG/JPEG, BMP, GIF(첫 프레임), WebP, ICO", "PNG, JPG/JPEG, BMP, GIF (first frame), WebP or ICO")
        case .iconChoose: return ("이미지 선택…", "Choose image…")
        case .iconRemove: return ("삭제", "Remove")
        case .iconError: return ("사용자 지정 아이콘을 변경할 수 없어요.", "Couldn’t update the custom icon.")
        case .valueMode: return ("수치", "Values")
        case .valueUsed: return ("사용한 %", "Used %")
        case .valueRemaining: return ("남은 %", "Remaining %")
        case .showResetTime: return ("초기화까지 남은 시간", "Time until reset")
        case .showResetTimeDescription: return ("끄면 위젯 폭이 줄어요", "Turn off to make the widget narrower")
        case .rotationEnabled: return ("여러 AI 자동으로 넘기기", "Rotate between AIs automatically")
        case .rotationInterval: return ("간격", "Interval")
        case .secondsFormat: return ("%d초", "%d s")
        case .pauseOnOpen: return ("상세 카드를 열면 멈춤", "Pause while the card is open")
        case .skipUnavailable: return ("연결 안 된 AI 건너뛰기", "Skip AIs that aren't connected")
        case .language: return ("언어", "Language")
        case .languageAuto: return ("자동", "Automatic")
        case .restartNotice: return ("다시 시작하면 적용돼요", "Applies after a restart")
        case .restartNow: return ("지금 다시 시작", "Restart now")
        case .refreshInterval: return ("새로고침 간격", "Refresh interval")
        case .minutesFormat: return ("%d분", "%d min")
        case .launchAtLogin: return ("로그인 시 자동 실행", "Open at login")
        case .launchAtLoginNeedsApp: return ("응용 프로그램 폴더의 앱으로 실행할 때 쓸 수 있어요", "Available when run as an app")
        case .versionFormat: return ("버전 %@", "Version %@")
        case .unofficialApiHeader: return ("비공식 API를 사용합니다", "Uses unofficial APIs")
        case .unofficialApiDescription: return ("각 AI 서비스의 비공식 API로 사용량을 읽어요. 서비스가 바뀌면 동작이 멈출 수 있어요.", "Usage is read through unofficial endpoints of each AI service, so it can stop working when a service changes.")
        case .logs: return ("로그", "Logs")
        case .openLogFolder: return ("로그 폴더 열기", "Open log folder")
        case .settingsFile: return ("설정 파일", "Settings file")
        case .settingsFileFormat: return ("변경 사항은 바로 적용되고 저장돼요 · %@", "Changes apply and save immediately · %@")
        case .agentMissing: return ("에이전트를 찾을 수 없습니다. tools/build-mac-app.sh로 앱을 빌드하세요.", "Agent not found. Build the app with tools/build-mac-app.sh.")
        case .waiting: return ("불러오는 중…", "Loading…")
        case .noProviders: return ("표시할 AI가 없습니다. 설정에서 켜 주세요.", "No AI is enabled. Turn one on in Settings.")
        }
    }
}
