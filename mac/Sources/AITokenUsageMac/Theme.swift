import SwiftUI

/// The Windows app's colours and provider marks (src/AITokenUsage/UI/ProviderGlyphs.cs): simple geometric marks,
/// not brand logos.
enum Theme {
    static let claude = Color(red: 0xD9 / 255, green: 0x77 / 255, blue: 0x57 / 255)
    static let codex = Color(red: 0x10 / 255, green: 0xA3 / 255, blue: 0x7F / 255)
    static let gemini = Color(red: 0x42 / 255, green: 0x85 / 255, blue: 0xF4 / 255)
    static let copilot = Color(red: 0x89 / 255, green: 0x57 / 255, blue: 0xE5 / 255)
    static let neutral = Color(red: 0x8A / 255, green: 0x8A / 255, blue: 0x8A / 255)
    static let warning = Color(red: 0xF5 / 255, green: 0xA5 / 255, blue: 0x24 / 255)
    static let critical = Color(red: 0xF0 / 255, green: 0x44 / 255, blue: 0x38 / 255)
    static let connected = Color(red: 0x3F / 255, green: 0xB9 / 255, blue: 0x50 / 255)
    static let accent = Color(red: 0x4C / 255, green: 0xC2 / 255, blue: 0xFF / 255)

    /// Extra accounts ("claude#k3x9q2") use their AI's colour: callers pass the kind.
    static func brand(_ kind: String) -> Color {
        switch kind {
        case "claude": return claude
        case "codex": return codex
        case "gemini": return gemini
        case "copilot": return copilot
        default: return neutral
        }
    }

    /// Bars: the AI's colour, orange from 75 % (warning), red from 90 % (critical).
    static func bar(kind: String, severity: String) -> Color {
        switch severity {
        case "critical": return critical
        case "warning": return warning
        default: return brand(kind)
        }
    }

    /// The status dot of an "AI 연결" card.
    static func statusDot(_ status: String) -> Color {
        switch status {
        case "ok": return connected
        case "rateLimited", "error", "waitingForCliRefresh": return warning
        default: return neutral
        }
    }
}

/// The 14×14 provider mark: Claude ✳︎ strokes, Codex rings, Gemini filled star, Copilot hexagon.
struct ProviderGlyph: View {
    let kind: String
    var size: CGFloat = 14

    var body: some View {
        let color = Theme.brand(kind)
        Group {
            if kind == "gemini" {
                GlyphShape(kind: kind).fill(color)
            } else {
                GlyphShape(kind: kind)
                    .stroke(color, style: StrokeStyle(lineWidth: 1.6, lineCap: .round, lineJoin: .round))
            }
        }
        .frame(width: size, height: size)
    }
}

struct GlyphShape: Shape {
    let kind: String

    func path(in rect: CGRect) -> Path {
        let s = rect.width / 14
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * s, y: rect.minY + y * s) }
        var path = Path()
        switch kind {
        case "claude":
            for (a, b) in [((7, 0.8), (7, 13.2)), ((0.8, 7), (13.2, 7)), ((2.6, 2.6), (11.4, 11.4)), ((11.4, 2.6), (2.6, 11.4))] {
                path.move(to: p(a.0, a.1))
                path.addLine(to: p(b.0, b.1))
            }
        case "codex":
            path.addEllipse(in: CGRect(x: rect.minX + 1.2 * s, y: rect.minY + 1.2 * s, width: 11.6 * s, height: 11.6 * s))
            path.addEllipse(in: CGRect(x: rect.minX + 4.6 * s, y: rect.minY + 4.6 * s, width: 4.8 * s, height: 4.8 * s))
        case "gemini":
            path.move(to: p(7, 0))
            path.addCurve(to: p(14, 7), control1: p(7.5, 4.5), control2: p(9.5, 6.5))
            path.addCurve(to: p(7, 14), control1: p(9.5, 7.5), control2: p(7.5, 9.5))
            path.addCurve(to: p(0, 7), control1: p(6.5, 9.5), control2: p(4.5, 7.5))
            path.addCurve(to: p(7, 0), control1: p(4.5, 6.5), control2: p(6.5, 4.5))
            path.closeSubpath()
        case "copilot":
            path.move(to: p(7, 1))
            for point in [p(12.2, 4), p(12.2, 10), p(7, 13), p(1.8, 10), p(1.8, 4)] { path.addLine(to: point) }
            path.closeSubpath()
        default:
            for (x, top) in [(2.5, 7.5), (7.0, 2.5), (11.5, 9.0)] {
                path.move(to: p(x, 12))
                path.addLine(to: p(x, top))
            }
        }
        return path
    }
}
