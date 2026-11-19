import AppKit
import SwiftUI

private extension WidgetTextWeight {
    func resolve(_ original: Font.Weight = .regular) -> Font.Weight {
        switch self {
        case .default: return original
        case .normal: return .regular
        case .bold: return .bold
        }
    }
}

/// The menu bar counterpart of the Windows taskbar widget (src/AITokenUsage/UI/ProviderPanel.xaml): the AI's mark and
/// name, then two lines of label · bar · value · time to reset. Bars use the AI's colour, orange from 75 %, red from
/// 90 %. Rendered to an image because a menu bar item only takes text or an image.
struct MenuBarLabel: View {
    @ObservedObject var client: AgentClient
    @Environment(\.colorScheme) private var colorScheme

    private var weight: WidgetTextWeight { client.state?.textWeight ?? .default }

    var body: some View {
        if let image = renderedImage() {
            Image(nsImage: image)
        } else {
            Text(client.currentPanel.map { "\($0.shortName) \($0.row1.value)" } ?? "AI")
                .fontWeight(weight == .default ? nil : weight.resolve())
        }
    }

    @MainActor
    private func renderedImage() -> NSImage? {
        let foreground: Color = colorScheme == .dark ? .white : .black
        let showReset = client.state?.showResetTime ?? true
        let content: AnyView
        if let panel = client.currentPanel {
            content = AnyView(WidgetContent(panel: panel, foreground: foreground, showReset: showReset, weight: weight))
        } else {
            let language = client.state?.language ?? AgentClient.systemLanguage
            content = AnyView(
                HStack(spacing: 4) {
                    ProviderGlyph(kind: "")
                    Text(client.lastError == nil ? L10n.text(.appTitle, language: language) : "AI ⚠︎")
                        .font(.system(size: 12, weight: weight.resolve(.semibold)))
                        .foregroundColor(foreground)
                }
            )
        }

        let renderer = ImageRenderer(content: content.frame(height: 22).fixedSize())
        renderer.scale = NSScreen.main?.backingScaleFactor ?? 2
        guard let image = renderer.nsImage else { return nil }
        image.isTemplate = false
        return image
    }
}

struct WidgetContent: View {
    let panel: PanelState
    let foreground: Color
    let showReset: Bool
    let weight: WidgetTextWeight

    var body: some View {
        HStack(spacing: 6) {
            HStack(spacing: 4) {
                ProviderGlyph(kind: panel.kind)
                Text(panel.shortName)
                    .font(.system(size: 12, weight: weight.resolve(.semibold)))
                    .lineLimit(1)
                if panel.isStale {
                    Text("·").font(.system(size: 12, weight: weight.resolve(.bold)))
                }
            }
            .opacity(panel.isStale ? 0.6 : 1)

            VStack(alignment: .leading, spacing: 0) {
                WidgetRowView(row: panel.row1, kind: panel.kind, showReset: showReset, weight: weight)
                if let row2 = panel.row2 {
                    WidgetRowView(row: row2, kind: panel.kind, showReset: showReset, weight: weight)
                }
            }
        }
        .foregroundColor(foreground)
        .padding(.horizontal, 2)
    }
}

/// Label 24 · bar 44×4 · value (semibold) · reset, like a Windows widget row.
private struct WidgetRowView: View {
    let row: RowState
    let kind: String
    let showReset: Bool
    let weight: WidgetTextWeight

    var body: some View {
        HStack(spacing: 4) {
            switch row.kind {
            case "meter":
                Text(row.label).font(.system(size: 9.5, weight: weight.resolve())).frame(width: 18, alignment: .leading)
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.gray.opacity(0.45))
                    Capsule().fill(Theme.bar(kind: kind, severity: row.severity))
                        .frame(width: 40 * max(0, min(1, row.fraction ?? 0)))
                }
                .frame(width: 40, height: 4)
                Text(row.value)
                    .font(.system(size: 10, weight: weight.resolve(.semibold)).monospacedDigit())
                    .frame(minWidth: 28, alignment: .trailing)
                if showReset && !row.resetText.isEmpty {
                    Text(row.resetText)
                        .font(.system(size: 9, weight: weight.resolve()).monospacedDigit())
                        .opacity(0.75)
                        .frame(minWidth: 34, alignment: .trailing)
                }
            case "text":
                if !row.label.isEmpty { Text(row.label).font(.system(size: 9.5, weight: weight.resolve())).frame(width: 18, alignment: .leading) }
                Text(row.value).font(.system(size: 10, weight: weight.resolve()))
            default:
                Text(row.value).font(.system(size: 10, weight: weight.resolve())).opacity(0.7)
            }
        }
        .lineLimit(1)
        .frame(height: 11)
        .opacity(row.dimmed ? 0.55 : 1)
    }
}
