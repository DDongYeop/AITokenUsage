import AppKit

enum InstalledServiceIcons {
    private struct Application {
        let identifiers: [String]
        let names: [String]
    }

    private static let apps = [
        "codex": Application(identifiers: ["com.openai.codex"], names: ["Codex"]),
        "claude": Application(identifiers: ["com.anthropic.claudefordesktop"], names: ["Claude"]),
        "antigravity": Application(identifiers: ["com.google.antigravity", "com.google.antigravity-ide"],
                                   names: ["Antigravity", "Google Antigravity", "Antigravity IDE"]),
        "gemini": Application(identifiers: [], names: ["Gemini", "Google Gemini"]),
        "copilot": Application(identifiers: ["com.github.CopilotForXcode"],
                               names: ["GitHub Copilot for Xcode", "GitHub Copilot"])
    ]
    private static let lock = NSLock()
    private static var cache: [String: NSImage?] = [:]

    static func load(_ providerId: String) -> NSImage? {
        let kind = String(providerId.prefix { $0 != "#" })
        guard let app = apps[kind] else { return nil }
        lock.lock()
        defer { lock.unlock() }
        if let cached = cache[kind] { return cached }

        let workspace = NSWorkspace.shared
        let registered = app.identifiers.compactMap { workspace.urlForApplication(withBundleIdentifier: $0) }
        let directories = FileManager.default.urls(for: .applicationDirectory, in: [.localDomainMask, .userDomainMask])
        let conventional = directories.flatMap { dir in
            app.names.map { dir.appendingPathComponent($0 + ".app", isDirectory: true) }
        }
        for candidate in registered + conventional {
            let url = candidate.resolvingSymlinksInPath()
            guard url.isFileURL, url.pathExtension == "app",
                  (try? url.resourceValues(forKeys: [.volumeIsLocalKey]).volumeIsLocal) == true,
                  let bundle = Bundle(url: url), let info = bundle.infoDictionary,
                  info["CFBundlePackageType"] as? String == "APPL",
                  matches(kind, identifier: bundle.bundleIdentifier, name: info["CFBundleName"] as? String,
                          displayName: info["CFBundleDisplayName"] as? String, conventional: conventional.contains(candidate)),
                  hasIcon(bundle, info: info) else { continue }
            let image = workspace.icon(forFile: url.path)
            cache.updateValue(image, forKey: kind)
            return image
        }
        cache.updateValue(nil, forKey: kind)
        return nil
    }

    static func matches(_ kind: String, identifier: String?, name: String?, displayName: String?, conventional: Bool) -> Bool {
        guard let app = apps[kind] else { return false }
        if let identifier, app.identifiers.contains(where: { $0.caseInsensitiveCompare(identifier) == .orderedSame }) {
            return true
        }
        return conventional && [name, displayName].compactMap { $0 }.contains(where: app.names.contains)
    }

    // NSWorkspace can return a generic application image even for a missing icon.
    private static func hasIcon(_ bundle: Bundle, info: [String: Any]) -> Bool {
        if let file = info["CFBundleIconFile"] as? String, !file.isEmpty {
            let ext = (file as NSString).pathExtension.isEmpty ? "icns" : nil
            if bundle.url(forResource: file, withExtension: ext) != nil { return true }
        }
        return (info["CFBundleIconName"] as? String)?.isEmpty == false &&
            bundle.url(forResource: "Assets", withExtension: "car") != nil
    }
}
