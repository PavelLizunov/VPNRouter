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

# v2.27.2: bundle upstream sing-box inside Contents/MacOS/ so first-run
# DeployBundledProfiles (in MainWindowViewModel.cs) can copy it from
# AppContext.BaseDirectory to ~/Library/Application Support/VPNRouter/bin/
# and chmod +x. Previously Mac users had to install sing-box manually
# (brew install sing-box) — the stale comment in MainWindowViewModel
# claimed it was bundled but the build script wasn't actually doing it.
#
# Matching Linux workflow: we use GitHub's prebuilt upstream binary
# (1.13.14) rather than the custom VPNRouter rebuild. Upstream includes
# with_clash_api + with_utls + with_quic by default, and eliminates
# "custom build" as a variable when diagnosing issues.
# NOTE: the macOS upstream archive ships NO libcronet (SagerNet builds no
# Cronet for Darwin), so NaiveProxy is gated off on macOS in ServerUriParser;
# the mac bundle is just the sing-box binary (nothing to "not cut" here).
SINGBOX_VER="1.13.14"
# macos-latest on Actions is arm64; local dev Macs are usually arm64
# too. Fall back to amd64 if running on Intel for the rare local case.
ARCH="arm64"
if [ "$(uname -m)" = "x86_64" ]; then
    ARCH="amd64"
fi
echo "    sing-box: fetching v${SINGBOX_VER} darwin-${ARCH}..."
curl -sSL -o /tmp/singbox.tar.gz \
    "https://github.com/SagerNet/sing-box/releases/download/v${SINGBOX_VER}/sing-box-${SINGBOX_VER}-darwin-${ARCH}.tar.gz"
tar -xzf /tmp/singbox.tar.gz -C /tmp
cp "/tmp/sing-box-${SINGBOX_VER}-darwin-${ARCH}/sing-box" "$APP/Contents/MacOS/sing-box"
chmod +x "$APP/Contents/MacOS/sing-box"
# Strip macOS quarantine xattr so Gatekeeper doesn't refuse to launch
# the helper binary on first run. (The .app bundle itself gets the
# quarantine bit from Safari on download; the nested sing-box binary
# would inherit it and get blocked.)
xattr -d com.apple.quarantine "$APP/Contents/MacOS/sing-box" 2>/dev/null || true
"$APP/Contents/MacOS/sing-box" version | head -1
echo "    sing-box bundled ($(stat -f%z "$APP/Contents/MacOS/sing-box" 2>/dev/null || stat -c%s "$APP/Contents/MacOS/sing-box") bytes)"

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
