# Handoff — v2.46.0-r1 shipped, needs end-to-end verify on windows-brat (NOT dev box)

**Date:** 2026-07-05
**From:** feature session (W1 true-split implementation + ship)
**To:** main session (the one that knows the test-env boundaries)
**Why this handoff exists:** the feature session started a post-ship MCP verify on the
LOCAL dev box by mistake. That machine is Pavel's primary working box, not a test target.
The packaged-app end-to-end verify must run on **windows-brat**, not here. This doc hands
the remaining verify + the ship state over cleanly.

---

## HARD RULE — where you may test (read first)

| Machine | Role | May install/launch/live-test VPNRouter? |
|---|---|---|
| **Local `C:\Project\VPNRouter`** | Pavel's PRIMARY dev box — build, git, CI orchestration | **NO. Never install / launch / live-connect / MCP-test here.** Has Program-Files ACL + file-lock friction, and it's his working machine. |
| **windows-brat** (Proxmox vmid 100, `192.168.0.106`, LTSC 2019, user `tester`) | Windows test VM | **YES** — this is where the packaged-app + driver end-to-end runs. |
| **debian-xfce** (vmid 101, `192.168.0.99`) | Linux test VM | N/A for true-split (Windows-only feature). |

**`mcp__vpnrouter-test__*` controls the LOCAL dev box** — so it is the WRONG tool for VM
testing. For windows-brat drive via WinRM (screenshot-to-file → copy PNG back → Read) or RDP.

Autonomous windows-brat access (per memory `windows-test-vm` / `autonomous-testvm-access`):
- Power/console: `tools/testvm-control.ps1` via Proxmox API token `.pve-api-token.xml`.
- WinRM: DPAPI cred `.testpc-cred-192.168.0.106.xml` (user `tester`, **UNELEVATED** — run
  elevated work as SYSTEM via `schtasks /ru SYSTEM /rl HIGHEST`).
- VM has **no standalone .NET** → must use the self-contained win-x64 ZIP (the shipped
  install ZIP is self-contained, so it runs as-is).

---

## What shipped

**v2.46.0-r1** — True split-tunnel driver (Windows exclude-mode). Rolling prerelease.

| Field | Value |
|---|---|
| Release | `v2.46.0-r1` (GitHub `PavelLizunov/VPNRouter`), **prerelease=true** |
| Tag / commit | `v2.46.0-r1` @ `d9f45595` (bug-hunt-fixes commit) |
| `AppVersion.Version` | `"2.46.0-r1"` (matches tag incl. -rN — rule #5 OK) |
| GitHub "Latest" | `v2.45.0` (correct — r1 is prerelease, stable keeps the badge) |
| Windows assets (4) | `VPNRouter-v2.46.0-r1-win.zip` (64.8 MB, sha256 `99ed285b7dd8cee761b2fa67da60b5eec6c017096f9cdbbcd4a25881ba491e02`), `VPNRouter-update-v2.46.0-r1-win.zip` (37.9 MB, sha256 `4f700e6f07be3a44e49f97f9dd55e4538de41db7701e25fc3ee5c3d9e0e146c6`), + 2 `.sha256` sidecars |
| Core bundled | **sing-box-lx** (AWG/XHTTP), `sing-box.exe` = 40447488 bytes, byte-identical to v2.45.0 core — verified, no AWG/XHTTP regression |
| Driver bundled | `driver\{mullvad-split-tunnel.sys 98400, .cat 12350, .inf 1796, checksums.sha256}` + `LICENSE.split-tunnel`; update-ZIP carries `driver\` under `_bootstrap\` (bug-hunt P1-2 fix live) |

**Build command used** (reproduce exactly — the `-SingBoxPath` and `-BundleSplitDriver`
flags are both required, else you regress AWG/XHTTP or drop the driver):
```powershell
.\build.ps1 -Version "2.46.0-r1" -SingBoxPath publish\sing-box-lx.exe -BundleSplitDriver -Upload
```

### CI status at handoff time
- `dotnet test` (push-event, on tag) — in_progress
- `Build macOS DMG` — in_progress
- `Build Linux AppImage + .deb` — in_progress
- `Auto-Update Integration Test (Windows)` — in_progress
- `Verify Release Integrity` — **success**
- `Build Android APK` — **skipped** (known NU1102 block; APK not expected — memory `android-local-build-toolchain`)
- Source commit `d9f45595` push-event CI: `test` + `grep` green, `characterization-windows` skipped (tolerated), 0 hard red. `verify-last-commit-ci.ps1` = exit 0.

**When mac+linux finish** the release should carry **14 desktop assets** (4 win + 4 mac +
6 linux). Android APK will be absent (NU1102) unless built locally — that's the known state,
not a regression.

---

## The feature (context)

Mullvad `win-split-tunnel` kernel driver, Windows exclude-mode: excluded apps bypass the
VPN at the kernel level (egress physical NIC) and **survive a sing-box restart/crash with
zero dropped requests**, instead of relying only on sing-box process-name routing.

**RED-LINE INVARIANT = fail-open**: any driver failure → log + silent fallback to
post-capture `process_name→direct` routing. The network is NEVER broken by the driver.

UI surface: a **"True split: active"** badge in the status bar, shown while engaged (hover
= caveats tooltip). Bound to `MainWindowViewModel.IsTrueSplitActive`, toggled by
`VpnEngine.TrueSplitEngagedChanged`.

Code: `VPNRouter.Core/Services/SplitTunnelDriver{Manager,Protocol,Interop}.cs`,
`VpnEngine.cs` (hooks 1-4 + `TryEngageSplitDriverAsync` + `SweepStaleStateAsync`),
`PlatformServices.CreateSplitTunnelDriver`, `MainWindowViewModel` badge wiring, `build.ps1`
`[6c/9]` driver bundle, `packaging/windows/uninstall.ps1` service cleanup.

---

## Already verified (do NOT re-do)

- **All 5 W1 phases code-complete + CI-green** (P2 manager, P3 pump, W1.2 wiring, W1.3 badge, W1.4 packaging).
- **4 independent review agents + adversarial bug-hunt** — 3 real P1s found & fixed in `d9f45595`:
  (1) update-ZIP omitted the driver, (2) hot-apply exclude-set edit didn't re-engage,
  (3) NIC-change debounce lifetime. Survivors logged in `plans/OPEN-DEFECTS.md` (all
  fail-safe/non-gating P2s).
- **Core LIVE-PROVEN on windows-brat** (via `SplitLiveHarness` reflection over `Core.dll`):
  - P3 event pump decodes split events (image-offsets 30/26/22 vs live START/STOP_SPLITTING).
  - **W1.2 W0.1 0-gap contract PASS** — excluded curl-loop lost **0/16** requests across a
    mid-loop `sing-box` kill.
  - Bundled signed `.sys` loads as a kernel service on Win10 LTSC (no test-signing); uninstall cleans it.
- **Packaging confirmed by file inspection** of the shipped ZIP: driver files + lx core present, checksums match.

---

## REMAINING WORK — packaged-app end-to-end on windows-brat

The harness proved the `Core.dll` mechanics. What is **not yet proven for the shipped
packaged app** is the normal user path through the real GUI. Run this on **windows-brat**:

1. **Install** the shipped `VPNRouter-v2.46.0-r1-win.zip` (self-contained) on windows-brat
   — normal install path (`C:\Program Files\VPNRouter\app\` via the GUI bootstrap), NOT an
   extract-dir hack. (The dev-box attempt failed on Program-Files locks — a dev-box artifact,
   not a shipped-binary defect.)
2. **Launch** via `VPNRouter.GUI.exe` (the bootstrap entry) → confirm the Avalonia window
   renders, no exception dialog, correct version.
3. **Configure exclude mode** with a non-empty excluded app list (e.g. a browser).
4. **Connect** using Pavel's subscription (memory `ninitux-subscription-url` — credential,
   local-only, never commit/echo).
5. **Assert the end-to-end user scenario (rule #13 — reach the actual final element):**
   - The **"True split: active" badge appears** in the status bar (screenshot it).
   - Log shows the driver reached **ENGAGED** (grep `vpnrouter*.log` for the engage line).
   - The excluded app egresses the physical NIC (bypasses VPN); non-excluded goes through tunnel.
   - **0-gap**: excluded app keeps connectivity across a `sing-box` restart.
   - Disconnect → badge disappears, driver disengages (RESET).
6. **Uninstall** → confirm `sc query mullvad-split-tunnel` is gone (uninstall.ps1 `sc stop`/`sc delete`).
7. **Log scan**: no `[ERR]` / `Exception` / `FATAL` / `crashed` in `vpnrouter*.log`.

Produce a PASS/FAIL report per item with screenshots + log excerpts (rule #12 post-ship verify).

**Fail-open sanity (important):** also confirm that with the driver DELIBERATELY broken
(e.g. rename the `.sys`), an exclude-mode connect still works via post-capture routing and
the badge simply stays off — the network must never break.

---

## Next gate — STABLE CUT IS USER-GATED

Do NOT auto-cut stable (golden rule #6). After windows-brat end-to-end PASS **and**:
- (a) `dotnet build -c Release` 0 errors, (b) regression tests green, (c) mac+linux CI green
  on r1, (d) 14 desktop assets present, (e) this MCP end-to-end PASS,
  (f) **live-update gate** (install previous stable → update to r1 → verify) —

…report READY and **wait for explicit user "cut" / "ok" / "promote"**. Then run the
`cut-stable` skill (bump AppVersion to `2.46.0`, fresh no-suffix tag, rebuild with the SAME
`-SingBoxPath publish\sing-box-lx.exe -BundleSplitDriver` flags, restore Latest, delete r1).

---

## Dev-box cleanup note (Pavel's machine)

The post-ship install script killed the running `VPNRouter.App` (PID 23352) before I
stopped. As of last read-only check a fresh `VPNRouter.App` (PID 44040) + `sing-box`
(PID 31988) were running again — environment appears self-restored. If anything looks off,
just relaunch VPNRouter from the Start menu. The downloaded r1 ZIP sits in
`C:\Project\VPNRouter\.r-publish\` (gitignored, harmless).

---

## Pointers

- Release notes draft: `plans/release-notes-v2.46.0-r1.md` (gitignored)
- Open defects ledger (cut gate reads this): `plans/OPEN-DEFECTS.md`
- Feature goal: `plans/goal-true-split-tunnel-2026-07-03.md`
- Phase briefs: `plans/w1.1-p2-*`, `w1.1-p3-*`, `w1.2-*`, `w1.3-*`, `w1.4-packaging-brief.md`
- ABI pin: `plans/w1-driver-abi-reference-2026-07-03.md`
- Live-harness recipe + gotchas: memory `w1-mullvad-driver-spike`
