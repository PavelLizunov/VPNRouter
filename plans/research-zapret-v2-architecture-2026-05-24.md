# Zapret v2 architecture research

**Date**: 2026-05-24  
**Status**: Research only, no code changes  

---

## Context

Current Zapret (v1.x) wraps external Flowseal/zapret-discord-youtube (winws.exe) via Cygwin. Seven friction points identified. Question: polish v1, or design v2 from scratch?

---

## 1. Current architecture

### ZapretManager (ZapretManager.cs)

Manages winws.exe process lifecycle. Key insight: Cygwin requires real console (not pipe-redirected). Uses wrapper .bat with SET variables for path expansion. Phase 3+ spawns via `cmd.exe /c wrapper.bat` because ProcessRunner forces UseShellExecute=false. Detects AV quarantine via immediate exit <2s (fires ImmediateExitDetected event).

### ZapretUpdater (ZapretUpdater.cs)

Downloads Flowseal release (~3.5MB), extracts to ProgramData/zapret, manages versions. Stops WinDivert service before overwrite. Parses general*.bat files for strategies. Seven error categories (rate-limit, network, corrupted, invalid, filesystem, concurrent).

### ZapretActions (ZapretActions.cs)

Diagnostics helper methods: service checks, Discord cache clear, hosts/IPSet download, game filter modes. All wrapped via IProcessRunner (v3.0 Phase 2G refactor).

### DpiBypassPage UI (DpiBypassPage.axaml)

Master-detail layout, 5 sections: Status / Strategy / Hosts / Filters / Advanced. Sidebar combo shows 10+ cryptic strategy names from Flowseal releases.

### Strategy concept

Friction: 10+ abbreviations (ALT1, ALT3, hostfakesplit, TLS in SNI). Built-in legacy ("multisplit") or parsed from general*.bat. Auto-selected on start (default "Multisplit").

---

## 2. Options for v2

### Option A: Polish v1 (smart defaults + Flowseal)

- Replace combo: "Auto-detect" + "Advanced (show all)" toggle
- Auto-probe: try ALT3 -> if blocked try ALT4 -> persist
- Auto-install Discord hosts on first run
- Single "Update all" button (zapret + hosts + ipset)
- Per-step progress toast
- Pre-check AV whitelist path

**UX delta**: 70% friction removed  
**Effort**: 3-4 days  
**Risk**: AV + Cygwin remain  
**Maintenance**: Minimal  

---

### Option B: Roll our own (Windows native, WinDivert)

- Implement DPI bypass directly in C# via WinDivert P/Invoke
- Spawn as pure .NET process
- 1-2 core strategies

**UX delta**: 95%  
**Effort**: 30-60 days  
**Risk**: Very high (packet manipulation, Windows-version sensitive)  
**Maintenance**: Very high  

---

### Option C: Leverage sing-box DPI features (RECOMMENDED)

- sing-box 1.13+ has `tls_fragment` + `udp_fragment` outbound options
- Android (v2.32.0) already uses this via AndroidDpiBypassInjector.cs
- Pure JSON mutation, no external binary, no Cygwin, no AV
- Three modes: off / standard / aggressive
- Runs inside VPN tunnel; transparent

**Implementation**:
1. Port AndroidDpiBypassInjector logic to Windows
2. Add `app.dpi_bypass_mode` to AppSettings
3. Extend ConfigGenerator.Generate() to call injector
4. UI: ComboBox (Off/Standard/Aggressive) in DpiBypass
5. Settings migration: auto-migrate Flowseal users to "standard"

**UX delta**: 90%  
**Effort**: 7-10 days  
**Risk**: Low (tested on Android, sing-box maintains upstream)  
**Maintenance**: Low  

---

### Option D: Hybrid (auto-strategy daemon)

- Daemon auto-probes + persists winning strategy
- UI: "Auto" (default) + "Custom" override

**UX delta**: 60%  
**Effort**: 7 days  
**Risk**: Moderate (daemon adds complexity)  
**Maintenance**: Moderate  

---

## 3. Comparison

| Aspect | A | B | **C** | D |
|---|---|---|---|---|
| UX friction removed | 70% | 95% | **90%** | 60% |
| AV quarantine | High | None | **None** | High |
| External dep | Flowseal | WinDivert | **sing-box (already used)** | Flowseal |
| Maintenance | Minimal | Very high | **Low** | Moderate |
| Effort (days) | 3-4 | 30-60 | **7-10** | 7 |
| Cross-platform | Separate | Aligned | **Aligned** | Separate |

---

## 4. Recommendation

**Primary: Option C (sing-box native)**

Sing-box's tls_fragment + udp_fragment are battle-tested on Android (v2.32.0, shipped 2026-05-07), require zero external dependencies, and eliminate Cygwin's AV + console quirks. Effort is modest (7-10 days) because core logic already exists; Windows work is porting + UI + settings. Aligns desktop + Android, simplifies architecture.

**Secondary: Option A** if Windows DPI profiles require deeper tuning (safe fallback, 3-4 days).

**Reject B & D**: Option B is 30-60 days for Windows-only; defeats cross-platform. Option D keeps Cygwin risks + adds daemon.

---

## 5. Phased rollout (Option C)

### Phase 0: Spike (1-2 days)
- Port AndroidDpiBypassInjector to Windows
- Test on 2-3 ISPs: YouTube/Discord, CPU <5%, latency <50ms

### Phase 1: Opt-in (1 week)
- UI toggle: "Enable new sing-box mode"
- A/B telemetry
- Rolling candidate vX.Y.Z-rN

### Phase 2: Default for new installs (1 week)
- Fresh users -> sing-box
- Existing Zapret users -> stay on Flowseal (auto-migrate to "standard")
- Stable vX.Y.Z

### Phase 3: Deprecate v1 (future, 1 week)
- Remove Flowseal UI
- vX+1.0.0

---

## 6. Open questions (spike scope)

1. Windows DPI effectiveness: Do Discord/YouTube work with "standard" on Rostelecom/MegaFon/Beeline?
2. If sing-box params don't match Flowseal's tuning, add user-editable params in Advanced?
3. How many users have custom strategy choice? Do they map to sing-box equivalents?
4. sing-box version requirement: 1.13.0 or 1.13.10+?

---

## Success criteria (end of Phase 2)

- New installs default to sing-box (no Flowseal)
- Existing users auto-migrate seamlessly
- YouTube + Discord work >=95% on tested ISPs
- CPU <5%, latency <50ms p95
- Zero AV quarantine reports

---

## Related docs

- `research-one-button-tgproxy-zapret-2026-05-24.md` (UX improvements)
- `v2.32.0-android-zapret-port.md` (Android proof-of-concept)
- `phase3-iprocessrunner-zapretmanager-2026-05-21.md` (ZapretManager refactor)
