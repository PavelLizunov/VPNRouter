#!/usr/bin/env bash
# Capture Android overlays mirroring desktop pages.
# Uses Mac SSH + adb. Each capture restarts the app to a known clean state,
# then applies the navigation steps for that overlay, then screencaps.
#
# Coordinates calibrated empirically from a 1080x1920 / density 450 device
# (see traces/kebab-open.png + main-fresh.png column scans). All taps are
# given in actual phone pixels.
#
# After every capture we validate the file size — anything larger than
# ~1.4 MB on this device strongly suggests the launcher home screen
# leaked through (busy wallpaper compresses badly), so we flag it.

set -e
MAC="slovn@192.168.0.246"
KEY="-i $HOME/.ssh/id_ed25519"
ADB="/opt/homebrew/bin/adb"
PKG="com.ninitux.vpnrouter"
ACT="$PKG/crc64282acc363ff4d3d1.MainActivity"
OUT="C:/Project/VPNRouter/parity-audit/android"
mkdir -p "$OUT"

# --- helpers -----------------------------------------------------------------

restart_app() {
    ssh $KEY $MAC "$ADB shell am force-stop $PKG; sleep 1; $ADB shell am start -n $ACT" >/dev/null 2>&1
    sleep 8
}

# Clear SharedPreferences so first-launch defaults apply (Subscription tab,
# All traffic radio). Permissions are preserved, only app data is wiped.
# Use this before any restart_app call where defaults must apply.
reset_defaults() {
    ssh $KEY $MAC "$ADB shell pm clear $PKG" >/dev/null 2>&1
    sleep 1
}

# Reset preferences AND restart app to a clean default state.
fresh_app() {
    reset_defaults
    restart_app
}

tap() {
    local x="$1" y="$2" delay="${3:-2}"
    ssh $KEY $MAC "$ADB shell input tap $x $y" >/dev/null 2>&1
    sleep "$delay"
}

swipe() {
    local x1="$1" y1="$2" x2="$3" y2="$4" dur="${5:-300}" delay="${6:-2}"
    ssh $KEY $MAC "$ADB shell input swipe $x1 $y1 $x2 $y2 $dur" >/dev/null 2>&1
    sleep "$delay"
}

cap() {
    local name="$1"
    local target="$OUT/page-${name}.png"
    ssh $KEY $MAC "$ADB shell screencap -p /sdcard/cap.png && $ADB pull /sdcard/cap.png /tmp/cap.png" >/dev/null 2>&1
    scp $KEY $MAC:/tmp/cap.png "$target" >/dev/null 2>&1
    local size
    size=$(stat -c%s "$target" 2>/dev/null || stat -f%z "$target")
    if [ "$size" -gt 1400000 ]; then
        echo "WARN: page-${name}.png is ${size} bytes — likely home screen leaked through"
    else
        echo "ok:   page-${name}.png (${size} bytes)"
    fi
}

# --- coordinates (calibrated 2026-05-09) -------------------------------------
# Header / form
KEBAB_X=990;       KEBAB_Y=244
TAB_Y=790
TAB_SUBSCRIPTION_X=240
TAB_SERVER_X=560
TAB_CUSTOM_JSON_X=840
RADIO_X=130
RADIO_SELECTED_APPS_Y=1300
RADIO_ALL_TRAFFIC_Y=1430
# After flipping to "Selected apps", the "Choose apps…" button appears
# directly below the radios. Y empirically near 1500 — sample of a
# capture should refine if needed.
CHOOSE_APPS_X=540; CHOOSE_APPS_Y=1410

# Kebab menu items (X = mid-popup at 800)
MENU_X=800
MENU_FIND_SERVER_Y=602
MENU_ROUTING_PROFILES_Y=770
MENU_SETTINGS_Y=944
MENU_OPEN_LOG_Y=1030
MENU_VIEW_CRASH_LOG_Y=1204

# --- captures ---------------------------------------------------------------

# 1. SIMPLE — fresh launch, default tab (Subscription)
fresh_app
cap "simple"

# 2. KEBAB MENU
tap $KEBAB_X $KEBAB_Y 2
cap "kebab-menu"

# 3. SUBSCRIBE — default tab is already Subscription, so just main view.
fresh_app
cap "subscribe"

# 4. SERVERS — tap "Server" tab on top
fresh_app
tap $TAB_SERVER_X $TAB_Y 3
cap "servers"

# 5. CUSTOM JSON
fresh_app
tap $TAB_CUSTOM_JSON_X $TAB_Y 3
cap "custom-json"

# 6. APPLICATIONS — tap "Selected apps" radio (split mode → Choose apps appears)
fresh_app
tap $RADIO_X $RADIO_SELECTED_APPS_Y 3
cap "applications-mode"

# 7. APPLICATIONS PICKER — Choose apps button → picker overlay (continues from #6)
tap $CHOOSE_APPS_X $CHOOSE_APPS_Y 5
cap "applications-picker"

# 8. NETWORK SETTINGS — kebab → Settings (top of overlay)
fresh_app
tap $KEBAB_X $KEBAB_Y 2
tap $MENU_X $MENU_SETTINGS_Y 4
cap "network-settings"

# 9. SETTINGS FULL — same overlay scrolled to bottom (autostart section)
swipe 540 1500 540 200 400 2
swipe 540 1500 540 200 400 2
swipe 540 1500 540 200 400 2
cap "settings-full"

# 10. DPI BYPASS — re-open Settings, scroll just enough to put DPI Bypass
#     card front-and-centre (~300 px swipe). Larger swipes overshoot it
#     into Reliability / Leak Protection.
fresh_app
tap $KEBAB_X $KEBAB_Y 2
tap $MENU_X $MENU_SETTINGS_Y 4
swipe 540 1400 540 1100 400 2
cap "dpi-bypass-settings"

# 11. FREE CONFIGS — kebab → Find a server
fresh_app
tap $KEBAB_X $KEBAB_Y 2
tap $MENU_X $MENU_FIND_SERVER_Y 4
cap "free-configs"

# 12. ROUTING PROFILES — kebab → Routing profiles
fresh_app
tap $KEBAB_X $KEBAB_Y 2
tap $MENU_X $MENU_ROUTING_PROFILES_Y 4
cap "profiles"

# 13. TOOLS / LOGS — kebab → Open log
fresh_app
tap $KEBAB_X $KEBAB_Y 2
tap $MENU_X $MENU_OPEN_LOG_Y 4
cap "tools-log"

# 14. CRASH LOG — kebab → View crash log
fresh_app
tap $KEBAB_X $KEBAB_Y 2
tap $MENU_X $MENU_VIEW_CRASH_LOG_Y 4
cap "crash-log"

echo "DONE: captured into $OUT"
ls -la "$OUT"
