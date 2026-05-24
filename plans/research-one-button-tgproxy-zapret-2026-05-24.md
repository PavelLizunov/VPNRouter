# One-button TgProxy + Zapret — research proposal

**Authored**: 2026-05-24
**Type**: Pure research, no code changes
**Trigger**: User feedback "сейчас они слишком сложные для пользователя, нужно
продумать как сделать их в одну кнопку, типо чтоб происходила магия,
пользователь нажал одну кнопку и все заработало"
**Scope**: TgProxy + Zapret only (DPI bypass). VPN config-adding deferred.

---

## 1. TgProxy current flow

### Click-by-click (fresh user)

1. **Footer button** (`LblTgProxyMainAction`, TelegramPage.axaml:318): "Start & open Telegram"
2. Click → `TgProxyMainActionAsync()` (MainWindowViewModel.cs:4638). Detects not running → `SetupTgProxyAsync()`.
3. `SetupTgProxyAsync()` chain (lines 4585–4616):
   - **Step 1**: `ToggleTgProxyAsync()` calls `UpdateTgProxyAsync()` if not installed.
     - Downloads Python embeddable (~11 MB) + wheels (~10 MB) + source from ninitux/tg-ws-proxy.
     - Status: "Downloading tg-ws-proxy..." → "Installed v1.6.5".
   - **Step 2**: Spawn `python.exe -m proxy.tg_ws_proxy --port 1443 --secret <random-32-hex>`.
     - Auto-generates secret if empty (MainWindowViewModel.cs:1191).
   - **Step 3**: Opens `tg://proxy` deep-link in Telegram (TgProxyManager.BuildProxyLink).
4. Footer status: green dot + "Running [PID 1234]".
5. Return visit: button toggles to "Stop".

### Friction points

1. **State-flip not visible**: footer branches at runtime on `TgProxyEnabled`. Users don't see install vs run distinction until click (TgProxyMainActionAsync:4638).
2. **Download latency unmanaged**: 3-step ~25 MB download. Toast only at end. UX expectation = instant; reality = 30–90s on DSL.
3. **Secret auto-generation fragile**: TgProxySecret stays empty if install path fails mid-way (Start at TgProxyManager.cs:55 assumes populated).
4. **Telegram scheme check too late**: `IsTelegramSchemeRegistered` (TgProxyManager.cs:295) fires inside final deep-link. If tg:// not registered, user sees OS error dialog instead of pre-emptive toast.
5. **Port 1443 conflict silent**: no pre-check. Process exits in 2s watchdog (line 147) → "Error: process exited" with no port-specific hint.
6. **No rollback on partial failure**: Python downloads but wheels fail → half-installed directory, retry re-downloads Python.

---

## 2. Zapret current flow

### Click-by-click (fresh user)

1. **Master-detail layout**: Tools → Zapret. Sidebar: Status / Strategy / Hosts / Filters / Advanced. Default = Status.
2. **Status section**: description + grey "Stopped" badge + warning banner + "Run diagnostics".
3. Navigate to **Strategy section** (sidebar item 2):
   - Combo of 10+ cryptic names: "Multisplit", "Fake+Multisplit", "ALT1", "ALT2", "ALT3", "Flowseal", "hostfakesplit", "TLS in SNI", "Custom", etc.
   - "Update Zapret" button grayed out until installed.
4. Click "Update Zapret":
   - `UpdateZapretAsync()` (MainWindowViewModel.cs:4058–4091) fetches Flowseal/zapret-discord-youtube release (~3.5 MB ZIP), 3 retries.
   - Status: "Downloading zapret…" → "Extracting…" → progress bar.
5. Return to Status section footer:
   - "Start DPI Bypass" button (DpiBypassPage.axaml:438–446, bound to `ToggleZapretCommand`).
6. Click Start: `ToggleZapretAsync()` (MainWindowViewModel.cs:4137–4226).
   - Auto-selects first installed strategy (default "Multisplit").
   - ZapretManager.Start / StartFromBat (ZapretManager.cs:88–142) spawns `cmd.exe /c _vpnrouter_silent.bat`.
   - 1.5s delay, polls WinwsPid (line 4201).
7. Footer status: blue + "Running [multisplit] (PID 5678)". Return click → Stop.

### Friction points

1. **Strategy upfront**: 10+ cryptic names. Hidden default "Multisplit" never surfaced as recommendation. Power users want Flowseal smart routing must do **Hosts install → THEN Strategy select** (two-tab dance).
2. **Two-step Flowseal**: Install hosts (Hosts section) → select Flowseal strategy (Strategy section). Out-of-order = "winws.exe exited immediately".
3. **Silent AV quarantine**: `DetectImmediateExit` (ZapretManager.cs:232) fires when winws exits <2s. Shows toast + whitelist-path button (Bug-r9-G). Cold-start user doesn't know to check AV first.
4. **"Update Zapret" button confusing**: in Strategy section, near combo — does it update binary, strategy list, or version?
5. **Two ways to start**: footer button vs Strategy section (no direct start there). Footer not always visible on narrow windows.
6. **Separate "Update IPSet"** button: another download loop, no progress.
7. **Cygwin console quirks**: ZapretManager.StartCmdBat needs hidden console; any pipe-redirect escape = silent fail.

---

## 3. Proposed one-button UX

### TgProxy magic button

**Location**: Existing footer (TelegramPage.axaml:317–329) — no layout change.

**Behavior**:
- **First click (stopped)**: "Start & open Telegram"
  1. If not installed: download Python + wheels + source **with per-step progress toast** (3 sub-steps).
  2. Generate random 32-char secret if empty.
  3. Spawn proxy on port 1443.
  4. Open `tg://proxy` after 500ms.
  5. Green status: "Running [PID X]".
- **Subsequent (running)**: "Stop" kills proxy.
- **Defaults**: port 1443 hardcoded, secret random+persisted, Telegram scheme pre-checked at ctor.

### Zapret magic button

**Location**: New Tools → Zapret footer button matching TgProxy's style.

**Behavior**:
- **First click**: "Start DPI bypass"
  1. If not installed: download Flowseal (~3.5 MB) with progress.
  2. Auto-select recommended strategy (Flowseal-pinned or "ALT3").
  3. Optionally pre-install Discord hosts (default on, override in secondary UI).
  4. Spawn winws.exe.
  5. Blue status: "Running [strategy] (PID Y)".
- **Subsequent**: "Stop" kills winws.
- **Defaults**: Flowseal-recommended strategy; Discord hosts default on; IPSet auto-updates on every start (no separate button).

### Unified state machine (both)

```
IDLE (not installed)
  ↓ click
DOWNLOADING ←→ (per-step toast progress)
  ├─ error → IDLE + toast "Download failed: <reason>"
  └─ done → STARTING
STARTING
  ├─ spawn process
  ├─ wait 500-2000ms
  ├─ alive → RUNNING
  └─ dead → FAILED (toast "Start failed: <stderr tail>")
RUNNING (green/blue + PID)
  └─ click Stop → STOPPING
STOPPING (wait 3s) → IDLE
FAILED
  ├─ retry → DOWNLOADING or STARTING
  └─ close → IDLE
```

---

## 4. Implementation outline

### Reusable services (no changes)

- TgProxyUpdater.DownloadAsync — extend `StatusChanged` event with per-step progress.
- TgProxyManager.Start — already auto-generates secret.
- ZapretUpdater.DownloadAndExtractAsync — retry loop exists, add per-step status.
- ZapretManager.Start / StartFromBat — already handles strategy selection.

### New orchestrator surface (sketch)

**Option A (MVP)**: Extend ViewModel commands.
- Add `SetupTgProxyOneClickAsync()` + `SetupZapretOneClickAsync()`.
- Each: `if (installed) start else download → start`.
- Pros: minimal diff, reuses existing event subs.
- Cons: duplicates logic in SetupX + ToggleX.

**Option B (Polish)**: New orchestrator classes.
- `TgProxyOrchestrator` + `ZapretOrchestrator` own state machine.
- Single `EnsureRunningAsync()` idempotent method.
- ViewModel binds to orchestrator state.
- Pros: reusable across App+Service+CLI.
- Cons: +200–300 LOC abstraction.

**Recommendation**: A for MVP, B if refactor pressure builds.

### Persistence + recovery

- YAML adds: `tools.tgproxy.last_secret`, `last_port`, `tools.zapret.last_strategy`, `discord_hosts_enabled`.
- First-run: missing → defaults; present → reuse (user не re-pair'ит Telegram каждый launch).
- Crash recovery: existing `BootstrapAutostartAsync` (MainWindowViewModel.cs:2542) re-runs start chain, re-downloads if needed.
- Partial-install resume: check installation markers; resume from middle step.

---

## 5. Risks

### Network / install

1. **GitHub rate-limit**: 60 req/hour for unauthenticated IP. Two concurrent first-time users behind NAT = throttled. Mitigation: retry+exponential backoff (already in ZapretUpdater, mirror in TgProxyUpdater).
2. **Cold-start = 25 MB download**: 30–90s on slow links. Mitigation: pre-download prompt + progress + Cancel.
3. **Antivirus quarantine**: Cygwin + Python embeddable often flagged. Mitigation: show AV whitelist path (done for Zapret Bug-r9-G; same pattern for TgProxy Python).
4. **Wheels PyPI dependency**: occasionally slow/down. Mitigation: bundle wheels in VPNRouter release.

### Runtime

1. **TgProxy port conflict**: pre-check via netstat; prompt port change if 1443 taken.
2. **Zapret strategy/hosts mismatch**: declare hosts dependency in strategy metadata; prevent incompatible combos.
3. **Cygwin console quirk**: unit-test BuildCygwinLaunchBat (ZapretManager.cs:272) on every release; log full bat content on spawn for postmortem.
4. **2s startup timeout too tight**: increase to 3–5s; check actual TcpListener bind instead of process exit.

### State drift

1. **Settings reload clears tgproxy.secret**: never auto-reset persistent fields; only rotate on explicit "New secret".
2. **Service vs App ownership**: lock file or registry check before spawn; show "via service" if Service is running.

---

## 6. Recommended phasing

### MVP (~2-3 days)
- Unify TgProxy footer button (detect installed → branch download or start).
- New Zapret footer button (same pattern).
- Auto-select Zapret strategy on first run (Flowseal recommended).
- Per-step progress toast (not full progress bar yet).
- Reuse all existing services. No new abstractions.

### Polish (~3-4 days)
- Progress bar + ETA for downloads.
- Pre-download confirmation dialog for TgProxy (25 MB warning).
- Port conflict pre-check + retry for TgProxy.
- Telegram scheme handler pre-check; banner if missing.
- Zapret strategy metadata (hosts dependency).
- Unit tests for state machine + rollback.
- Settings migration: persist last-used strategy + secret.

### Stretch (~2-3 days, each decoupled)
- Undo button: "Stop and roll back" if user wants to change settings.
- Telemetry: first-run duration, success rate, failure reasons.
- Smart retry: rate-limit → 30 min auto-retry.
- Auto-update: weekly background poll for new Zapret / TgProxy releases.

---

## Summary

**TgProxy**: уже почти 1-click. Footer button делает download + start + open Telegram. Главные friction: latency feedback (per-step progress), port conflict pre-check, Telegram scheme handler pre-check. Low-hanging fruit.

**Zapret**: требует design change. User должен выбрать strategy upfront (confusing), потом отдельно start. Proposal: переместить strategy selection в secondary UI; footer button становится 1-click (download + auto-select strategy + start). Friction: AV quarantine (already mitigated), strategy/hosts compatibility (нужна metadata).

**Unified UX**: оба кнопки в Tools footer, same color/style, same state machine. Reduce cognitive load: "одна кнопка = полностью сконфигурировано + работает".

**Build on existing**: TgProxyUpdater, ZapretUpdater, TgProxyManager, ZapretManager, ZapretActions — все reusable. MVP — ViewModel-only wiring.
