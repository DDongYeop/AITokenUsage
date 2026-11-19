import AppKit
import ServiceManagement
import SwiftUI

/// "AI 사용량 설정": the Windows settings window (src/AITokenUsage/UI/Settings) with the same sidebar pages —
/// AI 연결 · 위젯 표시 · 자동 전환 · 일반 · 정보 — and the same card rows. Changes apply and save immediately.
/// Windows-only rows (monitor, taskbar position, text colour, PC usage, tray icon) are left out.
struct SettingsWindow: View {
    static let id = "settings"

    @ObservedObject var client: AgentClient
    @State private var page: Page = .providers

    enum Page: Hashable, CaseIterable {
        case providers, widget, rotation, general, about

        var key: L10n.Key {
            switch self {
            case .providers: return .navProviders
            case .widget: return .navWidget
            case .rotation: return .navRotation
            case .general: return .navGeneral
            case .about: return .navAbout
            }
        }

        var icon: String {
            switch self {
            case .providers: return "link"
            case .widget: return "display"
            case .rotation: return "arrow.triangle.2.circlepath"
            case .general: return "gearshape"
            case .about: return "info.circle"
            }
        }
    }

    private var language: String { client.state?.language ?? AgentClient.systemLanguage }

    var body: some View {
        NavigationSplitView {
            List(Page.allCases, id: \.self, selection: $page) { page in
                Label(L10n.text(page.key, language: language), systemImage: page.icon)
            }
            .navigationSplitViewColumnWidth(190)
        } detail: {
            ScrollView {
                SettingsPagePreview(client: client, page: page)
                    .padding(20)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(minWidth: 720, minHeight: 520)
        .navigationTitle(L10n.text(.settingsTitle, language: language))
    }
}

/// One page: its title and rows (also rendered by PreviewRenderer).
struct SettingsPagePreview: View {
    @ObservedObject var client: AgentClient
    let page: SettingsWindow.Page

    var body: some View {
        let language = client.state?.language ?? AgentClient.systemLanguage
        VStack(alignment: .leading, spacing: 8) {
            Text(L10n.text(page.key, language: language))
                .font(.system(size: 20, weight: .semibold))
                .padding(.bottom, 4)
            if let state = client.state {
                switch page {
                case .providers: ProvidersPage(client: client, state: state)
                case .widget: WidgetPage(client: client, state: state)
                case .rotation: RotationPage(client: client, state: state)
                case .general: GeneralPage(client: client, state: state)
                case .about: AboutPage(state: state)
                }
            } else {
                Text(client.lastError ?? L10n.text(.waiting, language: language)).foregroundColor(.secondary)
            }
        }
    }
}

// MARK: Building blocks (Windows SettingsCard)

/// A settings row: header and optional description on the left, the control on the right.
struct SettingsCard<Control: View>: View {
    let header: String
    var description: String?
    @ViewBuilder var control: () -> Control

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(header).font(.system(size: 13, weight: .semibold))
                if let description {
                    Text(description).font(.system(size: 11.5)).foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            control()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(minHeight: 56)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.08)))
    }
}

/// A switch followed by "켬"/"끔", like the Windows ToggleSwitch.
struct OnOffToggle: View {
    let isOn: Bool
    let language: String
    let set: (Bool) -> Void

    var body: some View {
        HStack(spacing: 8) {
            Toggle("", isOn: Binding(get: { isOn }, set: set)).toggleStyle(.switch).labelsHidden()
            Text(L10n.text(isOn ? .on : .off, language: language)).font(.system(size: 12)).frame(width: 24, alignment: .leading)
        }
    }
}

// MARK: AI 연결

private struct ProvidersPage: View {
    @ObservedObject var client: AgentClient
    let state: AgentState

    var body: some View {
        Text(L10n.text(.providersDescription, language: state.language))
            .font(.system(size: 12)).foregroundColor(.secondary).padding(.bottom, 4)
        ForEach(Array(state.providers.enumerated()), id: \.element.id) { index, provider in
            ProviderSettingsCard(
                client: client, provider: provider, language: state.language,
                isFirst: index == 0, isLast: index == state.providers.count - 1)
        }
    }
}

private struct ProviderSettingsCard: View {
    @ObservedObject var client: AgentClient
    let provider: ProviderEntry
    let language: String
    let isFirst: Bool
    let isLast: Bool
    @State private var expanded: Bool?

    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }
    /// Expanded by default while there is something to do (a guide), like the Windows card.
    private var isExpanded: Bool { expanded ?? (provider.guide != nil) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 10) {
                ProviderGlyph(kind: provider.kind, size: 16)
                VStack(alignment: .leading, spacing: 2) {
                    Text(provider.title).font(.system(size: 13, weight: .semibold))
                    HStack(spacing: 5) {
                        Circle().fill(Theme.statusDot(provider.status)).frame(width: 7, height: 7)
                        Text(provider.statusText).font(.system(size: 11.5)).foregroundColor(.secondary).lineLimit(1)
                    }
                }
                Spacer(minLength: 8)
                Text(t(.showOnWidget)).font(.system(size: 12))
                OnOffToggle(isOn: provider.enabled, language: language) { client.setEnabled(provider.id, $0) }
                Button { expanded = !isExpanded } label: {
                    Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                }
                .buttonStyle(.borderless)
                .accessibilityLabel(L10n.text(.navWidget, language: language))
            }

            if isExpanded {
                if let guide = provider.guide {
                    GuideView(client: client, provider: provider, guide: guide, language: language)
                }
                if provider.kind == "copilot" {
                    CopilotSignInView(client: client, provider: provider, language: language)
                }
                LineOptions(client: client, provider: provider, language: language, isFirst: isFirst, isLast: isLast)
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.08)))
    }
}

/// Numbered steps: text, then a command box with [복사], or [터미널에서 로그인] / [다시 확인].
private struct GuideView: View {
    @ObservedObject var client: AgentClient
    let provider: ProviderEntry
    let guide: GuideState
    let language: String
    @State private var copiedIndex: Int?
    @State private var terminalFailed = false

    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(guide.steps.enumerated()), id: \.offset) { index, step in
                HStack(alignment: .top, spacing: 10) {
                    Text("\(index + 1)")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundColor(.black)
                        .frame(width: 18, height: 18)
                        .background(Circle().fill(Theme.accent))
                    VStack(alignment: .leading, spacing: 6) {
                        Text(step.text).font(.system(size: 12))
                        if let command = step.command {
                            HStack(spacing: 8) {
                                Text(command)
                                    .font(.system(size: 12, weight: .semibold, design: .monospaced))
                                    .textSelection(.enabled)
                                    .padding(.horizontal, 8).padding(.vertical, 5)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.07)))
                                Button(copiedIndex == index ? t(.copied) : t(.copy)) {
                                    NSPasteboard.general.clearContents()
                                    NSPasteboard.general.setString(command, forType: .string)
                                    copiedIndex = index
                                }
                            }
                        }
                        switch step.action {
                        case "openTerminal":
                            Button(t(.signInTerminal)) {
                                terminalFailed = !AgentClient.openTerminal(running: step.terminalCommand ?? "")
                            }
                        case "recheck":
                            Button(t(.recheck)) { client.recheck(provider.id) }
                        default:
                            EmptyView()
                        }
                    }
                }
            }
            if let footnote = guide.footnote {
                Text(footnote).font(.system(size: 11.5)).foregroundColor(.secondary)
            }
            if terminalFailed {
                Text(t(.terminalFailed)).font(.system(size: 11.5)).foregroundColor(Theme.critical)
            }
        }
        .padding(.leading, 26)
    }
}

/// "1줄 표시" / "2줄 표시" drop-downs and the order buttons.
private struct LineOptions: View {
    @ObservedObject var client: AgentClient
    let provider: ProviderEntry
    let language: String
    let isFirst: Bool
    let isLast: Bool

    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }

    var body: some View {
        HStack(alignment: .bottom, spacing: 12) {
            picker(t(.line1), selection: provider.line1Key) { client.setLines(provider.id, $0, provider.line2Key) }
            picker(t(.line2), selection: provider.line2Key) { client.setLines(provider.id, provider.line1Key, $0) }
            Spacer()
            Button { client.move(provider.id, up: true) } label: { Image(systemName: "arrow.up") }
                .help(t(.moveUp)).accessibilityLabel(t(.moveUp)).disabled(isFirst)
            Button { client.move(provider.id, up: false) } label: { Image(systemName: "arrow.down") }
                .help(t(.moveDown)).accessibilityLabel(t(.moveDown)).disabled(isLast)
        }
        .padding(.leading, 26)
    }

    private func picker(_ title: String, selection: String, set: @escaping (String) -> Void) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 12))
            Picker("", selection: Binding(get: { selection }, set: set)) {
                ForEach(provider.meterOptions, id: \.key) { option in Text(option.label).tag(option.key) }
            }
            .labelsHidden()
            .frame(width: 180)
        }
    }
}

/// GitHub device-flow sign-in (the Windows CopilotSignInPanel).
private struct CopilotSignInView: View {
    @ObservedObject var client: AgentClient
    let provider: ProviderEntry
    let language: String
    @State private var copied = false

    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let flow = client.copilotSignIn {
                switch flow.phase {
                case "code":
                    Text(L10n.format(.enterCodeFormat, language: language, flow.verificationUri ?? "https://github.com/login/device"))
                        .font(.system(size: 12))
                    HStack(spacing: 8) {
                        Text(flow.userCode ?? "")
                            .font(.system(size: 18, weight: .semibold, design: .monospaced))
                            .textSelection(.enabled)
                        Button(copied ? t(.copied) : t(.copy)) {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(flow.userCode ?? "", forType: .string)
                            copied = true
                        }
                        if let uri = flow.verificationUri, let url = URL(string: uri) {
                            Button(t(.openBrowser)) { NSWorkspace.shared.open(url) }
                        }
                        Button(t(.cancel)) { client.cancelCopilotSignIn() }
                    }
                case "verifying":
                    HStack { ProgressView().controlSize(.small); Text(t(.verifying)).font(.system(size: 12)) }
                case "error":
                    Text(flow.message ?? "").font(.system(size: 12)).foregroundColor(Theme.critical)
                    HStack {
                        Button(t(.githubSignIn)) { client.startCopilotSignIn() }
                        Button(t(.cancel)) { client.cancelCopilotSignIn() }
                    }
                default:
                    HStack {
                        ProgressView().controlSize(.small)
                        Text(t(.waitingForCode)).font(.system(size: 12))
                        Button(t(.cancel)) { client.cancelCopilotSignIn() }
                    }
                }
            } else {
                HStack(spacing: 8) {
                    if provider.status == "ok" {
                        Button(t(.githubSignOut)) { client.signOutCopilot() }
                    } else {
                        Button(t(.githubSignIn)) { client.startCopilotSignIn() }.buttonStyle(.borderedProminent)
                    }
                    Text(t(.unofficialApi)).font(.system(size: 11.5)).foregroundColor(.secondary)
                }
            }
        }
        .padding(.leading, 26)
    }
}

// MARK: 위젯 표시

private struct WidgetPage: View {
    @ObservedObject var client: AgentClient
    let state: AgentState
    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: state.language) }

    var body: some View {
        SettingsCard(header: t(.valueMode)) {
            Picker("", selection: Binding(get: { state.valueMode }, set: { client.setValueMode($0) })) {
                Text(t(.valueUsed)).tag("used")
                Text(t(.valueRemaining)).tag("remaining")
            }
            .pickerStyle(.radioGroup)
            .horizontalRadioGroupLayout()
            .labelsHidden()
        }
        SettingsCard(header: t(.textWeight)) {
            Picker(t(.textWeight), selection: Binding(get: { state.textWeight ?? .default }, set: client.setTextWeight)) {
                Text(t(.weightDefault)).tag(WidgetTextWeight.default)
                Text(t(.weightNormal)).tag(WidgetTextWeight.normal)
                Text(t(.weightBold)).tag(WidgetTextWeight.bold)
            }
            .labelsHidden()
        }
        SettingsCard(header: t(.showResetTime), description: t(.showResetTimeDescription)) {
            OnOffToggle(isOn: state.showResetTime, language: state.language) { client.setShowResetTime($0) }
        }
    }
}

// MARK: 자동 전환

private struct RotationPage: View {
    @ObservedObject var client: AgentClient
    let state: AgentState
    @State private var seconds: Double?
    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: state.language) }

    var body: some View {
        SettingsCard(header: t(.rotationEnabled)) {
            OnOffToggle(isOn: state.rotationEnabled, language: state.language) { client.setRotationEnabled($0) }
        }
        SettingsCard(header: t(.rotationInterval)) {
            HStack(spacing: 10) {
                Slider(
                    value: Binding(get: { seconds ?? Double(state.rotationSeconds) }, set: { seconds = $0.rounded() }),
                    in: 2...60, step: 1
                ) { editing in
                    if !editing, let seconds { client.setRotationSeconds(Int(seconds)) }
                }
                .frame(width: 200)
                Text(L10n.format(.secondsFormat, language: state.language, Int(seconds ?? Double(state.rotationSeconds))))
                    .font(.system(size: 12).monospacedDigit())
                    .frame(width: 40, alignment: .leading)
            }
            .disabled(!state.rotationEnabled)
        }
        SettingsCard(header: t(.pauseOnOpen)) {
            OnOffToggle(isOn: state.pauseOnHover, language: state.language) { client.setPauseOnHover($0) }
        }
        SettingsCard(header: t(.skipUnavailable)) {
            OnOffToggle(isOn: state.skipUnavailable, language: state.language) { client.setSkipUnavailable($0) }
        }
    }
}

// MARK: 일반

private struct GeneralPage: View {
    @ObservedObject var client: AgentClient
    let state: AgentState
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: state.language) }
    private var isBundled: Bool { Bundle.main.bundleURL.pathExtension == "app" }

    /// The running language differs from the saved setting: show the Windows "다시 시작하면 적용돼요" notice.
    private var needsRestart: Bool {
        switch state.languageSetting {
        case "ko", "en": return state.languageSetting != state.language
        default: return AgentClient.systemLanguage != state.language
        }
    }

    var body: some View {
        SettingsCard(header: t(.language), description: needsRestart ? t(.restartNotice) : nil) {
            HStack {
                if needsRestart {
                    Button(t(.restartNow)) { client.restartAgent() }
                }
                Picker("", selection: Binding(get: { state.languageSetting }, set: { client.setLanguage($0) })) {
                    Text(t(.languageAuto)).tag("auto")
                    Text("한국어").tag("ko")
                    Text("English").tag("en")
                }
                .labelsHidden()
                .frame(width: 180)
            }
        }
        SettingsCard(header: t(.refreshInterval)) {
            Picker("", selection: Binding(get: { state.refreshMinutes }, set: { client.setRefresh($0) })) {
                ForEach([2, 5, 10, 15], id: \.self) { m in Text(L10n.format(.minutesFormat, language: state.language, m)).tag(m) }
            }
            .labelsHidden()
            .frame(width: 180)
        }
        SettingsCard(header: t(.launchAtLogin), description: isBundled ? nil : t(.launchAtLoginNeedsApp)) {
            OnOffToggle(isOn: launchAtLogin, language: state.language) { enable in
                do {
                    if enable { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                } catch {
                    NSLog("AITokenUsage: launch at login failed: \(error)")
                }
                launchAtLogin = SMAppService.mainApp.status == .enabled
            }
            .disabled(!isBundled)
        }
    }
}

// MARK: 정보

private struct AboutPage: View {
    let state: AgentState
    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: state.language) }

    var body: some View {
        SettingsCard(header: t(.appTitle), description: L10n.format(.versionFormat, language: state.language, state.version)) {
            EmptyView()
        }
        SettingsCard(header: t(.unofficialApiHeader), description: t(.unofficialApiDescription)) { EmptyView() }
        SettingsCard(header: t(.logs), description: state.logDirectory) {
            Button(t(.openLogFolder)) {
                NSWorkspace.shared.open(URL(fileURLWithPath: state.logDirectory, isDirectory: true))
            }
        }
        SettingsCard(header: t(.settingsFile), description: L10n.format(.settingsFileFormat, language: state.language, state.settingsFile)) {
            Button("Finder") {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: state.settingsFile)])
            }
        }
    }
}
