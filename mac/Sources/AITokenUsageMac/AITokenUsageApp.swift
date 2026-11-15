import AppKit
import SwiftUI

/// macOS menu bar app: the Mac counterpart of the Windows taskbar widget.
@main
struct AITokenUsageApp: App {
    @StateObject private var client = AgentClient()

    init() {
        PreviewRenderer.runIfRequested()
        // A menu bar-only app even when started as a bare binary (swift run); the bundle sets LSUIElement too.
        NSApplication.shared.setActivationPolicy(.accessory)
    }

    var body: some Scene {
        MenuBarExtra {
            PopoverView(client: client)
        } label: {
            MenuBarLabel(client: client)
        }
        .menuBarExtraStyle(.window)
    }
}
