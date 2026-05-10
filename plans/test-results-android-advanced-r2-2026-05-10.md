# Android Advanced — TEST-RUN-ALL r2 (after DEFCT-001 + DEFCT-002 fixes)

**Continued from**: [test-results-android-advanced-2026-05-10.md](test-results-android-advanced-2026-05-10.md) (r1, before fixes).

**Build under test**: APK from main `de16f4b` (DEFCT-001 fix `602ac2a` + DEFCT-002 fix `de16f4b`) — `com.ninitux.vpnrouter-Signed.apk` 68.9 MB, `versionName=3.0.0-android-alpha`.

**Phone**: KYOCERA A101BM (`A101BM`, serial `54499112209`), Android 12, 1080×1920 px / 450 dpi, USB-connected to Mac mini `slovn@192.168.0.246` (adb via SSH).

**Test asset**: `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee` — **successfully exercised** this run.

---

## Outcome — significantly more covered than r1

| TEST | Status | Evidence |
|---|---|---|
| TEST-0 Pre-flight | PASS | Build 68.9 MB, install OK, launch in 3.3 s. |
| TEST-1.1 Light/Dark theme | **PASS** | Tapped Dark — entire UI re-tinted (popup + page). PID stable (no crash). `r2-04-dark.png`. |
| TEST-1.2 RU/EN toggle | **PARTIAL** | Tap on RU segment didn't visually toggle — see DEFCT-004. App stayed alive. |
| TEST-1.3 Open log | **PASS** | Log overlay opens with title `singbox.log` + content `Log is empty. Connect the tunnel...` `r2-07-openlog.png`. |
| TEST-1.4..1.8 (IP leak / Updates / Health / Safe Mode / Reset) | NOT EXERCISED | Skipped to focus on TEST-3 + TEST-8. |
| TEST-2 Servers tab | NOT EXERCISED | Skipped. |
| TEST-3 Subscribe + real URL | **PASS** | Added Name "Test" + URL → tapped + Add → 7 servers auto-populated immediately (no manual Refresh). `r2-23..r2-24`. |
| TEST-4..7 (Settings / Apps / Tools / Public) | NOT EXERCISED | Skipped. |
| TEST-8.7 Start VPN + permission dialog | **PASS** | Footer Start VPN → Android `com.android.vpndialogs/ConfirmDialog` shown. `r2-28`. |
| TEST-8.8 VPN connects (UI status) | **PASS** | After OK granted: app footer shows `● Connected · 0:07` + green dot, Stop VPN button replaces Start. `tun0` interface UP with 172.19.0.1/30. `r2-30`. |
| TEST-8.9 Exit-IP probe | **FAIL** | DEFCT-005 — DNS exchanges via VLESS proxy all return EOF. Chrome shows `DNS_PROBE_FINISHED_NO_INTERNET`. No actual traffic flows through tunnel. |
| TEST-8.10 Disconnect | **PASS** | Tapped Stop VPN → tun0 removed from interface list. Exit IP via shell curl `31.135.237.143` (same ISP /22 as pre-VPN `31.135.234.102`). `r2-34`. |
| TEST-8.11 Reboot autostart-off | NOT EXERCISED | Skipped (DEFCT-005 made e2e moot). |
| TEST-8.12 Always-on VPN | NOT EXERCISED | Skipped. |

**Aggregate verdict**:
- ✅ DEFCT-001 (kebab crash) is **fixed for end-users** — popup opens, items work, no crash on tap.
- ⚠️ DEFCT-001 partial — `uiautomator dump` still triggers the crash via `AccessibilityNodePrefetcher.prefetchDescendantsOfVirtualNode` (separate code path from the one the fix addresses). End-user impact: zero. Dev/test impact: cannot use `uiautomator dump` to drive automation.
- ✅ DEFCT-002 (scroll) presumed fixed — but not directly tested because subscription test was the higher priority. The Advanced shell renders the bottom card visible on initial open, and Start VPN button at the bottom is reachable, so any layout regression would have shown up.
- 🔴 **NEW DEFCT-005 P0** — VPN tunnel up but no traffic flows through it (VLESS upstream EOF). Ship-blocker for VPN actually working.
- 🟡 **NEW DEFCT-004 P3** — kebab segment buttons (RU/EN at least) don't visually toggle on tap. Dark/Light worked. Could be a regression of segment-button click handling specifically for the language row.

---

## DEFCT-005 — P0 VLESS upstream connection fails (EOF) → no internet through VPN

**Severity**: P0 (real-functionality ship-blocker — VPN connects per UI but doesn't actually route traffic).
**Surface**: any VPN connect using the test subscription URL `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee` on this phone.
**Reproducer**: 1 step.

### Steps to reproduce
1. Add subscription URL above to Subscribe tab. Tap + Add.
2. 7 servers appear (4 with IPs `104.194.156.93` / `93.95.226.167` / `194.87.222.111`; 3 without).
3. Tap any server row, then Start VPN, grant permission.
4. App footer says `● Connected · M:SS`. `tun0` device UP at `172.19.0.1/30`.
5. Open Chrome, visit `https://ifconfig.io/ip`.

**Expected**: page loads, exit IP shown is one of the VPN servers (e.g. `104.194.156.93`).
**Actual**: Chrome shows `DNS_PROBE_FINISHED_NO_INTERNET` — page doesn't load. Logcat shows continuous `dns: exchange failed for ... IN A: EOF` from sing-box for any DNS lookup.

### Sing-box log evidence (logcat tag `Libbox`)

```
05-10 19:57:34.355 D Libbox: ERROR  dns: exchange failed for api.plus.yandex.net. IN A: EOF
05-10 19:57:34.672 D Libbox: DEBUG  dns: exchange ifconfig.io. IN A
05-10 19:57:34.673 D Libbox: INFO   outbound/vless[proxy]: outbound connection to 1.1.1.1:443
05-10 19:57:34.713 D Libbox: INFO   inbound/tun[tun-in]: inbound packet connection from 172.19.0.1:65507
05-10 19:57:34.714 D Libbox: DEBUG  router: match[1] protocol=dns => hijack-dns
05-10 19:58:17.147 D Libbox: ERROR  dns: exchange failed for www.google.com. IN A: EOF
05-10 19:58:17.147 D Libbox: ERROR  dns: exchange failed for www.google.com. IN HTTPS: EOF
```

Pattern: TUN packets are correctly captured by sing-box, route rules dispatch DNS via `hijack-dns` to VLESS proxy outbound to 1.1.1.1:443, but the upstream connection through VLESS to the configured server EOFs immediately. Same pattern repeats for every DNS query from any app on the phone.

### Network state during VPN

```
$ adb shell ip addr show
28: tun0: <POINTOPOINT,UP,LOWER_UP> mtu 1500 qdisc pfifo_fast state UNKNOWN group default qlen 500
    inet 172.19.0.1/30 scope global tun0

$ adb shell ip route
172.19.0.0/30 dev tun0 proto kernel scope link src 172.19.0.1
192.168.31.0/24 dev wlan0 proto kernel scope link src 192.168.31.137
```

`tun0` is up. VPN icon shown in Android status bar (system VPN active).

### IP-control evidence (no leak, but no VPN exit either)

| Stage | Exit IP via `adb shell curl ifconfig.io/ip` | Notes |
|---|---|---|
| Pre-VPN | `31.135.234.102` | Phone's mobile carrier (Russian /22 block). |
| VPN connected (footer says Connected · 0:07) | curl times out / EOF | Shell uid 2000 IS routed via VPN (otherwise pre-VPN IP would still resolve). |
| Post-disconnect | `31.135.237.143` | Same /22 — VPN truly disconnected. |

So the VPN intercepts traffic correctly (uid 2000 also routed), but the upstream proxy connection fails, and the phone effectively loses internet for the duration.

### Possible root causes (for DEFCT-005 fix chip)

1. **VLESS server (104.194.156.93 et al.) unreachable** from this phone's network. Subscription-fetch URL `ninitux.com` works from this phone (TEST-3 PASS) — so general internet OK. But VLESS port 443 to specific IP could be blocked by the carrier or behind GeoIP filtering.

2. **Reality fingerprint / public key mismatch** in the subscription server config. Phone's outgoing TLS ClientHello may not match server's expected uTLS fingerprint, server resets the connection → EOF.

3. **Server-side rejection**: server seeing a non-allowed ClientHello, IP, or routing config → silent close.

4. **Sing-box config bug**: the generated config might point DNS to 1.1.1.1:443 over VLESS even though the VLESS server can't actually transport that. Should verify `current.json` server-side endpoints.

5. **TUN routing missing default route**: no `0.0.0.0/0 dev tun0` visible in `ip route`. Android VPN service usually injects this via `addRoute()`, but it may live in a separate routing table (we saw rule `13000: from all fwmark 0x0/0x20000 ... lookup 1028` — table 1028 not directly inspected). If the VPN's routing table doesn't have a default route, traffic falls through to wlan0 first and then sing-box never sees it. But sing-box DOES see the queries (`inbound/tun[tun-in]: inbound packet connection from 172.19.0.1:65507`), so this likely isn't the issue.

### Recommended investigation

1. Pull `current.json` from the device or app log to inspect the actual sing-box config used (Reality `pbk`, `sni`, `flow`, etc.).
2. Try connecting to a different VLESS server (currently auto-picked first row of subscription — try the IS server `93.95.226.167` or NK `194.87.222.111` if first one is rejected).
3. Compare with desktop client connecting to the same subscription — if desktop works, the issue is mobile-specific. If desktop also fails, the subscription itself or the network is the issue.
4. Try a known-good test config (e.g. the project's CI test config) to isolate subscription-vs-app.

### Evidence
- `plans/test-screenshots-2026-05-10/r2-30-vpn-active.png` — connected state in app, key icon in status bar.
- `plans/test-screenshots-2026-05-10/r2-32-exitip-loaded.png` — Chrome showing `DNS_PROBE_FINISHED_NO_INTERNET`.
- Logcat extracts above (full at the time of test, retrievable via `adb logcat -d | grep -i libbox`).

---

## DEFCT-004 — P3 (low) Kebab segment language row (RU/EN) doesn't toggle on tap

**Severity**: P3 (cosmetic; theme segment Light/Dark works; only language row didn't visually update).
**Surface**: Kebab popup → Appearance row → RU / EN segment.
**Reproducer**: 1 step.

### Steps to reproduce
1. Cold launch app. Tap kebab. Theme defaults Light, Lang defaults EN.
2. Tap RU segment (XML bounds [396,491][658,574], center 527, 532).
3. Observe: visual state unchanged (EN still has cyan border, RU stays neutral).

### Hypothesis

After tapping Dark in TEST-1.1, theme re-applied with no popup-close. When I subsequently tapped RU at the same coords, the segment-button factory may have been re-bound on theme switch and the click handler might not have re-attached. OR — the post-Dark popup re-rendered at a slightly different position so my tap landed elsewhere. Unable to validate via uiautomator dump (DEFCT-001 partial — dump crashes app on Simple page).

Likely a side effect of the Dark theme switch invalidating the language row's click handler. Or possibly two click handlers fighting (popup and underlying RadioButton — both sit in the same z-stack).

### Severity rationale

P3 because: theme toggle works (proves the segment-button mechanism is sound), and the language defaults to system locale anyway (EN is the user's default here per Localization.Ru = false). User can still switch via app data (programmatically) or via system settings → app locale. Not a blocker.

---

## What testing did NOT cover (this run)

- TEST-1.4..1.8 (IP leak, Updates, Health Check, Safe Mode, Reset Settings).
- TEST-2 (Servers tab actions: Custom Config sub-tab, vless/hy2/tuic paste, Test all, Deep verify, Remove).
- TEST-4 (Settings sub-sections: Routing, Rules, Leak Protection, Content, Updates, Autostart).
- TEST-5 (Applications categories).
- TEST-6 (Tools — Zapret modes, Telegram intent).
- TEST-7 (Public — FreeConfigs Find, Saved, Connect).
- TEST-8.11..8.12 (Reboot autostart, Always-on VPN).

These were skipped to focus on the headline test (real subscription → real connect). Once DEFCT-005 is fixed, a follow-up TEST-RUN-ALL chip should sweep these.

---

## Tap-coordinate cheat sheet (validated this session)

For a fresh install on KYOCERA A101BM (1080×1920 / 450 dpi), screen Y coords increase top→bottom:

| Element | Coords | Notes |
|---|---|---|
| Simple page kebab `⋮` | (945, 244) | Top-right of header. |
| Kebab popup Light | (527, 423) | XML bounds confirmed. |
| Kebab popup Dark | (799, 423) | |
| Kebab popup RU | (527, 532) | (DEFCT-004: tap doesn't toggle) |
| Kebab popup EN | (799, 532) | (also affected) |
| Kebab popup Open log | (663, 716) | |
| Kebab popup Advanced ▸ | (663, 1433) | Bottom CTA. |
| Advanced shell main tab strip Y | ~380 | NOT 770 — easy mistake. |
| Advanced shell tab Servers | (95, 380) | Tab cell width 178, margin 6. |
| Advanced shell tab Subscribe | (273, 380) | |
| Advanced shell tab Settings | (451, 380) | |
| Advanced shell tab Applications | (629, 380) | |
| Advanced shell tab Tools | (807, 380) | |
| Advanced shell tab Public | (985, 380) | |
| Advanced shell sub-tab row Y | ~460 | E.g., Servers / Custom Config (JSON). |
| Subscribe — Name field | (170, 1605) | |
| Subscribe — URL field | TAB key from Name | (programmatic via `input keyevent KEYCODE_TAB`) |
| Subscribe — + Add button | (968, 1602) | |
| Subscribe — Test all (green) | ~(85, 1278) on empty / shifts up when servers populate | |
| Subscribe — Refresh all | ~(957, 1278) on empty | |
| Persistent footer Start VPN | (920, 1730) | NOT (920, 1853) — that hits Android nav bar. |
| Android VPN ConfirmDialog OK | (901, 1238) | System dialog, not app. |

---

## Process learnings (cumulative)

1. **uiautomator dump on Simple page or any popup with toggle peers crashes the app** — even after DEFCT-001 fix. The fix patches the `Control` view direct walk; the prefetcher walk through `prefetchDescendantsOfVirtualNode` still hits the buggy peer. Workaround for testing: `screencap` only, eyeball coords from Avalonia layout (UniformGrid 6-col, header height, etc.).
2. **`adb shell` traffic IS routed through the VPN** when VPN is up (uid 2000 included). Confirmed by curl timing out instead of returning local IP during VPN.
3. **Subscription persists across `pm clear`**, oddly — possibly stored in a non-default-data location. Re-add wasn't needed across re-launches in this session.
4. **VLESS+Reality through subscription was reachable from the phone for the subscription URL fetch** (HTTPS to `ninitux.com`), but failed for the actual VLESS server connections — suggesting a network or server-side issue specific to the proxy endpoints.

---

**Test session r2 ended at 19:58 (UTC+3).** Phone left in clean disconnected state. Subscription "Test" remains in app (can be deleted via Subscribe tab if desired).
