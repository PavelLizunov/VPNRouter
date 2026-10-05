#!/usr/bin/env bash

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

echo "    sing-box-vpnctl: downloading darwin-universal release..."
SB_DARWIN_VER="1.14.2-vpnctl.2"
SB_DARWIN_SHA256="088ea42c639b67a5572e8f3e0a7df5f8e76a5ba2a32750f24737ec2c905492be"
SB_DARWIN_ZIP="/tmp/sing-box-${SB_DARWIN_VER}-darwin-universal.zip"
curl -sSL -o "$SB_DARWIN_ZIP" \
  "https://github.com/PavelLizunov/sing-box-vpnctl/releases/download/v${SB_DARWIN_VER}/sing-box-${SB_DARWIN_VER}-darwin-universal.zip"
if command -v sha256sum >/dev/null 2>&1; then
  echo "${SB_DARWIN_SHA256}  ${SB_DARWIN_ZIP}" | sha256sum -c -
elif command -v shasum >/dev/null 2>&1; then
  echo "${SB_DARWIN_SHA256}  ${SB_DARWIN_ZIP}" | shasum -a 256 -c -
fi
unzip -q -o "$SB_DARWIN_ZIP" -d /tmp/
cp "/tmp/sing-box-${SB_DARWIN_VER}-darwin-universal/sing-box" "$APP/Contents/MacOS/sing-box"
rm -f "$SB_DARWIN_ZIP"
chmod +x "$APP/Contents/MacOS/sing-box"
xattr -d com.apple.quarantine "$APP/Contents/MacOS/sing-box" 2>/dev/null || true
echo "    sing-box-vpnctl bundled ($(stat -f%z "$APP/Contents/MacOS/sing-box" 2>/dev/null || stat -c%s "$APP/Contents/MacOS/sing-box") bytes)"

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

if [ ! -f "$REPO_DIR/VPNRouter.App/Assets/InstallGuide.html" ]; then
    echo "ERROR: InstallGuide.html missing from repo. Don't ship DMG without it."
    exit 2
fi
cp "$REPO_DIR/VPNRouter.App/Assets/InstallGuide.html" "$STAGE/InstallGuide.html"

ln -s /System/Applications/Utilities/Terminal.app "$STAGE/Terminal"

echo "    Contents:"
ls -la "$STAGE"

echo "[5/5] Creating DMG + zip..."
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
