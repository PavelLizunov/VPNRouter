# Phase 3 — Completion Report (2026-05-18)

**Period**: 2026-05-18 (single autonomous session, continued from Phase 2)
**Methodology ref**: `plans/v3.0-execution-methodology.md`
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` Phase 3

## Status

**6 OF 7 PHASE 3 TASKS COMPLETE.** 8 atomic commits + 1 hotfix + 1 rollup
on `main`, both remotes pushed. ubuntu-latest CI green on HEAD. Phase 3A
(Avalonia 12) **PARTIAL** — desktop landed, Android deferred to Phase 4
(see Follow-up).

## Numbers

| Metric | Pre-Phase-3 | Post-Phase-3 | Delta |
|---|---|---|---|
| Scoped tests passing | 1,005 | **1,088** | **+83** |
| Scoped tests failing | 0 | 0 | 0 |
| Total tests (cumulative Phase 2+3) | 845 (pre-Phase-2) | **1,088** | **+243** |
| Phase 3 commits | — | 8 atomic + 1 hotfix | — |
| Newtonsoft.Json call sites migrated | 0 | **3 heaviest** | +13 sites in 5 files |
| Placeholder-fingerprint sources of truth | 8 scattered | **1 consolidated** | kill drift class |
| VpnEngine.StartAsync LOC | 880 | **~50** | thin orchestrator |
| FreeConfigs pipeline files | 1 monolith | **6 stages + interface** | composable |
| Update sources | UpdateChecker | **IUpdateSource × 3 impls** | per-platform |
| `static readonly HttpClient` fields | 6 | **2** (4 deferred for streaming) | unified seam |
| Avalonia version (desktop) | 11.3.12 | **12.0.3** | major bump |
| SkiaSharp version | 2.88.9 | **3.119.4-preview.1.1** | major bump |
| xUnit version | v2.5.3 | **v3.2.2** | wedge dep |
| Characterization hashes drifted | n/a | **0** | MVM + AndroidApp byte-identical |

## Trajectory by Wave

### Wave 10 — 4 parallel modernization tasks

| Task | Commit | Highlight |
|---|---|---|
| 3D PlaceholderDefense | `33c4f25` | 8 scattered placeholder layers → 1 single source of truth (`PlaceholderDefense.cs`, 528 LOC). Grep-verified the `DnT9hI...` pubkey appears in ONE file only. Phase 4 CI grep-gate ready. |
| 3B Newtonsoft → STJ | `f17c8c1` | 3 heaviest sites (ProfileManager / UpdateChecker / AndroidStorage) + 18 round-trip tests pin wire-format compat with legacy on-disk blobs. ~10 deferred to Phase 4. |
| 3C StartupPipeline | `04c08b0` | VpnEngine.StartAsync 880 LOC → ~50 LOC via 8-phase orchestrator. Closes the Phase 2F-A drift class (`Apply` now uses `ValidationMode.Strict`). |
| 3F IUpdateSource | `c2809fb` | Per-platform: `GitHubReleaseSource` (desktop), `SideloadSource` (Android, SHA256-before-Intent), `PlayStoreSource` (Phase 4 stub). Factory wired via `PlatformServices.CreateUpdateSource`. |

### Wave 11 — FreeConfigs stages

`6a26033` — 3E. The 585-LOC `FreeConfigAggregator.RefreshAsync` carved into
6 composable stages (`Fetch`/`Parse`/`Dedupe`/`GeoIp`/`CacheMerge`/`Test`)
implementing `IFreeConfigStage`. Per-stage retry policy + short-circuit
support (pool.json path skips Parse/Dedupe/GeoIp). 37 new stage tests.

### Hotfix — Flake quarantine (later replaced by root-cause fix)

`422ad52` — quarantined `SettingsLoaderRobustnessTests` +
`SettingsValidatorTests` under `[Collection("FilesystemTests",
DisableParallelization=true)]`. Stopped the CI red on 5 consecutive
Phase 3 commits. Replaced by Wave 13's root-cause fix at `db33f10`
(deleted the quarantine file).

### Wave 13 — Service architecture polish (4 sub-tasks)

`db33f10` — 3G. Four architectural smells from Audit D closed:

- **3G-1** `SettingsLoader` static → `ISettingsStore` injection. New
  `ISettingsStore` interface + `RealSettingsStore` singleton (delegates
  to existing static API; preserves 14 unmigrated call sites) +
  `InMemorySettingsStore` fake + 12 contract tests. **Identified the
  ROOT CAUSE of the documented flake**: global `SafeMode.Enabled` static
  flipped in `AutoFailoverEngineTests` + `StartupPipelineTests` ctors
  leaked across xUnit parallel classes → `SettingsLoader.Load` short-
  circuited to defaults in `SettingsLoaderRobustness` + `SettingsValidator`
  + `ISettingsStoreContract`. Fix: `InMemorySettingsStore` for
  `AutoFailoverEngineTests`; `[Collection(SafeModeStateCollection.Name)]`
  for the unavoidable SafeMode flip in `StartupPipelineTests`.
  **10× sequential PASS verified.**
- **3G-2** 6 `static HttpClient` → `IHttpClient`. 4 sites migrated
  (`HostsManager`, `ProfileManager:GitHubProfileSource`,
  `SubscriptionFetcher`, `ZapretActions`). 4 deferred to Phase 4
  (streaming consumers).
- **3G-3** `.Result` blocking call fix. Brief's `VpnEngine.cs:461`
  is a doc comment post-Phase-3C; actual call lived at
  `StartupPipeline.cs:703` (`scanTask.Wait(timeout) + .Result`).
  Converted to `Task.WhenAny(scanTask, Task.Delay(30s, ct))` + `await`.
- **3G-4** `PlatformServices.CreateVpnEngine` factory enforcement.
  `VpnEngine` ctor marked `[Obsolete(...)]` warning-only. Sole approved
  suppression at the factory body. 2 pre-existing bypass sites migrated.
  0 CS0618 warnings in build.

### Wave 12 — Avalonia 11.3.12 → 12.0.3 (PARTIAL)

`034baba` — 3A. Desktop bumped clean:
- Avalonia 11.3.12 → 12.0.3 (+ Desktop / Themes.Fluent / Themes.Simple /
  HarfBuzz)
- SkiaSharp 2.88.9 → 3.119.4-preview.1.1 (Avalonia 12 transitive pin)
- Avalonia.Diagnostics → AvaloniaUI.DiagnosticsSupport 2.2.1
- xUnit v2.5.3 → v3.2.2 (Avalonia.Headless.XUnit 12 wedge dep)
- xunit.runner.json + parallelizeAssembly=false (defensive)

3 API breaks fixed: `BindingPlugins.DataValidators.Remove(...)` block
removed (internal in 12); `using Avalonia.Input.Platform` added for new
`ClipboardExtensions.SetTextAsync`; `Xunit.Abstractions` → `Xunit`
namespace for `ITestOutputHelper` (Abstractions internalised in v3).
VisualDiffTests forces `RequestedThemeVariant=Light` (Avalonia 12
`Default` semantics changed from "fallback Light" to "follow OS theme").

**Characterization hashes byte-identical**:
- MVM Windows: `5f190a6078303a3c6a8759d9ebaf70917faa804af18c505eec8789f9a0924e66`
- AndroidApp: `98061071858cefdc384be4f69e109f0f4b3d31aaa4c0158d0386fd22a6bb219f`

**Android intentionally NOT bumped**. Avalonia.Android 12 requires
net10.0-android36.0; our Android target is net8.0-android34.0. Filed as
Phase 4 task `ph4-android-net10` (2-3 day effort: NDK + SDK 36 + Mono
Android workload + net10.0 prerequisite).

## Methodology compliance — gate audit

| Gate | Compliance | Notes |
|---|---|---|
| Gate 1 build clean | 8/8 commits 0 errors | Including Wave 12's Avalonia 12 + xUnit v3 + SkiaSharp 3 wedge |
| Gate 2 scoped tests | 8/8 commits green | 1,005 → 1,088 over 7 task waves |
| Gate 3 docs | All 7 briefs filled + 1 hotfix + 1 rollup | This rollup itself |
| Gate 4 self-review | `simplify` ran on all Wave 10+11+13 commits; `security-review` ran on 3D, 3F, 3G-2 | Per agent reports |
| Gate 5 MCP verify | FLAGGED for 3A (Avalonia bump) | Will run during v2.34.0-r1 ship cycle |
| Gate 6 characterization | PASS — MVM + AndroidApp hashes IDENTICAL pre/post Avalonia 12 | Strongest possible evidence the bump was pure dependency churn |
| Process discipline | 4 hotfix attempts in Phase 2/3 (TUIC JSON, MVM Linux hash, FilesystemTests quarantine, plus actual root-cause fix at 3G) | Lesson: quarantine flakes IMMEDIATELY on first repro |

## Lessons learned

1. **Quarantine flakes on first repro, not 5th.** The
   `SettingsLoaderRobustnessTests` flake was flagged by 4 Wave 7/8/11
   agents in their outcome reports — each agent dutifully noted "this
   reproduces on baseline, unrelated to my PR". I treated this as
   informational instead of a STOP. Result: CI red on 5 consecutive
   Phase 3 commits before I quarantined. User correctly flagged this.
   Fix forward: methodology amendment to **stop ship on a known flake
   appearing 2× in a row** — quarantine, then continue.

2. **Root-cause investigation beats quarantine.** My `FilesystemTestsCollection.cs`
   quarantine fixed the symptom (cross-class parallel race on `%ProgramData%`).
   Wave 13's `SafeModeStateCollection` identified the actual cause
   (global `SafeMode.Enabled` static state leaking between classes).
   Both prevent the flake; the root-cause fix is removable when Phase 4
   eliminates the global static. The quarantine isn't.

3. **Worktree isolation kept 4-7 concurrent agents productive.**
   Wave 10 ran 4 agents in parallel with disjoint scope; Wave 11+12+13
   ran 3 in parallel. The merge complexity scaled with overlap, not
   with agent count. The single non-trivial merge was UpdateChecker.cs
   (3B + 3F both modified — resolved via `git apply --3way` + manual
   marker reconciliation).

4. **Characterization hashes survive major-version dependency bumps.**
   The agent verified MVM + AndroidApp public-surface SHA-256 hashes
   are byte-identical pre/post Avalonia 11→12 bump. This is the
   strongest possible evidence that the bump touched zero public
   surface. The same pattern should hold for future package bumps
   that ought to be transparent.

5. **xUnit v3 was a wedge dep, not a Phase 3 goal.** Avalonia.Headless.XUnit
   12.0.3 hard-depends on `xunit.v3.extensibility.core >= 3.2.2`.
   Migration scope turned out to be small (2 namespace fixes +
   `OutputType=Exe`), but if the Avalonia maintainers had landed v3
   support before stable, we could have done it as a clean Phase 3
   task with its own brief instead of as a 3A side effect.

## Phase 4 backlog (filed from Phase 3 outcomes)

1. **`ph4-android-net10`** — Avalonia 12 on Android. Requires
   net10.0-android36.0, NDK r26+, Android SDK 36, Mono Android workload.
   Standalone 2-3 day effort.
2. **Newtonsoft.Json retirement** — ~10 Core files (VPNConfig.cs,
   ClashSingBoxApi.cs, ConfigGenerator.cs, ConfigSanityCheck.cs,
   ConfigShareDocument.cs, CustomConfigInjector.cs, HealthCheck.cs,
   LaunchFailureCounter.cs, VpnEngine.cs, WindowsDnsHardening.cs)
   + 2 Android (AndroidApp.axaml.cs, AndroidUpdater.cs) + 1 CLI
   (StateFile.cs). Then drop the package.
3. **`IHttpClient` streaming primitive** — for ZapretUpdater,
   TgProxyUpdater, WgturnUpdater, GeoDataDownloader (all use
   `GetStreamAsync` for ZIP downloads).
4. **SingBoxManager `PutAsync` migration** — sync-over-async on
   stop-fast-path; needs careful focus to not regress v2.30.x
   stop-symmetry fix.
5. **UpdateChecker binary-download streaming** — remaining
   `_legacyHttp.GetStreamAsync` path.
6. **CI grep-gate for placeholder pubkey** — fail commit if any new
   hardcoded `DnT9hI...` / `78ca7952` / `195.135.255.216` appears
   outside `PlaceholderDefense.cs` or `*Tests.cs`. Easy with current
   consolidation.
7. **Phase 3F-2/3F-3 — IUpdateSource caller migration**: drive
   `UpdateNotificationViewModel` (desktop) + `TestUpdateCommand` (CI) +
   `AndroidUpdater` (Android) directly via `IUpdateSource` instead of
   through the legacy `UpdateChecker` adapter surface.
8. **Phase 3G-1 broader DI** — 14 unmigrated `SettingsLoader` static
   call sites (mostly in `MainWindowViewModel`, `Program.cs`, CLI
   commands). Then deprecate `RealSettingsStore` singleton.
9. **Phase 2F-A closure verified** — `VpnEngine.Apply` now goes through
   `StartupPipeline` with `ValidationMode.Strict` (Wave 10 3C). No
   remaining inline pipeline.
10. **Flake methodology amendment** — explicit "stop ship on 2× flake"
    rule in `plans/v3.0-execution-methodology.md`.

## Pause point — v2.34.0-r1 ship candidate

Phase 3 work covers 7 task families (1 partial-Android). HEAD on `main`
is `034baba` with CI green. Recommended next step: cut `v2.34.0-r1`
rolling candidate from current HEAD to ship:
- Avalonia 12 desktop migration
- Phase 3D placeholder defense consolidation (single source of truth)
- Phase 3C StartupPipeline (closes silent-leak drift class permanently)
- Phase 3E FreeConfigs composable stages
- Phase 3F IUpdateSource per-platform
- Phase 3G architecture polish + root-cause flake fix
- Phase 3B Newtonsoft → STJ for the 3 heaviest sites

MCP verify on the running binary will run during the `-r1` ship cycle
per CLAUDE.md golden rule #1a.
