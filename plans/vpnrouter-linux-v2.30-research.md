# Linux research — v2.30 backlog

**Trigger**: 2026-04-29 user direction «после пре-релиза разбираешься
с linux». User reported on v2.29 cycle:
- Linux Stop button doesn't kill sing-box (fixed in r5/r6 — 3-step
  escalation chain).
- Linux update doesn't auto-restart (fixed in r5/r6 — detached sh
  helper).

Both fixes are in r6+ binary; **need user verification** they actually
work end-to-end on a real Linux machine. This doc captures: (a) what
to verify after r6 lands, (b) Linux-specific issues we know of from
prior plans (`vpnrouter-linux-port-research.md`,
`vpnrouter-v2.22-linux-hardening.md`), (c) new investigation items
that surfaced during v2.29 cycle.

---

## Part 1 — Verification of r5/r6 Linux fixes (P0 manual test)

### r5/r6 fix: Stop escalation chain

**File**: `VPNRouter.Core/Services/SingBoxManager.cs` lines 109-200
+ new helper `LinuxStopEscalationChain` at line 619.

**Verify**:
1. Connect VPN on Linux (Connect button or CLI start).
2. `pgrep -f sing-box` → should show 1 PID.
3. Stop via UI button.
4. `pgrep -f sing-box` → empty within 3 s.
5. Open `~/.config/vpnrouter/logs/vpnrouter-*.log` → search for
   `Linux stop:`. Should see one of:
   - `user pkill -TERM succeeded` (capability mode / .deb install)
   - `pkexec pkill -KILL succeeded` (pre-r6 path or capability missing)
   - `sudo -n pkill -KILL succeeded` (NOPASSWD sudoers configured)

**Failure modes to capture**:
- All 3 steps fail → log says "ALL escalation steps failed". Report
  pgrep output + manual `sudo pkill` result.
- pkexec dialog appears but user dismisses → step 2 returns 126,
  fallback to step 3 should kick in. Verify in log.

### r5/r6 fix: detached relaunch helper

**File**: `VPNRouter.Core/Services/UpdateChecker.cs` lines 619-696
(`ApplyUpdateLinux` rewritten to use `/tmp/vpnrouter-relaunch-<pid>.sh`).

**Verify**:
1. Install r6 .deb (or AppImage / tarball user-extracted).
2. (Future r7+ release available) click [Update] in UI.
3. Old process exits.
4. New process should start automatically WITHOUT manual launch.
5. Open `/tmp/vpnrouter-relaunch-<pid>.log` (helper logs to /tmp).
   Should see:
   - `vpnrouter-relaunch helper started, parent=NNNNN`
   - `parent gone, launching /opt/vpnrouter/VPNRouter.App`
   - `setsid returned 0`
6. `pgrep VPNRouter.App` → should show new PID.

**Failure modes to capture**:
- Helper file `/tmp/vpnrouter-relaunch-*.sh` left over → cleanup
  failed. Acceptable; tmpwatch handles.
- Helper log shows `setsid returned 127` → `/usr/bin/setsid` missing.
  Check util-linux package install.
- Helper log shows `parent gone, launching ...` but no new PID →
  binary launched but crashed silently. Check
  `~/.config/vpnrouter/logs/vpnrouter-*.log` for crash dump.

---

## Part 2 — Open issues from prior plans

### 2.1 — AppImage auto-update (still deferred since v2.22)

**Source**: `vpnrouter-v2.22-linux-hardening.md` "Step 2.5 — AppImage
path still deferred".

**Status**: AppImage installs hit `ApplyUpdateLinux` and abort with
"AppImage auto-update is not yet supported. Please download the new
VPNRouter-linux-x86_64.AppImage manually". Still true in v2.29.

**Why deferred**: replacing a FUSE-mounted AppImage while it runs is
non-trivial. Solutions:
1. **AppImageUpdate / zsync** — official tool, requires `.zsync` files
   alongside each AppImage release. ~2 hours setup + workflow.
2. **Manual replace + relaunch** — download new AppImage to
   `~/Applications/`, exec it after parent exit. Simpler but loses
   `chmod +x` / xattrs.

**Recommendation**: do AppImageUpdate + zsync in v2.30. Steps:
- `appimagetool` already used in `build-linux.yml` workflow → add
  `--guess` to enable zsync.
- Workflow uploads `.AppImage.zsync` alongside `.AppImage`.
- ApplyUpdateLinux detects AppImage path, shells out to
  `AppImageUpdate $self.AppImage` instead of in-process copy.
- AppImageUpdate handles the FUSE remount.

**Estimate**: 4-6 hours (workflow + ApplyUpdateLinux branch + test
harness).

### 2.2 — systemd user service (alt to XDG autostart)

**Source**: v2.29.0-r2 implementation chose XDG `.desktop` autostart.

**Question**: should we ALSO support systemd user service for users
on headless machines / Wayland-only sessions where XDG autostart is
flakier?

```
~/.config/systemd/user/vpnrouter.service:

[Unit]
Description=VPNRouter
After=graphical-session.target

[Service]
ExecStart=/opt/vpnrouter/VPNRouter.App --minimized
Restart=on-failure

[Install]
WantedBy=default.target
```

`systemctl --user enable vpnrouter` activates.

**Pros**: works headless, restart-on-failure, journalctl logs.
**Cons**: more moving parts, doesn't fit XDG sessions perfectly,
needs `loginctl enable-linger <user>` for non-graphical bootup.

**Recommendation**: NOT in v2.30. XDG covers 95 % of users. Only add
systemd path if a real user reports XDG broken on their setup.
Keep on backlog.

### 2.3 — `apt-get upgrade` triggers Update notification

**Possible bug**: when user runs `sudo apt-get upgrade` and gets the
new vpnrouter .deb, the running app's `ApplyUpdateLinux` flow is not
involved (apt does its own dpkg replace). But the receipt-based
"didn't update" check might fire incorrectly the next time the app
starts after the deb-replace, because the receipt was never written
(no in-app Update button click).

**Verify**: install r6 .deb manually via apt → restart app → check
that no false "update didn't take effect" warning is logged.

**Status**: probably already correct because CheckInstallReceipt
returns null when receipt is missing. But worth confirming.

### 2.4 — sing-box upstream version drift

**Source**: bundled sing-box is 1.13.10. Linux releases an upstream
sing-box update every 1-3 months. v2.30 should bump to latest stable
(check at release time).

**Action**: at v2.30 ship time, verify
`https://github.com/SagerNet/sing-box/releases/latest` and update
`build-singbox.ps1`'s SingBoxVersion default. Also update build-mac.sh
+ build-linux.sh equivalents.

---

## Part 3 — New investigation items from v2.29 cycle

### 3.1 — Linux receipt warning never user-visible

CheckInstallReceipt returns a warning string; on Windows + Linux it's
logged via Serilog (`App.axaml.cs:54`). UI banner wiring is missing.

User on Linux who has a failed update won't see the warning unless
they tail the log file. After repeated update failures (which v2.29.0
demonstrated CAN happen), this is a UX gap.

**Action for v2.30**: wire UI banner via `MainWindowViewModel.LastUpdateWarn`
property bound to a `<Border IsVisible="..."/>` at the top of
MainWindow. Pattern: same as the existing SafeMode banner (`Program.SafeMode`).

**Estimate**: 1.5 h — single new property + binding + dismiss button.

### 3.2 — Linux stop log lacks per-step timing

The new escalation chain in r5/r6 logs each step but doesn't record
how long each took. If we see "user pkill -TERM" succeed but it
actually took 5 s (slow process exit), that's diagnostic gold for
later.

**Action for v2.31**: add Stopwatch around each `TrySpawnAndWait`
call, log `elapsed=NNN ms`.

**Estimate**: 30 min.

### 3.3 — `polkit-pkexec` missing detection at startup

Some minimal Linux distros (Alpine, headless servers) don't have
polkit. The pre-flight detection should warn "pkexec not found —
auto-update + manual restart will fail. Install policykit-1." Currently
we discover this only when the user clicks Stop and watches the
escalation chain fall through to sudo.

**Action for v2.30**: add to `HealthCheck.cs` startup probes a check
for `/usr/bin/pkexec`. Warn if missing.

**Estimate**: 15 min.

### 3.4 — Tray icon stable on Wayland?

`vpnrouter-linux-port-research.md` flagged Avalonia 11.3 `TrayIcon`
needs verification on Wayland sessions (GNOME 45+ which is Wayland-
default). XDG autostart-ed VPNRouter goes to tray; if tray icon
doesn't render, user can't access UI without `pgrep`.

**Action for v2.30**: smoke-test on Fedora 39 Wayland session +
Ubuntu 23.10 Wayland session. If broken, fall back to a window
restoration via DBus signal or similar.

**Estimate**: 2-4 hours (test setup + potential fallback impl).

### 3.5 — `update-helper` polkit policy file install path

`packaging/linux/com.vpnrouter.update.policy` should land at
`/usr/share/polkit-1/actions/com.vpnrouter.update.policy`.

**Verify**: install r6 .deb, run
`ls -la /usr/share/polkit-1/actions/com.vpnrouter.update.policy`.
File should exist. If missing, the privileged update path falls back
to inline pkexec which prompts for password each time.

---

## Recommended sequence

| Phase | Item | Effort | Priority |
|---|---|---|---|
| **r7+ user test** | 2.1, 2.2 — verify Stop escalation + detached relaunch on real Linux | 30 min | P0 |
| **v2.30** | 3.1 — UI banner for receipt warning | 1.5 h | P1 |
| **v2.30** | 3.3 — startup polkit-pkexec detection | 15 min | P1 |
| **v2.30** | 3.4 — Wayland tray smoke-test | 2-4 h | P1 |
| **v2.30** | 2.1 — AppImage zsync auto-update | 4-6 h | P2 |
| **v2.30** | 3.5 — polkit policy install path verify | 15 min | P2 |
| **v2.31** | 3.2 — per-step timing in Stop log | 30 min | P3 |

**Total v2.30 Linux work**: ~10-15 hours. Mostly bite-sized fixes.

## Carry-forward / not in scope

- systemd user service (2.2) — defer until user reports XDG broken.
- sing-box upstream bump (2.4) — automatic per-release; not its own task.
- apt-get upgrade interaction (2.3) — probably no-op; verify only.

## Cross-references

- `plans/vpnrouter-linux-port-research.md` — original (v2.21) port plan.
- `plans/vpnrouter-v2.22-linux-hardening.md` — v2.22 hardening cycle.
- `plans/vpnrouter-update-reliability-strategy.md` — generic update
  reliability layers; Layer 7 (install receipts on Linux) already
  shipped.
- `VPNRouter.Core/Services/UpdateChecker.cs` — `ApplyUpdateLinux` +
  `CheckInstallReceipt`.
- `VPNRouter.Core/Services/SingBoxManager.cs` — `LinuxStopEscalationChain`
  (added in r5/r6).
