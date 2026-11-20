import AppKit
import SwiftUI

/// What opens under the menu bar item: the Windows hover card for the AI on the widget (‹ › switch AI, like a click or
/// the wheel on the Windows widget), then the Windows tray menu: 설정… · 지금 새로고침 · 자동 전환 일시정지 · 종료.
struct MenuWindow: View {
    @ObservedObject var client: AgentClient
    @Environment(\.openWindow) private var openWindow

    private var language: String { client.state?.language ?? AgentClient.systemLanguage }
    private func t(_ key: L10n.Key) -> String { L10n.text(key, language: language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let error = client.lastError, client.state == nil {
                Text(error).font(.callout).foregroundColor(Theme.critical)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(12)
            } else if let panel = client.currentPanel {
                DetailCard(panel: panel, iconStyle: client.state?.providerIconStyle, pager: client.rotatingPanels.count > 1 ? pager : nil)
                    .id(client.customIconRevision)
            } else if client.state != nil {
                Text(t(.noProviders)).foregroundColor(.secondary).padding(12)
            } else {
                HStack { ProgressView().controlSize(.small); Text(t(.waiting)).foregroundColor(.secondary) }.padding(12)
            }

            VStack(alignment: .leading, spacing: 0) {
                MenuRow(title: t(.menuSettings)) {
                    openWindow(id: SettingsWindow.id)
                    NSApp.activate(ignoringOtherApps: true)
                }
                MenuRow(title: t(.menuRefresh)) { client.refresh() }
                MenuRow(title: t(.menuPauseRotation), checked: client.rotationPausedByUser) {
                    client.rotationPausedByUser.toggle()
                }
                Divider().padding(.vertical, 4)
                MenuRow(title: t(.menuQuit)) { NSApplication.shared.terminate(nil) }
            }
            .padding(.horizontal, 6)
            .padding(.bottom, 6)
        }
        .padding(8)
        .frame(width: 380)
        .onAppear { client.setPaused(client.state?.pauseOnHover ?? true) }
        .onDisappear { client.setPaused(false) }
    }

    private var pager: AnyView {
        AnyView(HStack(spacing: 2) {
            Button { client.showPrevious() } label: { Image(systemName: "chevron.left") }
            Button { client.showNext() } label: { Image(systemName: "chevron.right") }
        }
        .buttonStyle(.borderless)
        .foregroundColor(.secondary))
    }
}

/// The Windows detail card (src/AITokenUsage/UI/DetailFlyout.xaml): mark, name and plan; per limit the long label and
/// value, then the bar with "3시간 12분 후 · 오늘 15:50" beside it; a divider and the status lines.
struct DetailCard: View {
    let panel: PanelState
    let iconStyle: String?
    var pager: AnyView?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                ProviderGlyph(id: panel.id, kind: panel.kind, style: iconStyle, size: 16)
                Text(panel.title).font(.system(size: 15, weight: .semibold))
                if let plan = panel.card.plan {
                    Text(plan).font(.system(size: 13)).foregroundColor(.secondary)
                }
                Spacer()
                if let pager { pager }
            }

            ForEach(panel.card.rows) { row in
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text(row.label).font(.system(size: 13))
                        Spacer()
                        Text(row.value).font(.system(size: 13, weight: .semibold).monospacedDigit())
                    }
                    if row.isText {
                        if !row.resetText.isEmpty {
                            Text(row.resetText).font(.system(size: 12)).foregroundColor(.secondary)
                        }
                    } else {
                        HStack(spacing: 12) {
                            ZStack(alignment: .leading) {
                                Capsule().fill(Color.primary.opacity(0.15))
                                Capsule().fill(Theme.bar(kind: panel.kind, severity: row.severity))
                                    .frame(width: 160 * max(0, min(1, row.fraction ?? 0)))
                            }
                            .frame(width: 160, height: 6)
                            Text(row.resetText).font(.system(size: 12)).foregroundColor(.secondary).lineLimit(1)
                        }
                    }
                }
                .opacity(row.dimmed ? 0.6 : 1)
            }

            if let extra = panel.card.extraLine {
                Text(extra).font(.system(size: 12)).foregroundColor(.secondary)
            }

            if !panel.card.statusLines.isEmpty {
                Divider()
                ForEach(panel.card.statusLines, id: \.self) { line in
                    Text(line).font(.system(size: 12)).foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.05)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.12)))
    }
}

/// A menu-looking row (highlight on hover) so the bottom half reads like the Windows tray menu.
private struct MenuRow: View {
    let title: String
    var checked = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Image(systemName: "checkmark").font(.system(size: 11, weight: .semibold)).opacity(checked ? 1 : 0)
                Text(title)
                Spacer()
            }
            .padding(.vertical, 4)
            .padding(.horizontal, 6)
            .contentShape(Rectangle())
            .background(RoundedRectangle(cornerRadius: 5).fill(hover ? Color.accentColor.opacity(0.85) : .clear))
            .foregroundColor(hover ? .white : .primary)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}
