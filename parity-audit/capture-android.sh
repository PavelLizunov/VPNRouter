#!/usr/bin/env bash
# Capture Android overlays mirroring desktop pages.
# Uses Mac SSH + adb. Each step: tap → wait → screencap → pull → save.

set -e
MAC="slovn@192.168.0.246"
KEY="-i $HOME/.ssh/id_ed25519"
ADB="/opt/homebrew/bin/adb"
PKG="com.ninitux.vpnrouter"
ACT="$PKG/crc64282acc363ff4d3d1.MainActivity"
OUT="C:/Project/VPNRouter/parity-audit/android"
mkdir -p "$OUT"

cap() {
    local name="$1"
    ssh $KEY $MAC "$ADB shell screencap -p /sdcard/cap.png && $ADB pull /sdcard/cap.png /tmp/cap.png" >/dev/null 2>&1
    scp $KEY $MAC:/tmp/cap.png "$OUT/page-${name}.png" >/dev/null 2>&1
    echo "captured: page-${name}.png"
}

tap() {
    local x="$1" y="$2" delay="${3:-2}"
    ssh $KEY $MAC "$ADB shell input tap $x $y" >/dev/null 2>&1
    sleep "$delay"
}

force_back() {
    ssh $KEY $MAC "$ADB shell input keyevent 4" >/dev/null 2>&1  # KEYCODE_BACK
    sleep 1
}

# 0. Cold restart for clean state
ssh $KEY $MAC "$ADB shell am force-stop $PKG; sleep 1; $ADB shell am start -n $ACT" >/dev/null 2>&1
sleep 7

# 1. SIMPLE — main scroller (this is the default state on launch)
cap "simple"

# 2. KEBAB MENU — capture once, since it's the gateway to most overlays
tap 1000 270 2
cap "kebab-menu"

# 3. SUBSCRIBE OVERLAY — already on main, no overlay needed (subscription is part of main UI)
# Close kebab first
force_back
cap "subscribe"

# 4. SERVERS OVERLAY — tap "Server" tab on main
tap 540 1180 2  # rough Server tab position
cap "servers"

# 5. CUSTOM JSON — tap "Custom JSON" tab
tap 850 1180 2
cap "custom-json"

# 6. APPLICATIONS — switch routing mode "Selected apps" then tap "Choose apps..."
tap 200 1850 2
cap "applications-mode"
tap 540 1980 3
cap "applications-picker"
force_back

# 7. NETWORK — kebab → Settings
tap 1000 270 2
tap 800 1320 3
cap "network-settings"

# 8. DPI BYPASS — find DPI section in settings (scroll if needed)
ssh $KEY $MAC "$ADB shell input swipe 540 1500 540 600 300" >/dev/null 2>&1
sleep 2
cap "dpi-bypass-settings"

# Close back to main
force_back
sleep 1
force_back

# 9. FREE CONFIGS — kebab → Find a server
tap 1000 270 2
tap 800 700 3  # Y for "Find a server"
cap "free-configs"
force_back

# 10. ROUTING PROFILES — kebab → Routing profiles
tap 1000 270 2
tap 800 760 3  # Y for "Routing profiles"
cap "profiles"
force_back

# 11. TOOLS / LOGS — kebab → Open log
tap 1000 270 2
tap 800 1030 3
cap "tools-log"
force_back

# 12. CRASH LOG — kebab → View crash log
tap 1000 270 2
tap 800 1170 3
cap "crash-log"
force_back

# 13. SETTINGS overlay full
tap 1000 270 2
tap 800 870 3
cap "settings-full"
force_back

echo "DONE: captured into $OUT"
ls -la "$OUT" | head -20
