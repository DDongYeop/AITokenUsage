import AppKit
import SwiftUI

/// The menu bar counterpart of the Windows taskbar widget: the AI's name and two mini bars
/// ("5h ▬▬ 44% 4h12m" / "주 ▬▬ 82% 2d3h"), rendered to a template image so it follows the menu bar's
/// light/dark appearance.
struct MenuBarLabel: View {
    @ObservedObject var client: AgentClient

    var body: some View {
        if let image = renderedImage() {
            Image(nsImage: image)
        } else {
            Text(fallbackText)
        }
    }

    private var fallbackText: String {
        guard let panel = client.currentPanel else { return "AI" }
        return "\(panel.shortName) \(panel.row1.value)"
    }

    @MainActor
    private func renderedImage() -> NSImage? {
        let content: AnyView
        if let panel = client.currentPanel {
            content = AnyView(WidgetContent(panel: panel))
        } else {
            let language = client.state?.language ?? AgentClient.systemLanguage
            content = AnyView(
                Text(client.lastError == nil ? L10n.text(.appTitle, language: language) : "AI ⚠︎")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(.black)
            )
        }

        let renderer = ImageRenderer(content: content.frame(height: 22).fixedSize())
        renderer.scale = NSScreen.main?.backingScaleFactor ?? 2
        guard let image = renderer.nsImage else { return nil }
        image.isTemplate = true
        return image
    }
}

/// Drawn in black only: the image is a template, so alpha is all that matters.
private struct WidgetContent: View {
    let panel: PanelState

    var body: some View {
        HStack(spacing: 5) {
            Text(panel.shortName)
                .font(.system(size: 11, weight: .semibold))
                .lineLimit(1)
                .opacity(panel.isStale ? 0.55 : 1)
            VStack(alignment: .leading, spacing: 1) {
                if let row2 = panel.row2 {
                    RowView(row: panel.row1)
                    RowView(row: row2)
                } else {
                    RowView(row: panel.row1)
                }
            }
        }
        .foregroundColor(.black)
        .padding(.horizontal, 1)
    }
}

private struct RowView: View {
    let row: RowState

    var body: some View {
        HStack(spacing: 3) {
            if !row.label.isEmpty {
                Text(row.label)
                    .frame(minWidth: 12, alignment: .leading)
            }
            if row.kind == "meter" {
                MiniBar(fraction: row.fraction ?? 0, severity: row.severity)
                Text(row.value + (row.severity == "critical" ? "!" : ""))
                    .monospacedDigit()
                    .frame(minWidth: 26, alignment: .trailing)
                if !row.resetText.isEmpty {
                    Text(row.resetText)
                        .monospacedDigit()
                        .opacity(0.7)
                }
            } else {
                Text(row.value)
                    .opacity(row.kind == "message" ? 0.7 : 1)
            }
        }
        .font(.system(size: 9, weight: .medium))
        .lineLimit(1)
        .opacity(row.dimmed ? 0.55 : 1)
    }
}

private struct MiniBar: View {
    let fraction: Double
    let severity: String

    var body: some View {
        ZStack(alignment: .leading) {
            Capsule().opacity(0.28)
            Capsule()
                .frame(width: max(0, min(1, fraction)) * 30)
        }
        .frame(width: 30, height: 4)
    }
}
