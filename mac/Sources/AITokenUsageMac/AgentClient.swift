import AppKit
import Foundation

/// Runs the bundled .NET agent, decodes its state lines and sends it commands. Restarts it when it exits.
@MainActor
final class AgentClient: ObservableObject {
    @Published private(set) var state: AgentState?
    @Published private(set) var lastError: String?
    @Published private(set) var rotationIndex = 0
    @Published private(set) var copilotSignIn: CopilotSignInEvent?
    /// The tray menu's "자동 전환 일시정지".
    @Published var rotationPausedByUser = false

    private var process: Process?
    private var input: FileHandle?
    private var buffer = Data()
    private var restartDelay: TimeInterval = 1
    private var rotationTimer: Timer?
    private var stopping = false
    private var paused = false

    init(autoStart: Bool = true) {
        guard autoStart else { return }
        start()
        NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.stop() }
        }
    }

    // MARK: Panels shown in the menu bar

    /// The panels the menu bar rotates through: every enabled one, minus unavailable ones when "skip" is on
    /// (unless that would leave nothing).
    var rotatingPanels: [PanelState] {
        guard let state else { return [] }
        if state.skipUnavailable {
            let available = state.panels.filter { $0.available }
            if !available.isEmpty { return available }
        }
        return state.panels
    }

    var currentPanel: PanelState? {
        let panels = rotatingPanels
        guard !panels.isEmpty else { return nil }
        return panels[rotationIndex % panels.count]
    }

    func showNext() {
        let count = rotatingPanels.count
        guard count > 0 else { return }
        rotationIndex = (rotationIndex + 1) % count
    }

    func showPrevious() {
        let count = rotatingPanels.count
        guard count > 0 else { return }
        rotationIndex = (rotationIndex + count - 1) % count
    }

    /// Stops rotating while the card is open, when "상세 카드를 열면 멈춤" (PauseOnHover) is on.
    func setPaused(_ value: Bool) { paused = value }

    // MARK: Commands

    func refresh() { send("refresh") }
    func setEnabled(_ id: String, _ enabled: Bool) { send("set-enabled \(id) \(enabled)") }
    func setNickname(_ id: String, _ nickname: String) {
        // Keep a paste containing line breaks inside one agent command.
        let singleLine = nickname.components(separatedBy: .newlines).joined(separator: " ")
        send("set-nickname \(id) \(singleLine)")
    }
    func setValueMode(_ mode: String) { send("set-value-mode \(mode)") }
    func setRotationEnabled(_ value: Bool) { send("set-rotation-enabled \(value)") }
    func setRotationSeconds(_ seconds: Int) { send("set-rotation-seconds \(seconds)") }
    func setSkipUnavailable(_ value: Bool) { send("set-skip-unavailable \(value)") }
    func setPauseOnHover(_ value: Bool) { send("set-pause-on-hover \(value)") }
    func setWidgetFontFamily(_ family: String?) {
        // A single line/token even for font names containing whitespace.
        if let family {
            send("set-widget-font-family \(Data(family.utf8).base64EncodedString())")
        } else {
            send("set-widget-font-family")
        }
    }
    func setShowResetTime(_ value: Bool) { send("set-show-reset-time \(value)") }
    func setRefresh(_ minutes: Int) { send("set-refresh \(minutes)") }
    func setLines(_ id: String, _ line1: String, _ line2: String) { send("set-lines \(id) \(line1) \(line2)") }
    func move(_ id: String, up: Bool) { send("move \(id) \(up ? "up" : "down")") }
    func recheck(_ id: String) { send("refresh \(id)") }

    /// "터미널에서 로그인": opens Terminal.app running the command in a login shell (so npm/Homebrew are on PATH).
    /// A .command file needs no Automation permission, unlike scripting Terminal.
    @discardableResult
    static func openTerminal(running command: String) -> Bool {
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("aitokenusage-\(UUID().uuidString).command")
        let text = "#!/bin/zsh -l\nclear\n\(command)\n"
        do {
            try text.write(to: script, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        } catch {
            return false
        }
        guard let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal") else { return false }
        NSWorkspace.shared.open([script], withApplicationAt: terminal, configuration: NSWorkspace.OpenConfiguration())
        return true
    }

    /// Restarts the agent so a new language applies (Core strings are created in the startup language).
    func restartAgent() {
        process?.terminate()
    }
    func startCopilotSignIn() {
        copilotSignIn = CopilotSignInEvent(phase: "starting")
        send("copilot-signin")
    }
    func cancelCopilotSignIn() {
        send("copilot-cancel")
        copilotSignIn = nil
    }
    func signOutCopilot() { send("copilot-signout") }

    /// Saved now, applied by "지금 다시 시작" (restartAgent), like the Windows General page.
    func setLanguage(_ language: String) { send("set-language \(language)") }

    private func send(_ line: String) {
        guard let input, let data = (line + "\n").data(using: .utf8) else { return }
        try? input.write(contentsOf: data)
    }

    // MARK: Process

    static func agentURL() -> URL? {
        if let custom = ProcessInfo.processInfo.environment["AITOKENUSAGE_AGENT"], !custom.isEmpty {
            return URL(fileURLWithPath: custom)
        }
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent("agent/aitokenusage-agent"),
           FileManager.default.isExecutableFile(atPath: bundled.path) {
            return bundled
        }
        return nil
    }

    private func start() {
        guard let url = Self.agentURL() else {
            lastError = L10n.text(.agentMissing, language: Self.systemLanguage)
            return
        }

        let process = Process()
        process.executableURL = url
        process.arguments = ["--language", Self.systemLanguage]
        let stdinPipe = Pipe()
        let stdoutPipe = Pipe()
        process.standardInput = stdinPipe
        process.standardOutput = stdoutPipe
        process.standardError = FileHandle.nullDevice

        stdoutPipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            DispatchQueue.main.async { self?.receive(data) }
        }
        process.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async { self?.agentExited(status: proc.terminationStatus) }
        }

        do {
            try process.run()
            self.process = process
            self.input = stdinPipe.fileHandleForWriting
            lastError = nil
        } catch {
            lastError = error.localizedDescription
            scheduleRestart()
        }
    }

    func stop() {
        stopping = true
        send("quit")
        process?.terminate()
    }

    private func agentExited(status: Int32) {
        process = nil
        input = nil
        buffer.removeAll()
        guard !stopping else { return }
        scheduleRestart()
    }

    private func scheduleRestart() {
        let delay = restartDelay
        restartDelay = min(restartDelay * 2, 60)
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, !self.stopping, self.process == nil else { return }
            self.start()
        }
    }

    private func receive(_ data: Data) {
        guard !data.isEmpty else { return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = buffer.subdata(in: buffer.startIndex..<newline)
            buffer.removeSubrange(buffer.startIndex...newline)
            guard !line.isEmpty else { continue }
            do {
                let decoder = JSONDecoder()
                switch try decoder.decode(MessageType.self, from: line).type {
                case "state":
                    apply(try decoder.decode(AgentState.self, from: line))
                case "copilotSignIn":
                    handle(try decoder.decode(CopilotSignInEvent.self, from: line))
                default:
                    break
                }
            } catch {
                lastError = "agent: \(error)"
            }
        }
    }

    private func handle(_ event: CopilotSignInEvent) {
        switch event.phase {
        case "code":
            // Open the browser for the first code only; a renewed (expired) code just updates the text.
            if copilotSignIn?.userCode == nil, let uri = event.verificationUri, let url = URL(string: uri) {
                NSWorkspace.shared.open(url)
            }
            copilotSignIn = event
        case "done", "cancelled":
            copilotSignIn = nil
        default:
            copilotSignIn = event
        }
    }

    /// Shows a state without an agent (preview rendering).
    func load(_ newState: AgentState) {
        state = newState
    }

    private func apply(_ newState: AgentState) {
        let rotationChanged = state?.rotationEnabled != newState.rotationEnabled
            || state?.rotationSeconds != newState.rotationSeconds
        state = newState
        restartDelay = 1
        if rotationChanged || rotationTimer == nil { configureRotation() }
    }

    private func configureRotation() {
        rotationTimer?.invalidate()
        rotationTimer = nil
        guard let state, state.rotationEnabled else { return }
        let interval = TimeInterval(max(2, state.rotationSeconds))
        rotationTimer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, !self.paused, !self.rotationPausedByUser else { return }
                self.showNext()
            }
        }
    }

    static var systemLanguage: String {
        let preferred = Locale.preferredLanguages.first ?? "en"
        return preferred.hasPrefix("ko") ? "ko" : "en"
    }
}
