# Research: One-button Zapret with auto-strategy detection (deep)

**Authored**: 2026-05-24
**Type**: Pure research, no code changes
**Trigger**: Post v2.36.0-r7 TgProxyOneTap ship — user wants the same magic
treatment applied to Zapret. Zapret has 3× more moving parts (strategy + hosts
+ filters + ipset + per-ISP variance), so a literal copy of TgProxyOneTap
needs auto-strategy logic to be honest.

**Sibling research** (read before this — do not re-derive their conclusions):
- `plans/research-one-button-tgproxy-zapret-2026-05-24.md` — baseline UX inventory + friction list
- `plans/research-rkn-block-checker-auto-strategy-2026-05-24.md` — rejected rkn-block-checker, recommended 5-LOC tactical round-robin
- `plans/research-zapret2-bolvan-migration-2026-05-24.md` — deferred zapret2 migration 3-6 months
- `plans/research-zapret-auto-probe-methodology-2026-05-24.md` — companion doc, focuses on the probe engine itself

---

## 1. End-to-end UX inventory — current Zapret flow

### 1.1 Master-detail layout (`DpiBypassPage.axaml`)

The Zapret page sits inside Tools tab via `ToolsPage.axaml:37`. It's a master-detail with 5 sidebar sections (`DpiBypassPage.axaml:50-59`):

1. Status
2. Strategy
3. Hosts
4. Filters
5. Advanced

…plus a sticky footer bar (`DpiBypassPage.axaml:419-447`) with status dot + status text + compact toggle button. The footer drives `ToggleZapretCommand` (`MainWindowViewModel.cs:4203`).

### 1.2 Click-by-click (first-time user)

| # | Step | Where | What the user must do | Friction |
|---|---|---|---|---|
| 1 | Land on Status tab | `DpiBypassPage.axaml:83` | Read 4 lines (description + warning banner + grey Stopped pill) | Cognitive: «что такое DPI bypass?» (`LblDpiDescription` at `MainWindowViewModel.cs:2387`) |
| 2 | Navigate to Strategy | `DpiBypassPage.axaml:55` | Click sidebar item 2 | Discoverability: footer button doesn't hint that strategy is missing |
| 3 | Click "Скачать" / "Download" | `DpiBypassPage.axaml:221`, command `UpdateZapretCommand` (`MainWindowViewModel.cs:4119`) | Wait 5-60 s for ~3.5 MB ZIP from GitHub | Indeterminate ProgressBar only; no byte count, no ETA |
| 4 | Choose strategy from ComboBox | `DpiBypassPage.axaml:215`, items from `ZapretUpdater.ParseStrategies` (`ZapretUpdater.cs:652`) | Pick one of ~18 cryptic names | No guidance; sort heuristic at `ZapretUpdater.cs:681-694` pins «general (ALT3)» first as proven, but UI leads with cryptic name without a "recommended" tag |
| 5 | (Optional) Navigate to Hosts | sidebar item 3 | Decide whether to install Discord hosts and/or Flowseal hosts | Dependency on use-case is opaque |
| 6 | Click "Добавить Discord hosts" | `DpiBypassPage.axaml:268`, command `ToggleDiscordHostsCommand` (`MainWindowViewModel.cs:4437`) | UAC prompt → modifies hosts file with 200 finland*.discord.media entries (`HostsManager.cs:110-114`) | Requires admin elevation. No warning on the button — user is surprised when UAC pops |
| 7 | (Optional) Click "Установить Flowseal hosts" | `DpiBypassPage.axaml:284`, command `ToggleFlowsealHostsCommand` (`MainWindowViewModel.cs:4365`) | Download `Flowseal/.service/hosts` from GitHub + merge | Same UAC. Two distinct hosts toggles confuse users |
| 8 | (Optional) Navigate to Filters | sidebar item 4 | Decide game filter (Off / TCP+UDP / TCP / UDP) and IPSet filter (Any / Loaded / Off) | 4×3 = 12 valid combinations. None documented in-app. `ZapretActions.SetGameFilterMode` writes a flag file (`ZapretActions.cs:312`) that `service.bat load_game_filter` reads on next start |
| 9 | (Optional) Click "Update IPSet list" | `DpiBypassPage.axaml:237`, command `UpdateIpSetListCommand` (`MainWindowViewModel.cs:4388`) | Download `Flowseal/.service/ipset-service.txt` into `lists/ipset-all.txt` | Separate button on Strategy section (post ZAPRET-2 consolidation) |
| 10 | Return to footer | `DpiBypassPage.axaml:438` | Click "Запустить обход DPI" / "Start DPI Bypass" | If steps 5-9 done wrong, winws.exe exits in <2 s → AV-block toast appears (`DpiBypassPage.axaml:93-125`) even when not actually AV. Bug-r9-G `ImmediateExitDetected` (`ZapretManager.cs:64-73`) is tuned for AV kill but mis-fires on strategy mismatch |
| 11 | Verify start | `MainWindowViewModel.cs:4266-4282` | Wait 1.5 s, check `WinwsPid` | Time-to-running ≈ 5 s. No HTTP probe confirms blocked sites unblock |

### 1.3 Friction summary

| # | Friction | File:line | New observation |
|---|---|---|---|
| F1 | Strategy combo has 18 entries, no recommendation visible | `MainWindowViewModel.cs:4087-4116` | Sort heuristic at `ZapretUpdater.cs:681-694` already does ALT3-first; UI doesn't expose «recommended» badge |
| F2 | Two-step Flowseal: hosts → strategy | `DpiBypassPage.axaml:281-296` then `:215-218` | Independent toggles. Order matters at runtime but not enforced |
| F3 | AV-block toast mis-fires on strategy mismatch | `ZapretManager.cs:232-245` | 2 s window catches both AV-kill (correct) and strategy-error fast-exit (false positive) |
| F4 | "Update Zapret" button ambiguity | `DpiBypassPage.axaml:221-228` | Adjacent to ComboBox + IPSet button = visual clutter |
| F5 | Strategy section contents are «версия и стратегия + IPSet списки» | `DpiBypassPage.axaml:438` | Section caption «Стратегия» misleads |
| F6 | Separate "Update IPSet" button | `DpiBypassPage.axaml:237` | Could be folded into the start orchestrator (idempotent fetch + cache 24h) |
| F7 | Cygwin console quirks | `ZapretManager.cs:213-223` | Stable now, not user-facing friction |
| F8 | No auto-strategy / no HTTP probe | n/a — feature missing | `ZapretActions.RunTests` launches `utils/test zapret.ps1` in a separate window — useless for in-app validation |
| F9 | Strategy/hosts coupling invisible | n/a — feature missing | Discord voice = hosts mandatory; YouTube = strategy alone. Page doesn't surface use-case → required-deps mapping |

---

## 2. Dependency graph by use-case

The key insight: **different use-cases need different combinations of strategy + hosts + filters + ipset**. A single button can only be "magic" if it covers the union of top use-cases by default.

| Use-case | Strategy req | Hosts (Discord) | Hosts (Flowseal) | Game filter | IPSet | Notes |
|---|---|---|---|---|---|---|
| Generic web (blocked sites) | Any SNI-bypass strategy (general, ALT, ALT2, ALT3) | No | Optional | Off | Any | 85% of presets cover this |
| YouTube unblock | Any strategy with SNI fragmentation (general, ALT3) | No | No | Off | Any | Strategy alone. Adding hosts breaks YouTube |
| Discord voice (finland*.discord.media) | Strategy + Discord hosts | **Yes (mandatory)** | Optional (overlaps) | Off | Any | Without hosts, voice silently fails |
| Discord text/embed | Strategy with `--hostlist-domains=discord.media` block | No | No | Off | Any | Same SNI bypass as web |
| Games (Valorant, CS, Fortnite UDP) | UDP-aware strategy (`general (ALT3)` ships UDP filter) | No | No | **TCP+UDP or UDP** | Any | Game filter flag tells `service.bat` to expand `%GameFilterTCP%`/`%GameFilterUDP%` placeholders |
| Mixed (web + Discord voice + games) | UDP-aware strategy + Discord hosts | Yes | No | TCP+UDP | Any | **The "default magic" target — 80% of users** |
| ISP-specific stuck case | Different strategy variant (ALT vs ALT2 vs ALT3) | depends | depends | depends | depends | Strategy effectiveness varies by region/ISP |

**Conclusion**: a single magic button **can** cover use-cases 1-6 with a single default config (UDP-aware strategy + Discord hosts + game filter TCP+UDP). Use-case 7 requires auto-strategy fallback or manual override.

---

## 3. Auto-strategy detection — methodology

Survey of 5 approaches. Detailed probe-engine design lives in the companion
doc `plans/research-zapret-auto-probe-methodology-2026-05-24.md` — read it
for the probe-set selection, tier schema, and class layout. This section
summarises the comparison and picks an option.

### 3.1 Options

**A. Round-robin top-3 Flowseal presets + HTTP HEAD probe**
- Start `general (ALT3)` → wait 30 s → HEAD-probe youtube.com → if fail, switch to `general` → retry → `general (ALT)` → retry.
- Worst case 93 s, 70-80% coverage.
- ~50 LOC.

**B. Parallel probe with 3 simultaneous winws.exe on isolated ports**
- WinDivert is a kernel singleton — multiple winws.exe attaches collide.
- **Not viable.**

**C. Persisted user-vote crowdsource**
- Backend cost + privacy concerns.
- Deferred indefinitely.

**D. Profile-based static catalog**
- JSON catalog `data/zapret-profiles.json` keyed by ASN.
- ~50 LOC reader + ASN lookup.
- 60% coverage (only ISPs in catalog get curated default).

**E. Hybrid (D + A fallback) — RECOMMENDED**
- Catalog hints the seed order; round-robin handles the long tail.
- MVP ships A only (round-robin); polish adds D.

### 3.2 Comparison

| Aspect | A | B | C | D | **E** |
|---|---|---|---|---|---|
| MVP LOC | ~50 | ~200 | ~200 + backend | ~80 | ~80 |
| Wall time (90th-pct) | 93 s | 5 s | <1 s | <1 s | 33-93 s |
| Coverage | 70-80% | 90% (blocked by WinDivert singleton) | 95% mature | 60% | 70-95% improving |
| Backend dep | No | No | Yes | No (CI cron) | No |
| Privacy | None | None | ASN+country | None | None |
| WinDivert singleton issue | None | **Blocker** | None | None | None |

### 3.3 Recommendation: **E — Hybrid (Catalog-then-RoundRobin)**

Ship A (round-robin) for MVP. Add D (catalog) in polish phase. Defer C and B indefinitely.

Implementation sketch (new `VPNRouter.Core/Services/ZapretAutoStrategy.cs`):

```csharp
public static class ZapretAutoStrategy
{
    private static readonly string[] DefaultOrder = new[]
    {
        "general (ALT3)", "general", "general (ALT)"
    };

    public static async Task<string?> ProbeAndPickAsync(
        IReadOnlyList<ZapretStrategy> available,
        ZapretManager zapret,
        IHttpClient http,
        IProgress<string> progress,
        CancellationToken ct)
    {
        for (int i = 0; i < DefaultOrder.Length; i++)
        {
            var name = DefaultOrder[i];
            var preset = available.FirstOrDefault(s => s.Name == name);
            if (preset == null) continue;

            progress.Report($"Тестирую стратегию ({i+1}/3): {name}");
            zapret.StartFromBat(preset.BatPath!, preset.Arguments);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            if (await ProbeYouTube(http, ct))
                return name;

            zapret.Stop();
        }
        return null; // all failed
    }
}
```

Probe helper: HEAD `https://www.youtube.com/` with 5 s timeout. Success = 200/301/302/403 (any non-network-error proves DPI bypassed). For deeper probe-set rationale see companion doc.

---

## 4. Hosts + filters + ipset auto-config

For each subsystem, decide **default ON / default OFF**, covering 80% of users without surprises.

### 4.1 Discord hosts — **Default ON**
- Discord voice is the #2 use-case (after generic web). Without hosts, voice silently fails.
- Cost: UAC prompt + 200-line hosts file edit + DNS flush (`HostsManager.cs:294-314`).
- Existing marker `# === VPNRouter Discord hosts START ===` makes uninstall idempotent.
- Override in expander.

### 4.2 Flowseal hosts — **Default OFF**
- Flowseal hosts list is a superset that occasionally maps non-Discord hosts; can backfire if IPs rotate upstream. Discord hosts alone are 95% of the value.
- Override in expander.

### 4.3 Game filter — **Default TCP+UDP**
- Covers Valorant/CS/Fortnite UDP AND TCP games. Small CPU bump.
- ALL bundled `general (ALT*)` .bat files have the `--filter-tcp=%GameFilterTCP%`/`--filter-udp=%GameFilterUDP%` placeholders.
- Setting flag file = no .bat regeneration.

### 4.4 IPSet — **Default: Any (all traffic)**
- Loaded mode requires `ipset-all.txt` populated. If empty + Loaded → winws filters nothing.
- Magic button forces Any. User can flip to Loaded with auto-update.

### 4.5 Default config summary (MVP magic button)

```
strategy:    "general (ALT3)" (probed; fallback to general, general (ALT))
hosts:       Discord ON, Flowseal OFF
game filter: TCP+UDP
ipset:       Any
```

Footprint: hosts file +200 entries, 1 file write to `utils/game_filter.enabled`, no IPSet list download. Total disk delta <10 KB. UAC prompt = 1 (hosts install).

---

## 5. State machine for the Zapret magic button

```
IDLE
  ├── installed=false → click "Включить обход" →
  │     ▼
  │   DOWNLOADING  (ZapretUpdater.DownloadAndExtractAsync)
  │     ├── progress: "Загрузка zapret X.Y.Z…" (1/3 ZIP, 2/3 extract, 3/3 install)
  │     ├── error → IDLE + toast
  │     └── done → CONFIGURING
  │
  └── installed=true → click "Включить обход" → CONFIGURING
        ▼
      CONFIGURING (hosts install + flag-file writes, fast)
        ├── needs UAC (Discord hosts) → request elevation → proceed
        ├── error → IDLE + toast
        └── done → PROBING
            ▼
        PROBING                                   ←─┐
          ├── attempts: i in {0, 1, 2}              │
          │   ├── spawn winws.exe (StartFromBat)    │
          │   ├── Bug-r9-G ImmediateExitDetected    │
          │   │     fires → skip 30s wait, fast-fail to next i
          │   ├── wait 30s                          │
          │   ├── HEAD probe youtube.com           │
          │   │   ├── 2xx/3xx → break, jump to RUNNING
          │   │   └── fail → Stop, i++ → continue ─┘
          │
          ├── all 3 attempts failed → FALLBACK_MANUAL_PICK
          └── i succeeded → RUNNING

      RUNNING (green dot, strategy name shown)
          └── click "Стоп" → STOPPING

      STOPPING (ZapretManager.Stop, 3 s timeout) → IDLE

      FALLBACK_MANUAL_PICK
          ├── show: "Стратегия не подобрана автоматически"
          ├── unchanged: winws.exe still running with last-tried strategy
          ├── secondary UI: ComboBox for manual override
          └── footer: "Стоп" → STOPPING
```

6 states + 1 transient. Matches TgProxyOneTap's 5-state machine plus one new (PROBING) for multi-attempt verification.

---

## 6. UX surface — hero card sketch

Mirror `TelegramPage.axaml:88-327` (TgProxyOneTap hero):

1. **Per-step progress textblock** (visible during DOWNLOADING / CONFIGURING / PROBING):
   - Downloading: «Загрузка zapret X.Y.Z (2/3)…»
   - Configuring: «Установка Discord hosts… (потребуется UAC)»
   - Probing: «Тестирую стратегию (1/3): general (ALT3)…»

2. **Hero card** with radial-gradient accent glow:
   - **Icon**: shield-with-bolt SVG (DPI-bypass metaphor). 46×46, accent-border.
   - **Title** (flips state):
     - Stopped: «Обход блокировок»
     - Probing: «Подбираю стратегию…»
     - Running: «Активна стратегия: general (ALT3)»
     - Fallback: «Стратегия не подобрана»
   - **Lede** (flips state):
     - Stopped: «Включаем zapret, ставим Discord hosts, подбираем рабочую стратегию автоматически.»
     - Probing: «Тестирую (1/3): general (ALT3) — пробую открыть youtube.com…»
     - Running: «YouTube, Discord и другие заблокированные сервисы работают через локальный bypass.»
     - Fallback: «Все 3 стратегии не сработали. Открой Дополнительно или @vpnrouter_support.»
   - **Magic button** (big primary, accent-solid):
     - Stopped: «Включить обход блокировок»
     - Running: «Остановить обход»
     - Disabled during downloading/probing
   - **3 step chips** (only when stopped):
     - 1: «скачаем zapret»
     - 2: «настроим hosts»
     - 3: «подберём стратегию»
   - **Strategy probing chips** (only when probing) — three chips that fill in:
     - 1: ALT3 [pending / active / ok / fail]
     - 2: general [pending / active / ok / fail]
     - 3: general (ALT) [pending / active / ok / fail]
   - **Air-pill** (only when running):
     - Green dot + «В эфире · {strategyName} · PID {n}»

3. **Toast banner** — re-use `ZapretAvBlockToast` for AV-block + general status toasts.

4. **Manual-pick UI** (only in FALLBACK_MANUAL_PICK state):
   - ComboBox of all available strategies.
   - "Применить" button.

5. **"Дополнительно" expander** (collapsed by default) stows all r5-era controls:
   - Discord hosts toggle (default ON)
   - Flowseal hosts toggle (default OFF)
   - Game filter dropdown (default TCP+UDP)
   - IPSet dropdown (default Any) + Update IPSet button
   - Strategy override ComboBox (preserves access to all 18 presets)
   - Custom args TextBox
   - Update Zapret button
   - Run diagnostics / Run tests / Clear Discord cache / Remove service / Open folder / GitHub link

**No information loss vs current page** — every control survives, just stowed.

### Sidebar fate

The 5-section sidebar **goes away**. Hero card replaces Status; «Дополнительно» absorbs Strategy / Hosts / Filters / Advanced.

### Footer fate

The footer bar **stays**. Two reasons:
- Muscle memory: TgProxyOneTap kept its footer.
- Always-visible status on narrow windows.

---

## 7. Risk + phasing

### 7.1 Risks

| Risk | Likelihood | Severity | Mitigation |
|---|---|---|---|
| HEAD probe to youtube.com confused by residual cache / DoH | Low | Medium | Probe with `Cache-Control: no-cache` + cache-busting query string |
| 30 s × 3 = 93 s feels slow on a "magic" button | Medium | High | Per-attempt progress chips; user can click "Стоп" mid-probe to bail |
| AV-block toast (Bug-r9-G) fires on strategy-mismatch fast-exit | High | Low | Suppress Bug-r9-G during PROBING; probe loop handles fast-exits as "next strategy" |
| UAC fatigue: Discord hosts always prompts on first run | High | Medium | Bundle hosts install with elevated VPN spawn (run once on first session) |
| WinDivert singleton fight during probe | Medium | High | Add 500 ms extra delay after Stop before next Start |
| IPSet=Loaded but list empty → probe always fails | Low | Low | Magic button forces IPSet=Any; warn in expander if user flips |
| HTTP probe burns 30s waiting on a clearly-failed strategy | Medium | Low | Use `ImmediateExitDetected` to skip the 30 s wait — cut probe time to ~10 s per failed attempt |
| Catalog stale: pinned strategy stops working as TSPU evolves | Medium | Medium | Auto-update catalog from GitHub on app boot (polish phase) |
| User on Linux/macOS clicks magic button | Low | Low | Card states «Только Windows» banner + button disabled |
| Two zapret instances coexist during refactor | Low | High | Same UX surface, same VM command names — change is XAML-only + new orchestrator method |

### 7.2 Phasing — 3-tier (matching TgProxyOneTap precedent)

**MVP (~2-3 days)**

- Replace `DpiBypassPage.axaml` master-detail with TgProxyOneTap-style hero card + footer (preserve footer).
- New VM method `SetupZapretOneClickAsync()` orchestrates: download (if missing) → Discord hosts install (default ON) → game filter TCP+UDP (default) → spawn winws.exe with `general (ALT3)` → wait 30 s + HEAD probe → escalate to general / general (ALT) on fail.
- New `VPNRouter.Core/Services/ZapretAutoStrategy.cs` static class (probe orchestrator + HEAD helper).
- 3-step progress chips in hero card (pre-probe) + 3 attempt chips (during probe).
- Suppress Bug-r9-G AV-toast during PROBING.
- «Дополнительно» expander stows ALL existing controls (no removals).
- New strings: `ZapretOneTapTitle*`, `ZapretOneTapLede*`, `ZapretOneTapStep1/2/3`, `ZapretOneTapProbeAttempt(n, total, strategy)`.

Acceptance:
- [ ] First-run user clicks 1 button, gets working DPI bypass in ≤ 100 s on typical Russian ISP.
- [ ] Returning user clicks 1 button, gets working bypass in ≤ 5 s (saved strategy).
- [ ] All 18 existing strategies still accessible via «Дополнительно».
- [ ] AV-block toast doesn't false-alarm during probing.

**Polish (~3-4 days)**

- Per-ISP profile catalog (D): `data/zapret-profiles.json` ships with 10-15 pre-curated entries (Beeline, MTS, Rostelecom, Megafon, Tele2, Yota, ER-Telecom, TTK, Net by Net, plus 5-6 regional ISPs). Loader at boot, auto-update from GitHub release.
- IPSet auto-update on first probe-success (caches list in `lists/ipset-all.txt`, max 24 h TTL).
- HEAD probe with multiple targets (youtube.com + discord.com + 4pda.to) — succeed only if 2/3 pass.
- Custom diagnostic panel in expander (real-time ImmediateExit count, last probe result).
- Per-step download progress with byte count + ETA.
- Visual baseline screenshot for `VisualDiffTests`.

**Stretch (~3-5 days, each decoupled)**

- Crowdsource (C): user-vote backend; opt-in toggle in Settings; daily sync.
- Smart retry: if PROBING fails 3 attempts, retry once after 5 min.
- Auto-rotate strategy on degradation: HealthMonitor-style watcher pings youtube.com every 5 min; if 2 consecutive fails → re-run PROBING loop.
- zapret2 migration: when #59 decision flips, replace ALT3/general/general (ALT) seed list with zapret2 Lua function names.
- Per-strategy success-rate telemetry (anonymous, opt-in).

---

## 8. Effort summary

| Phase | Hours | Files touched | New files | New tests |
|---|---|---|---|---|
| MVP | 16-24 | `DpiBypassPage.axaml`, `MainWindowViewModel.cs`, `Strings.cs` (Core), `ZapretManager.cs` (minor) | `ZapretAutoStrategy.cs` | `ZapretAutoStrategyTests.cs`, `ZapretOneTapVisualDiffTests.cs` |
| Polish | 24-32 | + `AppSettings.cs` (catalog persistence) | `data/zapret-profiles.json`, `ZapretProfileCatalog.cs` | `ZapretProfileCatalogTests.cs` |
| Stretch | 24-40 | + backend (out of scope) | per item | per item |

Total MVP + Polish: ~6-8 dev-days, aligning with TgProxyOneTap budget.

---

## 9. Open questions

1. **Default game filter = TCP+UDP** assumes most users don't care about the CPU bump. Confirm by spot-checking winws CPU under TCP+UDP vs Off.
2. **HEAD probe target = youtube.com**: should we let user pick? MVP defaults to YouTube.
3. **AppSettings persistence**: add new field `ZapretAutoStrategy` (probed) + keep `ZapretStrategy` as user override.
4. **Probe-time UAC**: Discord hosts requires admin. If app isn't elevated, magic button must request elevation BEFORE downloading.
5. **Catalog override**: catalog overrides only the *order* of probes, not the *set*. Seeds remain `{ALT3, general, general (ALT)}` for MVP.
6. **Behaviour while VPN is also running**: zapret + VPN combo is documented as supported. Verify HEAD probe routes via VPN; if so, probe tests «does zapret help on top of VPN» which may always pass (false positive). Mitigation: probe a site only DPI-blocks (YouTube via residential IP).

---

## 10. Critical files for implementation

The 5 files most central to the MVP magic-button flow:

- `C:/Project/VPNRouter/VPNRouter.App/Views/Pages/DpiBypassPage.axaml`
- `C:/Project/VPNRouter/VPNRouter.App/ViewModels/MainWindowViewModel.cs`
- `C:/Project/VPNRouter/VPNRouter.Core/Services/ZapretManager.cs`
- `C:/Project/VPNRouter/VPNRouter.Core/Services/ZapretUpdater.cs` (sort heuristic seeds probe order)
- `C:/Project/VPNRouter/VPNRouter.Core/Localization/Strings.cs`

Reference for hero pattern: `C:/Project/VPNRouter/VPNRouter.App/Views/Pages/TelegramPage.axaml:88-394` (TgProxyOneTap variant A).
