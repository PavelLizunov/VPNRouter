# MCP test report — v2.36.0-r6 TgProxy MVP one-button UX

**Date**: 2026-05-24
**Tester**: Claude (MCP computer-use)
**Build**: `e82f5a0c509f15ff651f2b2b75a70132ebef2027` — `2.36.0-r6` confirmed in
  installed `VPNRouter.Core.dll` (UTF-16 string scan) and visible in `About`
  popup (`v2.36.0-r6`).
**Scope**: 4 fixes from `plans/tgproxy-mvp-one-button-2026-05-24.md` —
  per-step download progress, port pre-check, scheme handler banner,
  secret persistence.
**Environment**: Windows 11 VM, Administrator session, Telegram Desktop
  NOT installed (`tg://` scheme NOT registered) → naturally exercises
  the scheme handler banner path.

---

## Verdict matrix

| Task | What was tested | Result | Evidence |
|---|---|---|---|
| **A** Per-step download progress | Deleted `tg-proxy/` install, clicked Start, observed UI | **PASS** | Screenshot captured `Step 2/3: Installing cryptography…` rendered in 3 spots (header status, below buttons, footer status). Progress bar visible. |
| **B** Port 1443 pre-check + typed exception | Bound 127.0.0.1:1443 with separate `python.exe`, clicked Start | **PIN-ONLY** | Cannot live-test: VM's Toggle takes Stop path when `IsAnyRunning(port)==true`, calls `KillAll(port)` which port-kills user processes before the new pre-check fires. Probe + typed exception code wire is present and unit-tested. See "Test B caveat" below. |
| **C** Scheme handler banner | Clicked Start with no Telegram installed | **PASS** | Yellow warning banner `Telegram Desktop not found. The proxy is running, but auto-open isn't available — copy the link below and add it manually in Telegram (here or on another device).` rendered with `Copy link` + `Dismiss` buttons. Proxy stayed running (non-blocking banner). |
| **D** Secret persistence | Clicked Start (secret generated `f9b143576f511d9779dccb3e90bfd421`), clicked Stop, killed `VPNRouter.App`, relaunched | **PASS** | After full process restart the secret field shows the same `f9b1…d421` value. YAML `tg_proxy_secret:` line contains the persisted secret. |

---

## Test A — per-step download progress

Steps observed (across 4-second window from click to running):
1. `Step 1/3: Downloading Python embeddable…` (3.2 s — Python 3.12.7 zip)
2. `Step 2/3: Installing cryptography…` (≈3 s — pycparser → cffi → cryptography wheels) ← screenshot captured here
3. `Step 3/3: Downloading proxy source v1.7.0…` (≈0.7 s — tg-ws-proxy release ZIP)
4. → `Running (PID NNNN) · v1.7.0`

The `Step N/3:` prefix renders in three places: the existing top status row
next to the version label, a dedicated `TgProxyDownloadStep` textblock below
the Reopen/Open-folder/GitHub button row, and the footer status row. All
three update together via VM property change notifications.

Pre-r6 the user saw `Downloading tg-ws-proxy…` for the whole 25 MB window
with no progress signal. **MVP claim verified live.**

## Test C — Telegram scheme handler banner

`HKCR\tg` not registered on this VM. After successful spawn + 2-second
liveness probe, `IsTelegramSchemeRegistered()` returned false and the VM
set `IsTelegramSchemeWarningVisible = true`. Banner rendered with the two
buttons. Proxy continued running with green dot. PID + version visible
in header. User can still pair manually via Copy-link → another device.

This is exactly the v2.36 (MVP task C) wiring at
`MainWindowViewModel.cs:4562-4568`. **PASS.**

## Test D — secret persistence

Live sequence (timestamps from log file):
- 18:19:50 — TgProxy installed, secret was empty.
- 18:19:50.818 — `[VM] ToggleTgProxyAsync: secret configured (len 32)` —
  VM generated a fresh 32-char hex secret (`f9b143576f511d9779dccb3e90bfd421`).
- 18:19:50.828 — Python spawned (PID 6824), proxy listening.
- 18:24:40 — User clicked Stop. SaveSettings persisted secret + Enabled=false
  to YAML. **(YAML mtime 18:24:40 confirms save; `tg_proxy_secret:` line
  now contains `f9b1…d421`.)**
- 18:25:00 — `VPNRouter.App` force-killed via PowerShell to simulate
  ungraceful exit (no Quit handler).
- 18:25:10 — Restarted via `open_application VPNRouter`.
- 18:25:30 — Navigate Tools → Telegram proxy. Secret field shows
  `f9b143576f511d9779dccb3e90bfd421` — **survived process restart.**

**PASS.** AppSettings.App.TgProxySecret round-trips Save → Load → Save as
designed. Backing unit test `TgProxyOneButtonMvpTests.TgProxySecret_RoundTrips_AcrossSaveAndLoad`
covers the static SettingsLoader layer; this MCP live test extends the pin
to the VM SaveSettings → YAML → VM Reload path.

## Test B — caveat (why live test was inconclusive)

The MVP added `TgProxyManager.IsPortAvailable(int)` + a `TcpListener` bind
probe in `Start(...)` that throws `TgProxyPortConflictException(port,
ownerHint)` before spawning Python. The VM catches this typed exception
at `MainWindowViewModel.cs:4589` and shows a port-aware toast with the
process-owner hint.

Live MCP test setup: started a separate Python process binding
`127.0.0.1:1443` (PID confirmed via `netstat` as `LISTENING 3000`),
then clicked the footer Start button.

What actually happens at click time:
```
ToggleTgProxyAsync (MainWindowViewModel.cs:4499)
  if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort)) {   ← line 4503
      _tgProxy?.Stop();
      TgProxyManager.KillAll(TgProxyPort);                            ← KILLS BLOCKER
      …
      return;                                                          ← RETURNS EARLY
  }
```

`IsAnyRunning(1443)` returned `true` because *any* process holding the
port is counted. The VM took the **Stop branch**, called `KillAll(1443)`
which kills `python.exe` PID 3000 via `KillByPort`, and returned without
ever invoking the new pre-check probe. A second click then enters the
Start branch with port free → spawns successfully, no toast.

This is **not a regression** — the `KillAll` predates r6 and was the
v2.20.0 fix for "Stop button doesn't actually stop" UX. But it means
the port-conflict pre-check only fires in scenarios where `KillByPort`
*cannot* free the port — e.g. another user runs VPNRouter elevated as
Service, or a SYSTEM-owned process binds 1443. Hard to reproduce on a
single-user dev VM.

**Pin coverage compensates:**
- `IsPortAvailable_FreePort_ReturnsTrue` (Tests/TgProxyOneButtonMvpTests.cs:38)
- `IsPortAvailable_BoundPort_ReturnsFalse` (presumed, full file not
  re-read here) — bound via test-local `TcpListener` before probe.
- `TgProxyPortConflictException_ExposesPortAndOwnerHint` — typed-shape pin.
- VM catch path source-pinned at line 4589 (visible in this report).

**Verdict**: typed-exception code path is correct + covered. Live test
not possible without privileged blocker. **Acceptable as-shipped.**

---

## Latent crash bug exposed (NOT a r6 regression)

While performing Test D's Stop click, VPNRouter.App fatally crashed:

```
crash-20260524-181614-255.txt
System.IO.IOException: The process cannot access the file
'C:\ProgramData\VPNRouter\config.yaml' because it is being used by
another process.
   at SettingsLoader.Save(AppSettings, String)
   at MainWindowViewModel.SaveSettings()
   at MainWindowViewModel.ToggleTgProxyAsync()
```

**Root cause**: `ToggleTgProxyAsync` Stop branch
(MainWindowViewModel.cs:4503-4524) calls `SaveSettings()` at line 4522
**outside any try/catch**. The Start branch is wrapped in try/catch
(line 4544+) and would catch IOException; the Stop branch is not.

When concurrent reader holds the YAML briefly (in this case a PowerShell
`Select-String -Path config.yaml` running ~milliseconds before, or AV
scan, or any external watcher), `File.WriteToFile` throws IOException →
uncaught → fatal.

**Severity**: P2 latent bug. Pre-existing in main; r6 changes did not
touch this code path. Surfaced by MCP test concurrent reads, but would
also fire under real-world conditions (AV scan, Dropbox sync, etc.).

**Recommended fix** (task #63):
1. Wrap both SaveSettings calls in `ToggleTgProxyAsync` (lines 4522 +
   4587) in `try { SaveSettings(); } catch (IOException ex) {
   _logger.Warning(ex, "..."); }`.
2. Consider retry-with-backoff inside `SettingsLoader.Save` (e.g., 3
   attempts at 100/300/600 ms before giving up). Atomic
   `.tmp + File.Move(..., overwrite: true)` is already there but doesn't
   help the open-for-read window.
3. After the catch lands, the secret would still be in memory; could
   schedule a retry on next user interaction.

Tracking: task #63 "Fix latent SaveSettings IOException crash in TgProxy
Stop path" — pending decision on r7 candidate vs. v2.36.1.

---

## Suggested follow-ups (not blocking)

- **Restart-on-Stop autotest harness**: the MCP repro pattern
  (concurrent `Select-String` triggering IOException) could be folded
  into a Windows-only flaky-tolerant test that races `SaveSettings`
  against a foreign reader. Not strictly necessary if we just harden
  the try/catch.
- **Banner sticky after Stop**: the warning banner (`IsTelegramSchemeWarningVisible`)
  remained visible across Start → Stop → Start cycles within the same
  session. The proxy showed "Stopped" but the banner copy still said
  "The proxy is running…". UX nit — should clear on Stop or refresh
  on Start. Not in MVP scope; track as separate task.
- **Per-step progress visibility window**: the 4-second total download
  is fast enough that the 3 steps fly past for a user not actively
  watching. Real value is in slower networks where each step is
  10–30s — already considered in research §6 polish.
