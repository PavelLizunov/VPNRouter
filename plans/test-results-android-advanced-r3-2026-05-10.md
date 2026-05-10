# Android Advanced — TEST-RUN-ALL r3 (after DEFCT-005 fix)

**Continued from**: [r2 report](test-results-android-advanced-r2-2026-05-10.md) (DEFCT-005 found).

**Build under test**: APK from main `5a771a6` (DEFCT-005 fix on top of DEFCT-001+002 fixes) — `com.ninitux.vpnrouter-Signed.apk` 68.9 MB, `versionName=3.0.0-android-alpha`.

**Phone**: KYOCERA A101BM (`A101BM`, serial `54499112209`), Android 12, USB-connected to Mac mini `slovn@192.168.0.246` (adb via SSH).

**Test asset**: `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee` — **end-to-end PASS** this run.

---

## Headline result

✅ **End-to-end VPN flow works.** Adding the user's real subscription, tapping Start VPN, and probing exit IP produces the expected change from RU mobile carrier IP to a German VPN server IP (`104.194.156.93` = de-01). Real internet traffic flows through the VPN tunnel; Google loads in 629 ms.

The DEFCT-005 fix (`5a771a6`) closes the last ship-blocker found in this test cycle.

---

## Outcome

| TEST | r2 result | r3 result | Notes |
|---|---|---|---|
| TEST-0 Pre-flight | PASS | PASS | Build 68.9 MB, install OK. |
| TEST-1.1 Light/Dark theme | PASS | PASS (re-verified during navigation) | |
| TEST-1.3 Open log | PASS | (skipped — covered in r2) | |
| TEST-3 Subscribe + real URL | PASS | **PASS** | Same flow as r2 — Name "Test" + URL → + Add → 7 servers populate. `r3-01-subs-added.png`. |
| TEST-8.7 VPN permission dialog | PASS | PASS | Dialog still shows; OK at (901, 1238). |
| TEST-8.8 VPN connects (UI) | PASS | **PASS** | Footer Connected · 0:07, tun0 172.19.0.1/30 UP. `r3-02-vpn-on.png`. |
| TEST-8.9 Exit-IP via VPN | **FAIL** (DEFCT-005) | **PASS** | Pre `31.135.237.143` (RU) → VPN `104.194.156.93` (de-01 DE). |
| TEST-8.9b HTTP traffic via VPN | (not exercised) | **PASS** | `curl https://www.google.com` returned HTTP 200 in 629 ms. Country code `DE`. |
| TEST-8.10 Disconnect | PASS | **PASS** | tun0 removed; exit IP `31.135.237.143` restored. `r3-03-stopped.png`. |

**Aggregate verdict**: VPN core flow is shippable for end-users. All known P0/P1 defects from r1+r2 closed:
- DEFCT-001 (kebab crash) — fixed end-user impact in r2
- DEFCT-002 (scroll) — fixed in r2
- DEFCT-005 (placeholder server) — **fixed in r3**

Remaining lower-severity items (DEFCT-001 partial uiautomator dump, DEFCT-004 RU/EN segment toggle) are not ship-blockers and continue per separate fix chips.

---

## DEFCT-005 fix verification — detailed evidence

### 1. End-to-end flow without manual row selection

The fix's core behavior change: `AndroidStorage.GetActiveServer` now auto-picks `servers[0]` when no name is selected but cached servers exist (fix to the row-tap-only-on-name-column issue described in the commit message). My r3 test exercised exactly this code path:

1. Cold launch app, fresh install, `pm clear` first.
2. kebab → Advanced ▸ → Subscribe tab.
3. Type Name "Test", TAB to URL field, paste user's subscription URL, BACK to dismiss keyboard, tap + Add.
4. 7 servers populate (de-01/is-01/nk-01 with various ports).
5. **Without tapping any server row**, tap Start VPN → grant permission.
6. App says Connected · 0:07. tun0 UP.

This is the original failure mode from r2. Now it works because `GetActiveServer` returns `servers[0]` (de-01 443) instead of returning null and falling through to the dead `PlaceholderVlessUri`.

### 2. Exit IP probe via `adb shell curl`

```
$ adb shell curl -s -m 8 https://ifconfig.io/ip
31.135.237.143               ← pre-VPN, phone's mobile carrier

$ adb shell curl -s -m 12 https://ifconfig.io/ip
104.194.156.93               ← post-VPN, de-01 server's exit IP

$ adb shell curl -s -m 8 https://ifconfig.io/country_code
DE                            ← Germany (de-01 confirmed)

$ adb shell curl -s -m 12 -o /dev/null -w "HTTP %{http_code} time=%{time_total}s\n" https://www.google.com
HTTP 200 time=0.629752s      ← Google reachable through VPN, sub-second response

$ adb shell ip addr show | grep tun0
30: tun0: <POINTOPOINT,UP,LOWER_UP> mtu 1500 qdisc pfifo_fast state UNKNOWN group default qlen 500
    inet 172.19.0.1/30 scope global tun0
```

After Stop VPN:

```
$ adb shell curl -s -m 8 https://ifconfig.io/ip
31.135.237.143               ← restored to mobile carrier

$ adb shell ip addr show | grep tun0
                             ← (no output — tun0 removed)
```

### 3. Logcat — no EOF errors during the connection

Where r2's logcat showed continuous `dns: exchange failed for ... IN A: EOF`, r3 shows successful exchanges. The previous EOF was 100% caused by the placeholder server's broken Reality config (`pbk DnT9hI...`, `sni yahoo.com`, etc.) targeting a dead test endpoint. Fix removed that fallback.

---

## What r3 explicitly did NOT cover

- TEST-1.2 RU/EN toggle — DEFCT-004 P3 still open (separate concern, not VPN-blocking).
- TEST-1.4..1.8 (IP leak / Updates / Health Check / Safe Mode / Reset Settings).
- TEST-2 (Servers tab — Custom Config sub-tab, vless/hy2/tuic paste, Test all, Deep verify).
- TEST-4 (Settings sub-sections).
- TEST-5 (Applications categories).
- TEST-6 (Tools — Zapret modes, Telegram intent).
- TEST-7 (Public — FreeConfigs Find / Saved / Connect).
- TEST-8.11..8.12 (Reboot autostart, Always-on VPN).
- DEFCT-001 partial — uiautomator dump still crashes on Simple page popup; tracked separately.

A future TEST-RUN-ALL r4 (or split per-tab chips) can sweep these once DEFCT-005 is confirmed in production users' hands. None are ship-blockers given the headline VPN flow now works.

---

## What this enables for the project

1. **VPN actually works on Android** with a real-world subscription URL — the headline feature is functional.
2. **Test pipeline validated**: build APK on Windows VM → SCP to Mac mini → `adb install` → drive UI via `screencap` + computed coords → curl probe → screenshot proof → markdown report. Round-trip ~3 min from code change to verified test result.
3. **`screencap`-only navigation works** despite DEFCT-001 partial — Avalonia layout knowledge (UniformGrid 6-col, 178 px cells, 6 px margin) is enough to navigate without `uiautomator dump`. The cheat sheet in [r2 report](test-results-android-advanced-r2-2026-05-10.md) captures the validated coordinates.

---

**Test session r3 ended at 20:51 (UTC+3).** Phone left in clean disconnected state. Subscription "Test" remains in app.
