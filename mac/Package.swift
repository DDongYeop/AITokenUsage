// swift-tools-version:5.9
import PackageDescription

// The macOS menu bar app. It shows what the .NET agent (src/AITokenUsage.Agent) reports; build the .app with
// tools/build-mac-app.sh, which bundles the agent next to this binary.
let package = Package(
    name: "AITokenUsageMac",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "AITokenUsageMac", path: "Sources/AITokenUsageMac")
    ]
)
