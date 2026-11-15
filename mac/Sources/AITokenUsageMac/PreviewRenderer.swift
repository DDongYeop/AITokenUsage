import AppKit
import SwiftUI

/// `AITokenUsage --render-preview <state.json> <out-dir>`: renders the menu bar label (every panel) and the window
/// to PNG files from one agent state line (`aitokenusage-agent --once`). For checks and README screenshots.
@MainActor
enum PreviewRenderer {
    static func runIfRequested() {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: "--render-preview"), index + 2 < args.count else { return }
        let input = URL(fileURLWithPath: args[index + 1])
        let outDir = URL(fileURLWithPath: args[index + 2])

        do {
            let state = try JSONDecoder().decode(AgentState.self, from: Data(contentsOf: input))
            try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
            let client = AgentClient(autoStart: false)
            client.load(state)

            for (i, panel) in client.rotatingPanels.enumerated() {
                while client.currentPanel?.id != panel.id { client.showNext() }
                for dark in [false, true] {
                    let bar = HStack { MenuBarLabel(client: client).foregroundStyle(dark ? Color.white : Color.black) }
                        .padding(.horizontal, 8)
                        .frame(height: 24)
                        .background(dark ? Color(white: 0.16) : Color(white: 0.93))
                        .environment(\.colorScheme, dark ? .dark : .light)
                    try write(bar, to: outDir.appendingPathComponent("menubar-\(i)-\(panel.id)-\(dark ? "dark" : "light").png"))
                }
            }

            for dark in [false, true] {
                let window = PopoverView(client: client)
                    .background(dark ? Color(white: 0.18) : Color(white: 0.97))
                    .environment(\.colorScheme, dark ? .dark : .light)
                try write(window, to: outDir.appendingPathComponent("window-\(dark ? "dark" : "light").png"))
            }
            print("rendered to \(outDir.path)")
            exit(0)
        } catch {
            FileHandle.standardError.write("render-preview failed: \(error)\n".data(using: .utf8)!)
            exit(1)
        }
    }

    private static func write<V: View>(_ view: V, to url: URL) throws {
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let image = renderer.cgImage else { throw CocoaError(.fileWriteUnknown) }
        let rep = NSBitmapImageRep(cgImage: image)
        guard let png = rep.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
        try png.write(to: url)
    }
}
