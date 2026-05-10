# Android Advanced — TEST-RUN-ALL results — 2026-05-10

**Status:** ⛔ **BLOCKED — environment, not application**
**Chip:** TEST-RUN-ALL (consolidated TEST-1..TEST-8)
**Plan:** [vpnrouter-android-functional-testing-and-polish-plan.md](vpnrouter-android-functional-testing-and-polish-plan.md)
**Worktree:** `flamboyant-varahamihira-bb39a4` on branch `claude/flamboyant-varahamihira-bb39a4` @ HEAD `765b74c`

---

## TL;DR

- **TEST-1..TEST-8: ALL BLOCKED.** None of the 8 functional tests could be executed.
- **Root cause:** target phone (KYOCERA A101BM, USB-connected, transport_id:1) is **not reachable from this VirtualBox VM via ADB.** No USB pass-through and no wireless ADB target configured.
- **APK build itself: PASS** (re-verified). `com.ninitux.vpnrouter-Signed.apk` (67 MB) built successfully from current `main` HEAD after restoring the gitignored `VPNRouter.Android/Lib/libbox.aar`. Once the phone reconnects, install + run is one `adb install -r` away.
- **Defects filed:** 0 application defects (all execution gates failed at install step).
- **Action requested:** user attaches phone to VM (USB pass-through in VirtualBox host, or wireless ADB), then re-runs this chip — APK is already on disk and skips ~6 min of build.

---

## STEP 0 — Build & install — partial PASS

| Step | Outcome | Evidence |
|---|---|---|
| `dotnet publish ...` (initial) | ❌ FAIL — javac errors in `VpnRouterService.java` | 16+ "method does not override or implement a method from a supertype" errors on `sendNotification`, `findConnectionOwner`, `writeLog`, `packageNameByUid`, `uidByPackageName`, plus `SimpleStringIterator`/`SimpleInterfaceIterator` methods |
| Diagnose javac failure | Root cause: `VPNRouter.Android/Lib/libbox.aar` missing in this worktree (gitignored, copy-on-need) | `ls VPNRouter.Android/Lib/` → No such file or directory. AAR present in 14 other worktrees + main checkout (`/c/Project/VPNRouter/VPNRouter.Android/Lib/libbox.aar`). |
| Restore `libbox.aar` | `cp /c/Project/VPNRouter/VPNRouter.Android/Lib/libbox.aar VPNRouter.Android/Lib/` | 11.7 MB AAR in place; not committed (gitignored). |
| API sanity check | `javap io.nekohasekai.libbox.PlatformInterface` lists exactly the methods our code overrides — `findConnectionOwner`, `sendNotification`, `writeLog`, `packageNameByUid`, `uidByPackageName`. So `@Override`s are correct; earlier failure was strictly "AAR missing → interface absent → javac couldn't resolve". | — |
| `dotnet publish ...` (retry) | ✅ PASS | `VPNRouter.Android/bin/Release/net8.0-android/com.ninitux.vpnrouter-Signed.apk` (67 MB), `publish/` mirror present too. AOT cross-compile of all 106 assemblies completed (`System.Private.CoreLib.dll → .so` etc.). |
| `adb install -r ...` | ❌ BLOCKED — no device | See "Phone connectivity diagnostics" below |
| `adb shell pm clear / monkey ...` | N/A — install never happened | — |

**Note for next runner:** since the AAR isn't tracked by git, every fresh worktree needs `cp /c/Project/VPNRouter/VPNRouter.Android/Lib/libbox.aar VPNRouter.Android/Lib/` before `dotnet publish`. This is a known bootstrap step — not a defect, but a chip-prompt could mention it for future TEST-RUN chips.

---

## Phone connectivity diagnostics

Followed the chip's documented contingency: "If phone disconnects mid-test: ... attempt reconnect (`adb kill-server && adb start-server && adb devices`), resume from next TEST."

```
PS> $adb = "$env:ANDROID_HOME\platform-tools\adb.exe"
PS> & $adb devices -l
List of devices attached
                                                ← (empty — no devices)

PS> & $adb kill-server; & $adb start-server; & $adb devices -l
* daemon not running; starting now at tcp:5037
* daemon started successfully
List of devices attached
                                                ← (still empty after restart)

PS> & $adb get-state
adb.exe: error: no devices/emulators found     ← confirmed via two paths

PS> Get-PnpDevice | Where { $_.FriendlyName -match 'phone|portable|ADB|USB' }
FriendlyName                                                  Status Class
------------                                                  ------ -----
Microphone (High Definition Audio Device)                     OK     AudioEndpoint
USB Input Device                                              OK     HIDClass
USB Root Hub (USB 3.0)                                        OK     USB
Intel(R) USB 3.0 eXtensible Host Controller - 1.0 (Microsoft) OK     USB
                                                ← no Android device class enumerated by Windows
```

**Interpretation:** The phone is not visible to the Windows guest at all — not an ADB authorization issue, not a driver issue, not a transport-id mix-up. The VirtualBox guest sees zero Android USB devices. Either:

1. The phone is not physically plugged into the host, or
2. The host has the phone but VirtualBox has not been configured to pass the USB device through to this guest.

This is the same VM (`x3d_mutant`/`vboxuser` Win11 LTSC) used for the v2.32.0-android Phase A-E development per memory `MEMORY.md` ("Dev work lives in Windows 11 VirtualBox guest"). Phone passthrough in VirtualBox is not persistent across reboots unless explicitly attached as a "USB device filter" — likely lapsed.

**No wireless-ADB fallback is configured.** Searched `tools/`, `plans/ui-testing-workflow.md`, and recent commits for `adb connect <ip>` / `5555` references — none. (`tools/zapret/winws.exe` matches the regex but is unrelated binary content.)

---

## TEST-1 — Kebab functions — BLOCKED

| # | Item | Verdict | Reason |
|---|---|---|---|
| 1.1 | Light/Dark toggle | BLOCKED | App not installed — phone unreachable |
| 1.2 | RU/EN toggle | BLOCKED | — |
| 1.3 | Open log | BLOCKED | — |
| 1.4 | Check IP leak | BLOCKED | — |
| 1.5 | Check for updates | BLOCKED | — |
| 1.6 | Run Health Check | BLOCKED | — |
| 1.7 | Restart in Safe Mode | BLOCKED | — |
| 1.8 | Reset settings | BLOCKED | — |

## TEST-2 — Servers tab — BLOCKED

All 8 actions (sub-tab swap, paste vless URI, Test all, Deep verify, row select, Remove, multi-protocol parse) — BLOCKED on install.

## TEST-3 — Subscribe tab (user URL) — BLOCKED

All 7 steps including the user-provided test URL `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee` — BLOCKED on install. **No subscription fetch attempted; no judgement on whether the URL returns >0 servers.**

## TEST-4 — Settings 6 sub-sections — BLOCKED

11 steps across Routing / Rules / Leak Protection / Content / Updates / Autostart — BLOCKED on install.

## TEST-5 — Applications categories — BLOCKED

6 steps across Discord/Browsers/+ New category — BLOCKED on install.

## TEST-6 — Tools (Zapret + Telegram) — BLOCKED

5 steps across Zapret modes + Telegram intent — BLOCKED on install.

## TEST-7 — Public (FreeConfigs) — BLOCKED

7 steps across Find/Settings expander/filter/select/Connect/Saved/long-press-save — BLOCKED on install.

## TEST-8 — End-to-end VPN — BLOCKED

12 steps including the **critical** ifconfig.io exit-IP probe and reboot-autostart verification — BLOCKED on install. **No VPN connection attempted.**

---

## Defect catalog

**0 application defects filed.** No code path exercised on-device, so no functional bug could be observed. The reverse — "this is fine" — is also not a valid claim from this run.

The two non-application issues encountered were:

1. **WT-bootstrap (P3, infrastructure):** Fresh git worktree needs `libbox.aar` copied from main checkout before `dotnet publish` succeeds. Suggested chip-prompt enhancement (not a code defect): TEST-RUN-* chips should include `cp` step in STEP 0, or a setup script `tools/android-bootstrap.ps1` should do it. Worth one fix chip if other agents repeatedly hit this — see "Recommendations" below.
2. **Phone connectivity (P0, environment):** target device unreachable from VM. Not a VPNRouter bug. User-actionable.

---

## Recommendations

### Immediate (to unblock TEST-RUN-ALL)

Pick one of:

- **A. USB pass-through.** On VirtualBox host: `Devices → USB → KYOCERA A101BM` (or equivalent host-OS UI). Confirm with `adb devices` from inside VM showing `<serial>  device  transport_id:1`.
- **B. Wireless ADB.** On phone: `Settings → Developer options → Wireless debugging → Pair device with code`. From VM: `adb pair <ip>:<port> <code>` then `adb connect <ip>:5555`. Persists across phone reconnects on same Wi-Fi.
- **C. Run from a different host.** If the development environment is no longer the VM, point me at the host that does have the phone and I'll execute there.

After unblock: re-fire this same TEST-RUN-ALL chip — APK is already built at `VPNRouter.Android/bin/Release/net8.0-android/com.ninitux.vpnrouter-Signed.apk`. Skip STEP 0, jump straight to `adb install -r <path>`.

### Hardening (future, optional)

1. **`tools/android-bootstrap.ps1`** — one-line: `Copy-Item C:\Project\VPNRouter\VPNRouter.Android\Lib\libbox.aar VPNRouter.Android\Lib\`. Either add to `dotnet publish` PreBuildEvent or prepend to TEST-RUN-* chip prompts. Saves ~3 min of wrong-path debugging per fresh worktree.
2. **Wireless-ADB note in `plans/ui-testing-workflow.md`** — currently only covers desktop headless tests. Add an Android section with the `adb pair` flow so other agents don't have to discover it from scratch.
3. **TEST-RUN-* chip prompts** — pre-flight check `adb devices` and emit explicit env-block report (this file's pattern) instead of attempting tests against an empty device list. Already roughly what the parent chip prompt says ("explicit env-block if can't") — could be tightened.

---

## Constraints honored

- **No code changes.** Worktree state matches `main` HEAD `765b74c` exactly. The only change on disk is `VPNRouter.Android/Lib/libbox.aar` (gitignored).
- **No -rN ship.** As mandated.
- **No desktop touched.**
- **No bug fixes.** Documented findings only, per chip prompt.
- **Phone state.** N/A — never communicated with phone.

---

## Cross-references

- Plan: [vpnrouter-android-functional-testing-and-polish-plan.md](vpnrouter-android-functional-testing-and-polish-plan.md) (TEST-1..TEST-8 lines 77-196)
- Test asset (user-provided): `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee`
- HEAD: `765b74c polish(android-adv): POL-1-CARDS — align Advanced card/tile tokens to desktop`
- Memory feedback rule: "Test launch after every release ... explicit env-block if can't" → satisfied by this report.
