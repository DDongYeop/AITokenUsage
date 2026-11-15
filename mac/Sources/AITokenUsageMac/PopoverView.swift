import AppKit
import ServiceManagement
import SwiftUI

/// The window under the menu bar item: one detail card per enabled AI (the Windows hover card), then settings.
struct PopoverView: View {
    @ObservedObject var client: AgentClient
    @State private var showSettings = false

    private var language: String { client.state?.language ?? AgentClient.systemLanguage }
    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header

            if let error = client.lastError, client.state == nil {
                Text(error).font(.callout).foregroundColor(.red).fixedSize(horizontal: false, vertical: true)
            } else if let state = client.state {
                if state.panels.isEmpty {
                    Text(t(.noProviders)).foregroundColor(.secondary)
                } else {
                    ForEach(state.panels) { panel in
                        ProviderCard(panel: panel) {
                            if panel.kind == "copilot" {
                                CopilotSignInView(client: client, panel: panel, language: language)
                            }
                        }
                    }
                }
            } else {
                HStack { ProgressView().controlSize(.small); Text(t(.waiting)).foregroundColor(.secondary) }
            }

            if showSettings, let state = client.state {
                Divider()
                SettingsSection(client: client, state: state)
            }

            Divider()
            footer
        }
        .padding(14)
        .frame(width: 340)
        .onAppear { client.setPaused(true) }
        .onDisappear { client.setPaused(false) }
    }

    private var header: some View {
        HStack {
            Text(t(.appTitle)).font(.headline)
            Spacer()
            Button { client.refresh() } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.borderless)
                .help(t(.refresh))
            Button { showSettings.toggle() } label: { Image(systemName: showSettings ? "gearshape.fill" : "gearshape") }
                .buttonStyle(.borderless)
                .help(t(.settings))
        }
    }

    private var footer: some View {
        HStack {
            Button(t(.openLogs)) {
                let logs = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/AITokenUsage")
                NSWorkspace.shared.open(logs)
            }
            .buttonStyle(.borderless)
            Spacer()
            Button(t(.quit)) { NSApplication.shared.terminate(nil) }
                .keyboardShortcut("q")
        }
        .font(.callout)
    }
}

private struct ProviderCard<Accessory: View>: View {
    let panel: PanelState
    @ViewBuilder var accessory: () -> Accessory

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(panel.title).font(.system(size: 13, weight: .semibold))
                if let plan = panel.card.plan {
                    Text(plan).font(.caption).foregroundColor(.secondary)
                }
                Spacer()
            }

            ForEach(panel.card.rows) { row in
                CardRowView(row: row)
            }

            if let extra = panel.card.extraLine {
                Text(extra).font(.caption).foregroundColor(.secondary)
            }

            ForEach(panel.card.statusLines, id: \.self) { line in
                Text(line).font(.caption).foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
            }

            accessory()
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05)))
        .opacity(panel.isStale ? 0.7 : 1)
    }
}

private struct CardRowView: View {
    let row: CardRowState

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text(row.label).font(.callout)
                Spacer()
                Text(row.value)
                    .font(.callout.monospacedDigit().weight(.semibold))
                    .foregroundColor(row.isText ? .primary : SeverityColor.of(row.severity))
            }
            if !row.isText, let fraction = row.fraction {
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Color.primary.opacity(0.12))
                        Capsule().fill(SeverityColor.of(row.severity))
                            .frame(width: geo.size.width * max(0, min(1, fraction)))
                    }
                }
                .frame(height: 6)
            }
            if !row.resetText.isEmpty {
                Text(row.resetText).font(.caption).foregroundColor(.secondary)
            }
        }
        .opacity(row.dimmed ? 0.6 : 1)
    }
}

/// GitHub device-flow sign-in for Copilot (the Windows settings page's "GitHub 로그인").
private struct CopilotSignInView: View {
    @ObservedObject var client: AgentClient
    let panel: PanelState
    let language: String

    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }

    var body: some View {
        if let flow = client.copilotSignIn {
            VStack(alignment: .leading, spacing: 6) {
                switch flow.phase {
                case "code":
                    Text(t(.enterCode)).font(.caption)
                    HStack {
                        Text(flow.userCode ?? "").font(.system(.title3, design: .monospaced).weight(.semibold)).textSelection(.enabled)
                        Spacer()
                        Button(t(.copyCode)) {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(flow.userCode ?? "", forType: .string)
                        }
                        if let uri = flow.verificationUri, let url = URL(string: uri) {
                            Button(t(.openBrowser)) { NSWorkspace.shared.open(url) }
                        }
                    }
                case "error":
                    Text(flow.message ?? "").font(.caption).foregroundColor(.red)
                case "verifying":
                    HStack { ProgressView().controlSize(.small); Text(t(.verifying)).font(.caption) }
                default:
                    HStack { ProgressView().controlSize(.small); Text(t(.requestingCode)).font(.caption) }
                }
                HStack {
                    if flow.phase == "error" {
                        Button(t(.githubSignIn)) { client.startCopilotSignIn() }
                    }
                    Button(t(.cancel)) { client.cancelCopilotSignIn() }
                }
            }
            .font(.callout)
        } else if panel.status == "signedOut" || panel.status == "notInstalled" {
            Button(t(.githubSignIn)) { client.startCopilotSignIn() }
                .font(.callout)
        }
    }
}

enum SeverityColor {
    static func of(_ severity: String) -> Color {
        switch severity {
        case "critical": return .red
        case "warning": return .orange
        default: return .accentColor
        }
    }
}

private struct SettingsSection: View {
    @ObservedObject var client: AgentClient
    let state: AgentState
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: state.language) }
    private var isBundled: Bool { Bundle.main.bundleURL.pathExtension == "app" }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(t(.providers)).font(.subheadline.weight(.semibold))
            ForEach(state.providers) { provider in
                Toggle(provider.title, isOn: Binding(
                    get: { provider.enabled },
                    set: { client.setEnabled(provider.id, $0) }
                ))
            }

            Picker(t(.valueMode), selection: Binding(
                get: { state.valueMode },
                set: { client.setValueMode($0) }
            )) {
                Text(t(.used)).tag("used")
                Text(t(.remaining)).tag("remaining")
            }

            Picker(t(.rotation), selection: Binding(
                get: { state.rotationEnabled ? state.rotationSeconds : 0 },
                set: { client.setRotation($0 == 0 ? nil : $0) }
            )) {
                Text(t(.rotationOff)).tag(0)
                ForEach([3, 5, 10, 15, 30, 60], id: \.self) { s in Text("\(s)\(t(.seconds))").tag(s) }
            }

            Picker(t(.refreshInterval), selection: Binding(
                get: { state.refreshMinutes },
                set: { client.setRefresh($0) }
            )) {
                ForEach([2, 5, 10, 15], id: \.self) { m in Text("\(m)\(t(.minutes))").tag(m) }
            }

            Picker(t(.language), selection: Binding(
                get: { state.languageSetting },
                set: { client.setLanguage($0) }
            )) {
                Text(t(.auto)).tag("auto")
                Text("한국어").tag("ko")
                Text("English").tag("en")
            }

            Toggle(t(.launchAtLogin), isOn: Binding(
                get: { launchAtLogin },
                set: { enable in
                    do {
                        if enable { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                    } catch {
                        NSLog("AITokenUsage: launch at login failed: \(error)")
                    }
                    launchAtLogin = SMAppService.mainApp.status == .enabled
                }
            ))
            .disabled(!isBundled)
            .help(isBundled ? "" : t(.launchAtLoginNeedsApp))
        }
        .font(.callout)
    }
}
