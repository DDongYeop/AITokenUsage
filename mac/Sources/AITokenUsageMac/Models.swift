import Foundation

/// One state line written by the .NET agent (src/AITokenUsage.Agent/Program.cs, AgentState).
struct AgentState: Decodable, Equatable {
    var type: String
    var generatedAt: String
    var language: String
    var languageSetting: String
    var valueMode: String
    var rotationEnabled: Bool
    var rotationSeconds: Int
    var skipUnavailable: Bool
    var refreshMinutes: Int
    var panels: [PanelState]
    var providers: [ProviderEntry]
}

struct ProviderEntry: Decodable, Equatable, Identifiable {
    var id: String
    var title: String
    var enabled: Bool
}

struct PanelState: Decodable, Equatable, Identifiable {
    var id: String
    var kind: String
    var title: String
    var shortName: String
    var status: String
    var isStale: Bool
    var available: Bool
    var row1: RowState
    var row2: RowState?
    var card: CardState
}

/// kind: "meter" (label + bar + value), "text" (text line, no bar) or "message" (muted status line).
struct RowState: Decodable, Equatable {
    var kind: String
    var label: String
    var fraction: Double?
    var value: String
    var severity: String
    var dimmed: Bool
    var resetText: String
}

struct CardState: Decodable, Equatable {
    var plan: String?
    var rows: [CardRowState]
    var extraLine: String?
    var statusLines: [String]
}

struct CardRowState: Decodable, Equatable, Identifiable {
    var label: String
    var fraction: Double?
    var value: String
    var resetText: String
    var severity: String
    var isText: Bool
    var dimmed: Bool

    var id: String { label + "|" + value }
}

/// {"type":"copilotSignIn"} lines: phase "code" | "verifying" | "done" | "cancelled" | "error".
struct CopilotSignInEvent: Decodable, Equatable {
    var phase: String
    var userCode: String?
    var verificationUri: String?
    var message: String?
}

struct MessageType: Decodable {
    var type: String
}
