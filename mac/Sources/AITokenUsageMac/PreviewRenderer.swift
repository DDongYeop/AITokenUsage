import AppKit
import SwiftUI

/// `AITokenUsage --render-preview <state.json> <out-dir>`: renders the menu bar widget (every panel, light and dark),
/// the detail card and the settings pages to PNG files from one agent state line (`aitokenusage-agent --once`).
/// For checks and README screenshots; controls drawn by AppKit (buttons, pickers) show as placeholders here.
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
                for dark in [false, true] {
                    let bar = WidgetContent(
                        panel: panel, foreground: dark ? .white : .black, showReset: state.showResetTime,
                        labelFont: MenuBarFont(family: state.widgetFontFamily))
                        .frame(height: 22)
                        .padding(.horizontal, 8)
                        .background(dark ? Color(white: 0.12) : Color(white: 0.93))
                    try write(bar, to: outDir.appendingPathComponent("menubar-\(i)-\(panel.id)-\(dark ? "dark" : "light").png"))
                }
                let card = DetailCard(panel: panel)
                    .frame(width: 364)
                    .padding(8)
                    .background(Color(white: 0.13))
                    .environment(\.colorScheme, .dark)
                try write(card, to: outDir.appendingPathComponent("card-\(i)-\(panel.id).png"))
            }

            for page in SettingsWindow.Page.allCases {
                let view = SettingsPagePreview(client: client, page: page)
                    .frame(width: 560)
                    .padding(20)
                    .background(Color(white: 0.13))
                    .environment(\.colorScheme, .dark)
                try write(view, to: outDir.appendingPathComponent("settings-\(page).png"))
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
