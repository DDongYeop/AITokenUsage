#!/usr/bin/env bash
# Builds dist/mac/AITokenUsage.app (menu bar app + self-contained .NET agent) and a zip of it.
#
#   tools/build-mac-app.sh            # this Mac's architecture
#   tools/build-mac-app.sh x64        # Intel build (osx-x64 / x86_64)
#
# Needs the .NET 10 SDK (dotnet on PATH or in ~/.dotnet) and Xcode command line tools (swift).
# The app is ad-hoc signed, not notarized: the first launch needs right-click → Open (see README).
set -euo pipefail
trap 'echo "build failed at line $LINENO" >&2' ERR

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARCH="${1:-$(uname -m)}"
case "$ARCH" in
  arm64|aarch64) RID=osx-arm64; SWIFT_ARCH=arm64 ;;
  x64|x86_64)    RID=osx-x64;   SWIFT_ARCH=x86_64 ;;
  *) echo "unknown architecture: $ARCH (use arm64 or x64)" >&2; exit 1 ;;
esac

# The first dotnet (PATH, ~/.dotnet, /usr/local/share/dotnet) that has a 10.x SDK.
DOTNET=""
for candidate in "$(command -v dotnet || true)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet; do
  if [[ -x "$candidate" ]] && "$candidate" --list-sdks 2>/dev/null | grep -q '^10\.'; then DOTNET="$candidate"; break; fi
done
[[ -n "$DOTNET" ]] || { echo ".NET 10 SDK not found (install: https://dot.net/download)" >&2; exit 1; }
export DOTNET_ROOT="$(dirname "$DOTNET")"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
OUT="$ROOT/dist/mac"
APP="$OUT/AITokenUsage.app"
WORK="$OUT/obj"

echo "==> AITokenUsage $VERSION for $RID"
rm -rf "$APP" "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/agent" "$WORK"

echo "==> agent (dotnet publish)"
"$DOTNET" publish "$ROOT/src/AITokenUsage.Agent/AITokenUsage.Agent.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o "$WORK/agent" -v quiet -nologo | grep -E " error |warning CA" || true
cp "$WORK/agent/aitokenusage-agent" "$APP/Contents/Resources/agent/"

echo "==> menu bar app (swift build)"
(cd "$ROOT/mac" && swift build -c release --arch "$SWIFT_ARCH" >/dev/null)
BIN="$(cd "$ROOT/mac" && swift build -c release --arch "$SWIFT_ARCH" --show-bin-path)/AITokenUsageMac"
cp "$BIN" "$APP/Contents/MacOS/AITokenUsage"

sed "s/__VERSION__/$VERSION/g" "$ROOT/mac/Info.plist" > "$APP/Contents/Info.plist"

echo "==> icon"
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
if sips -s format png "$ROOT/src/AITokenUsage/Assets/AppIcon.ico" --out "$WORK/icon.png" >/dev/null 2>&1; then
  for size in 16 32 128 256 512; do
    sips -z $size $size "$WORK/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$WORK/icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || echo "   (icon skipped)"
fi

echo "==> ad-hoc sign"
# No hardened runtime: the .NET JIT would need extra entitlements, and ad-hoc builds are not notarized anyway.
codesign --force --sign - "$APP/Contents/Resources/agent/aitokenusage-agent"
codesign --force --sign - "$APP"

rm -rf "$WORK"
ZIP="$OUT/AITokenUsage-$VERSION-macOS-$SWIFT_ARCH.zip"
rm -f "$ZIP"
(cd "$OUT" && ditto -c -k --keepParent AITokenUsage.app "$(basename "$ZIP")")

echo "==> done"
echo "    $APP"
echo "    $ZIP"
