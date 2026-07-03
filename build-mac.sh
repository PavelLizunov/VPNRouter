#!/usr/bin/env bash
# VPNRouter macOS build script.
#
# Run on a Mac with .NET 8 SDK installed. Produces:
#   /tmp/VPNRouter-v<version>-mac.dmg
#   /tmp/VPNRouter-v<version>-mac.zip
#
# DMG contents (all four required — don't ship without them):
#   1. VPNRouter.app           — the app bundle
#   2. Applications            — symlink to /Applications (drag target)
#   3. InstallGuide.html       — Russian+English one-time sudoers setup guide
#   4. Terminal                — alias to /System/Applications/Utilities/Terminal.app
#
# Usage:
#   ./build-mac.sh 2.4.7

set -euo pipefail

if [ -z "${1:-}" ]; then
    echo "Usage: $0 <version>"
    echo "Example: $0 2.4.7"
    exit 1
fi

VERSION="$1"
REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR=/tmp/vpn-mac-publish
APP=/tmp/VPNRouter.app
STAGE=/tmp/vpn-stage-dmg
DMG=/tmp/VPNRouter-v${VERSION}-mac.dmg
ZIP=/tmp/VPNRouter-v${VERSION}-mac.zip

export PATH="/opt/homebrew/bin:$PATH"

echo "[1/5] Cleaning previous build..."
rm -rf "$PUBLISH_DIR" "$APP" "$STAGE" "$DMG" "$ZIP"

echo "[2/5] dotnet publish (osx-arm64, self-contained)..."
dotnet publish "$REPO_DIR/VPNRouter.App/VPNRouter.App.csproj" \
    -c Release -r osx-arm64 --self-contained \
    -o "$PUBLISH_DIR" 2>&1 | tail -3

echo "[3/5] Building .app bundle..."
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/VPNRouter.App"
cp "$REPO_DIR/VPNRouter.App/Assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

# v2.45.x: bundle the sing-box-lx FORK (with_awg + with_xhttp) so AmneziaWG and
# XHTTP work on macOS too — SAME fork / pinned commits / patches as Windows (see
# tools/build-singbox-lx.ps1), built natively for darwin via tools/build-singbox-lx.sh.
# SingBoxFeatures tag-probes the bundled binary at runtime, so AWG/XHTTP auto-enable
# once the core carries the tags — no app-code platform gate. Previously macOS shipped
# GitHub's UPSTREAM sing-box 1.13.14 = NO AWG/XHTTP. Trade-off: macOS now rides the
# custom fork (1.13.13-base), exactly as Windows already does. Still no libcronet on
# Darwin (NaiveProxy stays gated off in ServerUriParser).
# Requires Go >=1.24.7 + git + python3 on the builder (build-mac.yml pins Go via setup-go;
# locally set GO=~/sdk/go1.25.x/bin/go if `go` on PATH is older).
echo "    sing-box-lx: building darwin fork (with_awg + with_xhttp)..."
bash "$REPO_DIR/tools/build-singbox-lx.sh" "$VERSION" "$APP/Contents/MacOS/sing-box"
chmod +x "$APP/Contents/MacOS/sing-box"
# Strip the Gatekeeper quarantine xattr from the nested helper binary so it launches.
xattr -d com.apple.quarantine "$APP/Contents/MacOS/sing-box" 2>/dev/null || true
echo "    sing-box-lx bundled ($(stat -f%z "$APP/Contents/MacOS/sing-box" 2>/dev/null || stat -c%s "$APP/Contents/MacOS/sing-box") bytes)"

# ── Bundle wgturn-cli (Phase 1 of emergency channel integration) ──
# Built from PavelLizunov/wgturn-core. Provides the wgturn-cli binary
# that VPNRouter will invoke via EmergencyChannelEngine (Phase 2) to
# tunnel WireGuard through VK Calls' TURN infrastructure when standard
# VPN protocols are blocked.
#
# Source resolution: $WGTURN_CORE_DIR > tools/wgturn-cli-cache/wgturn-core/
# > gh repo clone PavelLizunov/wgturn-core (requires gh auth). If neither
# works, the step is skipped with a warning and the .app still works.
WGTURN_CORE="${WGTURN_CORE_DIR:-}"
if [ -z "$WGTURN_CORE" ] || [ ! -d "$WGTURN_CORE/cmd/wgturn-cli" ]; then
    if [ -d "$REPO_DIR/tools/wgturn-cli-cache/wgturn-core/cmd/wgturn-cli" ]; then
        WGTURN_CORE="$REPO_DIR/tools/wgturn-cli-cache/wgturn-core"
    elif command -v gh >/dev/null 2>&1; then
        echo "    wgturn-cli: cloning PavelLizunov/wgturn-core into tools/wgturn-cli-cache/..."
        mkdir -p "$REPO_DIR/tools/wgturn-cli-cache"
        if gh repo clone PavelLizunov/wgturn-core "$REPO_DIR/tools/wgturn-cli-cache/wgturn-core" >/dev/null 2>&1; then
            WGTURN_CORE="$REPO_DIR/tools/wgturn-cli-cache/wgturn-core"
        fi
    fi
fi
if [ -n "$WGTURN_CORE" ] && [ -d "$WGTURN_CORE/cmd/wgturn-cli" ] && command -v go >/dev/null 2>&1; then
    WGTURN_SHA=$(cd "$WGTURN_CORE" && git rev-parse --short=12 HEAD 2>/dev/null || echo unknown)
    mkdir -p "$APP/Contents/MacOS/bin"
    echo "    wgturn-cli: building darwin-${ARCH} (sha $WGTURN_SHA)..."
    (
        cd "$WGTURN_CORE"
        GOOS=darwin GOARCH="${ARCH}" CGO_ENABLED=0 go build \
            -trimpath -ldflags="-s -w -X main.version=$WGTURN_SHA" \
            -o "$APP/Contents/MacOS/bin/wgturn-cli" ./cmd/wgturn-cli
    )
    chmod +x "$APP/Contents/MacOS/bin/wgturn-cli"
    xattr -d com.apple.quarantine "$APP/Contents/MacOS/bin/wgturn-cli" 2>/dev/null || true
    "$APP/Contents/MacOS/bin/wgturn-cli" version || true
    echo "    wgturn-cli bundled at Contents/MacOS/bin/ (sha $WGTURN_SHA)"
else
    echo "    wgturn-cli: SKIPPED (set WGTURN_CORE_DIR or clone into tools/wgturn-cli-cache/, requires go on PATH)"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>VPNRouter</string>
    <key>CFBundleDisplayName</key><string>Virtual Penguin Network</string>
    <key>CFBundleIdentifier</key><string>com.vpnrouter.app</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleExecutable</key><string>VPNRouter.App</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSUIElement</key><false/>
</dict>
</plist>
PLIST
plutil "$APP/Contents/Info.plist" > /dev/null

echo "[4/5] Staging DMG contents..."
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

# Install guide HTML — explains one-time sudoers setup in RU/EN.
# Lives in the repo so it version-controls alongside the app.
if [ ! -f "$REPO_DIR/VPNRouter.App/Assets/InstallGuide.html" ]; then
    echo "ERROR: InstallGuide.html missing from repo. Don't ship DMG without it."
    exit 2
fi
cp "$REPO_DIR/VPNRouter.App/Assets/InstallGuide.html" "$STAGE/InstallGuide.html"

# Terminal alias — user double-clicks it from the DMG to paste the sudo command.
ln -s /System/Applications/Utilities/Terminal.app "$STAGE/Terminal"

echo "    Contents:"
ls -la "$STAGE"

echo "[5/5] Creating DMG + zip..."
# hdiutil create occasionally fails with "Resource busy" on GitHub
# Actions macOS runners (shared storage contention). Retry up to 3x
# with a sync between attempts so the VFS catches up.
HDIUTIL_OK=0
for attempt in 1 2 3; do
    if hdiutil create -volname "VPNRouter ${VERSION}" -srcfolder "$STAGE" \
        -ov -format UDZO "$DMG" 2>&1 | tail -2; then
        HDIUTIL_OK=1
        break
    fi
    echo "hdiutil attempt ${attempt} failed; retrying after sync..."
    sync
    sleep 3
    # Detach anything stale that might be holding the path
    hdiutil detach "/Volumes/VPNRouter ${VERSION}" 2>/dev/null || true
done
if [ "$HDIUTIL_OK" != "1" ]; then
    echo "ERROR: hdiutil create failed after 3 attempts"
    exit 1
fi
ditto -c -k --keepParent "$APP" "$ZIP"

echo
echo "Done:"
ls -la "$DMG" "$ZIP"
