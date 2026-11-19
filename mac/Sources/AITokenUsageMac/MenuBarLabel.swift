import AppKit
import SwiftUI

/// Resolve the installed family to a face name for SwiftUI; a missing family uses the system font.
struct MenuBarFont {
    private let faceName: String?

    @MainActor
    init(family: String?) {
        faceName = family.flatMap {
            NSFontManager.shared.font(withFamily: $0, traits: [], weight: 5, size: 12)?.fontName
        }
    }

    func font(size: CGFloat, weight: Font.Weight = .regular) -> Font {
        guard let faceName else { return .system(size: size, weight: weight) }
        return .custom(faceName, fixedSize: size).weight(weight)
    }

    var fallbackLabelFont: Font? {
        faceName.map { .custom($0, fixedSize: NSFont.systemFontSize) }
    }
}

/// The menu bar counterpart of the Windows taskbar widget (src/AITokenUsage/UI/ProviderPanel.xaml): the AI's mark and
/// name, then two lines of label · bar · value · time to reset. Bars use the AI's colour, orange from 75 %, red from
/// 90 %. Rendered to an image because a menu bar item only takes text or an image.
struct MenuBarLabel: View {
    @ObservedObject var client: AgentClient
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let labelFont = MenuBarFont(family: client.state?.widgetFontFamily)
        if let image = renderedImage(labelFont: labelFont) {
            Image(nsImage: image)
        } else {
            Text(client.currentPanel.map { "\($0.shortName) \($0.row1.value)" } ?? "AI")
                .font(labelFont.fallbackLabelFont)
        }
    }

    @MainActor
    private func renderedImage(labelFont: MenuBarFont) -> NSImage? {
        let foreground: Color = colorScheme == .dark ? .white : .black
        let showReset = client.state?.showResetTime ?? true
        let content: AnyView
        if let panel = client.currentPanel {
            content = AnyView(WidgetContent(panel: panel, foreground: foreground, showReset: showReset, labelFont: labelFont))
        } else {
            let language = client.state?.language ?? AgentClient.systemLanguage
            content = AnyView(
                HStack(spacing: 4) {
                    ProviderGlyph(kind: "")
                    Text(client.lastError == nil ? L10n.text(.appTitle, language: language) : "AI ⚠︎")
                        .font(labelFont.font(size: 12, weight: .semibold))
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
    let labelFont: MenuBarFont

    var body: some View {
        HStack(spacing: 6) {
            HStack(spacing: 4) {
                ProviderGlyph(kind: panel.kind)
                Text(panel.shortName)
                    .font(labelFont.font(size: 12, weight: .semibold))
                    .lineLimit(1)
                if panel.isStale {
                    Text("·").font(labelFont.font(size: 12, weight: .bold))
                }
            }
            .opacity(panel.isStale ? 0.6 : 1)

            VStack(alignment: .leading, spacing: 0) {
                WidgetRowView(row: panel.row1, kind: panel.kind, showReset: showReset, labelFont: labelFont)
                if let row2 = panel.row2 {
                    WidgetRowView(row: row2, kind: panel.kind, showReset: showReset, labelFont: labelFont)
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
    let labelFont: MenuBarFont

    var body: some View {
        HStack(spacing: 4) {
            switch row.kind {
            case "meter":
                Text(row.label).font(labelFont.font(size: 9.5)).frame(width: 18, alignment: .leading)
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.gray.opacity(0.45))
                    Capsule().fill(Theme.bar(kind: kind, severity: row.severity))
                        .frame(width: 40 * max(0, min(1, row.fraction ?? 0)))
                }
                .frame(width: 40, height: 4)
                Text(row.value)
                    .font(labelFont.font(size: 10, weight: .semibold).monospacedDigit())
                    .frame(minWidth: 28, alignment: .trailing)
                if showReset && !row.resetText.isEmpty {
                    Text(row.resetText)
                        .font(labelFont.font(size: 9).monospacedDigit())
                        .opacity(0.75)
                        .frame(minWidth: 34, alignment: .trailing)
                }
            case "text":
                if !row.label.isEmpty { Text(row.label).font(labelFont.font(size: 9.5)).frame(width: 18, alignment: .leading) }
                Text(row.value).font(labelFont.font(size: 10))
            default:
                Text(row.value).font(labelFont.font(size: 10)).opacity(0.7)
            }
        }
        .lineLimit(1)
        .frame(height: 11)
        .opacity(row.dimmed ? 0.55 : 1)
    }
}
