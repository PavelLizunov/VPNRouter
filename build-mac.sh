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
