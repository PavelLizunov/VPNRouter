# Dead Code + Duplication Audit (v2.32.3-r1 baseline)

Generated 2026-05-17. Read-only audit. No source changes.

## Summary

- **Total LOC** (`.cs`, excluding bin/obj/publish): ~93,700 across 242 files.
  - VPNRouter.Core 32,376
  - VPNRouter.Android 21,431
  - VPNRouter.Tests 19,422
  - VPNRouter.App 16,976
  - VPNRouter.CLI 1,402
  - VPNRouter.Service 938
  - tools/VpnRouterTestMcp 985
  - VPNRouter.Tools (PoolAggregator) 172
- **Solution coverage gap**: `VPNRouter.Android` and `tools/VpnRouterTestMcp` projects are NOT in `VPNRouter.sln`. They build only out-of-band (Android with `/p:EnableAndroidTarget=true`, MCP server stand-alone). Worth noting for any solution-wide refactor — `dotnet build VPNRouter.sln` will not touch them.
- **Top 5 refactor priorities** (by impact):
  1. **Delete the dead VPNRouter.UI source-link in `VPNRouter.Android.csproj`** — points to non-existent path (build-time bug waiting to surface). 10 minutes, 0 risk.
  2. **Strip 270 unused `L_X` wrapper getters** in `VPNRouter.App/ViewModels/MainWindowViewModel.Localization.cs` (51% of file, ~270 LOC).
  3. **Collapse duplicate `Strings.cs` keys** — 547 of 589 App keys are identical to Core. Make App.Strings a thin Core re-export (matches Android.Localization pattern) → ~1,400 LOC removable, single source of truth.
  4. **Split `UnitTest1.cs` (6,169 LOC, 42 classes)** into per-feature test files. Already 25+ standalone test files exist; the 42 in UnitTest1 are inertia from the original baseline.
  5. **Split `AndroidApp.axaml.cs` (7,177 LOC)** — Settings (32 handlers), Apps picker (14 handlers), Theme/Profiles overlay are natural extractions; mirrors the existing AndroidApp.* partial pattern.

---

## §1 Orphaned files

### Build/source-link to non-existent paths

| File | Problem |
|---|---|
| `VPNRouter.Android/VPNRouter.Android.csproj` lines 91-95 | `<Compile Include="..\VPNRouter.UI\**\*.cs">` + `<AvaloniaResource Include="..\VPNRouter.UI\Controls\**\*.axaml">` glob into `..\VPNRouter.UI\` which **does not exist** on disk. The Android-Controls comment in `VPNRouter.Android/Controls/StatusCard.cs` explicitly says the shared project was reverted on 2026-05-09 ("The shared VPNRouter.UI project was removed because the user explicitly said «we should not have touched desktop at all»"). The csproj include was never cleaned up. Glob silently matches nothing on disk, so build succeeds. Should be removed (or, if a UI lib is wanted again, the dir restored). |
| `VPNRouter.Android/Controls/StatusCard.cs` | Defines `namespace VPNRouter.UI.Controls` (preserving the original namespace surface despite the directory move). Cosmetic-only — namespace name lying about the project layout. Consider renaming to `namespace VPNRouter.Android.Controls` and updating call sites. |

### Top-level orphans (untracked / build artifacts in repo root)

| Path | Status | Recommendation |
|---|---|---|
| `VPNRouter-update-v2.32.2-win.zip` (+ .sha256) | gitignored release artifact | Move to a `dist/` or out-of-repo location |
| `VPNRouter-update-v2.32.3-r1-win.zip` (+ .sha256) | gitignored release artifact | Same |
| `VPNRouter-v2.32.2-win.zip` (+ .sha256) | gitignored release artifact | Same |
| `VPNRouter-v2.32.3-r1-win.zip` (+ .sha256) | gitignored release artifact | Same |
| `build-stable.log`, `build-v2.32.log` | gitignored (matches `*.log`) | One-off build session logs; delete or move to plans/ |
| `test_parser.csx` (2.57 KB) | one-off C# script ("Quick test: simulate what ZapretUpdater.ParseStrategies does for ALT3") | Untracked debugging scratch. Either delete or move to `tools/scratch/`. |
| `LOCALIZATION_PLAN.md` (3.75 KB, dated Apr 22) | NOT gitignored — committed | Stale planning doc — code was completed long ago. Move to `plans/archive/` or delete. |
| `update-helper.cmd` (Apr 22) | NOT gitignored — committed | Auto-update fallback script. Still referenced by old VPNRouter.GUI.exe Go stub. Keep but flag. |
| `b_icon.png`, `w_icon.png` (1+ MB combined) | committed at root | Look like the original mascot artwork pre-Avalonia. App now uses `VPNRouter.App/Assets/penguin_mascot.png`. Verify ownership and either move to `design/assets/` or delete. |

### Stale directories

| Dir | Status |
|---|---|
| `VPNRouter.GUI/` | Contains a **Go program** (`main.go`, `integrity.go`, `marker.go`, `repair.go`), NOT C#. It's a backwards-compat launcher stub for old shortcuts/auto-updater that still expects `VPNRouter.GUI.exe`. Build product (`VPNRouter.GUI.exe`) sits next to source. Keep — but no overlap with .NET project family. Worth a `README.md` explaining the Go presence. |
| `test-screenshots/` (24 PNGs, Apr 22) | Manual screenshots from a single early debug session ("s17-kebab-crash.png", "s21-crashlog-final.png"). Stale. Move to `plans/archive/` or delete. |
| `docs/` | Single file: `SSH_FALLBACK_PLAN.md`. Misnomer — was supposed to be public-facing docs. Either populate or fold into `plans/`. |
| `samples/rules/` (8 files) | Example CSV/JSON rule sets. Likely referenced by docs/README for user education. Keep. Verify referenced by README. |
| `parity-audit/` | git status shows `parity-audit/android/` as untracked but the directory is **empty on disk** at audit time. Verify and clean up. |

---

## §2 Obsolete markers

Severity legend: HIGH = code likely removable, MED = backwards-compat helper that may still be hit, LOW = doc-only.

### LOW (legacy commentary in active code, used)
- `VPNRouter.Core/Services/SettingsLoader.cs:312` — "legacy root Vless.Server / Uuid scalars" (still parsed for migration).
- `VPNRouter.Core/Services/SettingsMigrator.cs:375` — "legacy CustomDirectRule was direct-only".
- `VPNRouter.Core/Services/SubscriptionResolver.cs:46` — "Legacy migration: if only old SubscriptionUrl is set".
- `VPNRouter.Core/Services/CustomConfigInjector.cs:352, 492` — "legacy format", "Legacy: dns-out rule" (1.11 → 1.13 migration logic, still needed).
- `VPNRouter.Core/Services/VpnEngine.cs:555-557` — "legacy direct entry that was never refreshed" (sanity narrative).
- `VPNRouter.Core/Services/AutoFailoverEngine.cs:100` — "Legacy direct-VLESS mode (no subscription) keeps auto-switch".
- `VPNRouter.Core/Services/ConfigGenerator.cs:118` — "kept for back-compat (SettingsMigrator empties it on v1->v2".
- `VPNRouter.Core/Services/TgProxyManager.cs:294` — "Legacy path: still sweep the old-style names".
- `VPNRouter.Core/Services/UpdateChecker.cs:26, 794, 1178, 1198, 1215` — "VPNRouter-install-v1.24.6.zip (old Windows naming, still supported)" (4 sites). Kept for users on pre-v1.24 still upgrading.
- `VPNRouter.Android/AndroidStorage.cs:110, 147, 608, 908, 925` — multiple legacy preference-key migrations from Phase 1.H onwards (active code, in use).

### MED (potentially removable post v3.0 LTS migration)
- `VPNRouter.App/Localization/Strings.cs:1138` + `VPNRouter.Core/Localization/Strings.cs:1148` — "Legacy single-string accessor (kept for any cached XAML still binding". Could be retired once all XAML is touched.
- `VPNRouter.App/ViewModels/MainWindowViewModel.Localization.cs:141` — "Legacy v2.29.0 aliases (kept for cached XAML)". Same.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:4125-4127` — "legacy field stable in exclude mode (leave existing legacy data don't surprise the user)".

### HIGH (active TODOs that should be filed or fixed)
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs:17` — `/// TODO: implement MacFirewallManager with pfctl anchor rules.` Tracks an unimplemented feature; either ticket it in plans/ or remove the file if Mac firewall is not on the roadmap.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5306` — `// TODO(DBG-2 sister): once VPNRouter.App has its own ...` (incomplete dbg-2 sibling); resolve or ticket.
- `VPNRouter.Tests/UnitTest1.cs:246` — `// TODO(post-subscription-refactor): these two tests assume the pre-subscription`. Tests still pass per current run, but the refactor was done — re-verify they exercise the new path.

### `#if false` / `#if DEBUG` audit
- No `#if false` found anywhere.
- No `#if DEBUG` in production code (test-helper-only references).
- `#if PLATFORM_WINDOWS` blocks are all live and necessary (37+ hits, deliberate gating for ETW/WMI/registry/firewall).

### `[Obsolete]` attribute
- Zero `[Obsolete]` attributes anywhere. The codebase prefers comment-based legacy flagging.

---

## §3 Duplicate code

### A. `Strings.cs` Core ⇄ App key overlap (HIGHEST IMPACT)

| Metric | Value |
|---|---|
| Core unique keys (`public static string X`) | 894 |
| App unique keys | 589 |
| Keys in both | **547** |
| Sampled-identical values among shared keys (50 sample) | **50/50 identical** |
| App-only keys | 42 |
| Core-only keys | 347 |

`VPNRouter.App/Localization/Strings.cs` (1,631 LOC) is a 93%-overlap duplicate of `VPNRouter.Core/Localization/Strings.cs` (2,734 LOC). The Android port already proves the cleaner pattern: `VPNRouter.Android/Localization.cs` is a pass-through wrapper around Core (100% of its 884 keys delegate to Core). The desktop App should follow the same pattern.

**Dedup target**: ~1,400 LOC removable from App.Strings.cs by:
1. Moving the 42 App-only keys to Core (they're not particularly UI-specific, mostly tab/page labels — already 547 of their siblings live in Core).
2. Replacing `VPNRouter.App.Localization.Strings` with `using static VPNRouter.Core.Localization.Strings;` re-exports.

### B. `MainWindowViewModel.Localization.cs` L_ wrappers (HIGH IMPACT)

The 531 `public string L_X => Strings.X;` properties exist to make Core strings observable via XAML `{Binding L_X}`. **270 of 531 (51%) are not referenced anywhere outside the file itself**, indicating dead wrappers left behind by removed XAML.

Examples (sample):
- `L_AddConfig`, `L_AddCustomAppHint`, `L_AddServerFirst`, `L_AddServers`, `L_Apply` (only `L_ApplyChanges` / `L_ApplyNowReloadVpn` are referenced), `L_ApplyFailed`, `L_AppsHint`, `L_AppsModeExcludeHint`, `L_AppsModeIncludeHint`, `L_AutoFailoverCustomMode`, `L_AutoFailoverExhausted`, `L_AutoFailoverProbing`, `L_AutostartPlatformNotice`, `L_AutostartSection`, `L_AutostartTgProxy`, `L_AutostartUi`, `L_AutostartUiSessionHeader`, `L_AutostartVpn`, `L_AutostartWithWindows`, `L_AutostartZapret`, ... (full list available via `comm -23 /tmp/l_wrappers.txt /tmp/used_L_wrappers.txt`).

**Dedup target**: ~270 LOC removable (line per wrapper). Safe action: delete each unused `L_X` getter.

### C. Utility helpers — no significant duplicates found

Searched for SHA256 hashing, hex string conversion, JSON serialization helpers. All hash uses go through `System.Security.Cryptography.SHA256.Create().ComputeHash(...)` inline in 4 sites (`FreeConfigAggregator`, `UpdateChecker`, `WgturnUpdater`, `PoolAggregator/Program.cs`). 5–10 line inline blocks; not worth extracting unless a HashHelper.cs is added during a broader refactor.

Helper classes (`AdminHelper`, `AutostartHelper`, `ScreenshotHelper`, `VisualDiffHelper`, `WindowForegroundHelper`, `WindowsServiceHelper`) are all single-purpose and not cross-duplicated.

### D. Android ⇄ Core/App overlap

The Android port already source-links VPNRouter.Core (via `<Compile Include="..\VPNRouter.Core\**\*.cs">`), so Core classes are physically compiled twice (once into Core.dll for desktop, once into Android.dll for Android). This is by design (see Android.csproj comment) and not a duplication target. Same applies to `VPNRouter.Android/AndroidConfigBuilder.cs` (373 LOC) — it's a thin shim, not a copy of ConfigGenerator.

---

## §4 God-file split recommendations

### `VPNRouter.Android/AndroidApp.axaml.cs` — 7,177 LOC

Current partials already extract:
- `AndroidApp.AdvancedShell.cs` (825 LOC)
- `AndroidApp.AutoUpdate.cs` (350)
- `AndroidApp.ConfigShare.cs` (707)
- `AndroidApp.DpiBypass.cs` (605)
- `AndroidApp.FreeConfigs.cs` (1,330)
- `AndroidApp.QrScanApply.cs` (301)
- `AndroidApp.ServerList.cs` (1,499)
- `AndroidApp.SubscribePage.cs` (1,320)
- `AndroidApp.Tools.cs` (731)

The core `AndroidApp.axaml.cs` still holds:
- **Settings page handlers**: 32 `OnSettings*Changed` handlers (lines 3128-3370). → Extract `AndroidApp.SettingsPage.cs` (~900 LOC).
- **Apps picker**: 14 handlers around `_appPickerCache`, `_appPickerMode`. → Extract `AndroidApp.AppsPicker.cs` (~700 LOC).
- **Theme + Profiles overlay**: 7 theme handlers, 9 profile handlers (lines 603-2034 + 3670-3865). → Extract `AndroidApp.ThemeAndProfiles.cs` (~600 LOC).
- **Reliability subpage**: `OnReliability*` handlers (lines 3244-3329). → Could merge into Settings partial.

**Expected post-split**: core file ~3,500-4,000 LOC, 4 new partials at 600-900 LOC each.

### `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — 6,753 LOC

Existing partials handle: AutostartBootstrap (301), Localization (607), RuntimeStatus (358), ServerTesting (428), SimpleMode (599), Wgturn (561) — total 9,607 across partials.

Headline file holds 193 `[ObservableProperty]` and 139 method declarations. Natural extraction targets:

- **Custom Rules editor** (lines ~798-1900): 30+ methods around `_customRules`, `RebuildCustomRulesList`, `ApplyEditedRules`, `RebuildReadModeGroups`, conflict UI. → `MainWindowViewModel.CustomRules.cs` (~1,200 LOC).
- **Zapret + Tg Proxy state** (lines 1101-2370): Zapret state, immediate exit, av-block toast, tg proxy labels. → `MainWindowViewModel.ZapretAndTgProxy.cs` (~700 LOC).
- **Settings persistence pipeline** (lines 2920-4216): `LoadSettingsIntoUI`, `SaveSettings`, `ApplyPendingChangesAsync`, custom configs, `RestoreConnectedStatus`. → `MainWindowViewModel.SettingsPipeline.cs` (~1,300 LOC).
- **Connection + subscription refresh** (lines 4216-4680+): `ToggleConnectionAsync`, `RefreshSubscriptionAsync`, sub refresh timer. → `MainWindowViewModel.Connection.cs` (~700 LOC).

**Expected post-split**: headline file ~2,800 LOC, 4 new partials at 700-1,300 LOC.

### `VPNRouter.Tests/UnitTest1.cs` — 6,169 LOC

Contains **42 distinct test classes** (one per feature area). They've been growing as a single file since 2024. Standalone test files already exist (49 .cs files in VPNRouter.Tests), so the precedent is set. Extract each class into its own file:

Largest classes inside UnitTest1.cs (LOC = next-class-start − current-class-start):
- `CustomConfigInjectorTests` — 588 LOC
- `VlessUriParserTests` — 498 LOC
- `LeakProtectionTests` — 469 LOC
- `LeakProtectionAppSettingsTests` — 201 LOC
- `ConfigGeneratorTests` — 372 LOC
- `VlessServersResolverTests` — 222 LOC
- `FreeConfigAggregatorPreserveTests` — 216 LOC
- `CustomDirectRulesParserTests` — 139 LOC
- `CustomDirectRulesGeneratorTests` — 230 LOC
- `CustomRulesV2_30_GeneratorTests` — 193 LOC
- `CustomRulesV2_30_ParserTests` — 171 LOC
- `FreeConfigKeepPolicyTests` — 89 LOC
- (… 30 more, mostly 50-200 LOC each)

**Expected post-split**: 42 new files, average ~150 LOC each. Headline `UnitTest1.cs` becomes empty/deletable (or holds only the assembly-level test setup).

### `VPNRouter.Core/Localization/Strings.cs` — 2,734 LOC

Single large file holding 894 getters. Could be partitioned by namespace prefix:
- `Strings.Tabs.cs` (TabXxx, ~10 getters)
- `Strings.Servers.cs` (~80)
- `Strings.FreeConfigs.cs` (Fc*, ~150)
- `Strings.Smp.cs` (Smp*, ~120 — Simple-mode)
- `Strings.Rules.cs` (Rules*, ~100)
- `Strings.Network.cs` (Network/Autostart/Update*, ~120)
- `Strings.Settings.cs` (~80)
- `Strings.Zapret.cs` (Wm*, Zapret*, Dpi*, ~80)
- `Strings.Tg.cs` (Tg*, ~30)
- `Strings.Misc.cs` (rest, ~120)

Mechanical split via `partial class Strings`. Expected per-file: 80-200 LOC.

### `VPNRouter.Core/Services/VpnEngine.cs` — 1,658 LOC

Already focused, but split candidates:
- DNS-flush and Windows-DNS-hardening pre/post hooks (separate concerns, ~200 LOC).
- Server-resolution and effective-server logic (~300 LOC).
- Lifecycle (`StartAsync`/`Stop`/`Apply`) — ~600 LOC core.
- TUN fingerprint comparison + reload decision (~200 LOC).

Each is a single cohesive concern; partial-class split into 3-4 files reduces cognitive load without changing behavior.

### `VPNRouter.Core/Services/UpdateChecker.cs` — 1,387 LOC

Holds: API check (`GetLatestRelease`), asset parsing for 4 platforms (Win/Win-update/Mac/Linux), download + verify, extract + apply, post-install bootstrap. Natural split:
- `UpdateChecker.AssetParsing.cs` (~300 LOC).
- `UpdateChecker.DownloadVerify.cs` (~300).
- `UpdateChecker.Apply.cs` (~400).
- Headline file with public API surface (~300).

---

## §5 Unused localization strings

**Core `Strings.cs`** (894 keys, 2,734 LOC): only **4 keys** with **0 references** across the entire codebase:
- `SmpRefreshButton`
- `SmpSaveButton`
- `SmpSaveFirstServer`
- `SmpSaveFirstSubscription`

Saving these: ~4 LOC. The Core strings are extremely well-utilized.

**App `Strings.cs`** (589 keys): no untracked unused strings beyond duplicates (see §3). All keys flow through MainWindowViewModel.Localization or XAML.

**App `MainWindowViewModel.Localization.cs`** (531 `L_X` wrappers, 607 LOC): **270 wrappers unused** (51%). The string they wrap still exists in Core, so removing the wrapper does not lose data. Sample of unused (full delta computable via `comm -23 l_wrappers.txt used_L_wrappers.txt`).

**Total removable LOC from localization layer**: 4 (Core) + 270 (App L_) = **274 LOC**, plus the ~1,400 LOC achievable by collapsing App.Strings → Core.Strings (§3.A).

---

## §6 Unreferenced services

Audit of 60 classes in `VPNRouter.Core/Services/`. None are zero-reference. Lowest-referenced (single external call site):

| Class | Ref count | Confidence | Notes |
|---|---|---|---|
| `DnsFlusher` | 1 ext (VpnEngine.cs) | HIGH (used) | Single call site is fine — keep. |
| `NetworkInterfaceDetector` | 1 ext (VpnEngine.cs) | HIGH (used) | Same. |
| `VlessDeepVerifier` | 1 ext (MainWindowViewModel.ServerTesting.cs) | HIGH (used) | Constructed once, fine. |
| `WindowsDnsHardening` | 2 ext (VpnEngine.cs Apply/Restore) | HIGH (used) | Single concern, single user. |
| `SubscriptionResolver` | 3 ext (CLI, Service, Tests) | HIGH (used) | Used by both daemon entry points. |
| `EtwProcessMonitor` | 4 ext (CLI, PlatformServices, HealthMonitor, Service) | HIGH (used) | Active. |
| `LockFile` | 4 ext (App, MWVM) | HIGH (used) | Lockfile lifecycle. |
| `BuiltInAndroidProfiles` | 5 ext (Android, Tests) | HIGH (used) | Android-only catalog. |
| `FirewallManager` | 5 ext (VpnEngine, Tests) | HIGH (used) | Active firewall surface. |

No genuinely-unused services found. Every Core service is reachable from at least one non-test call site.

**App services** (6 classes in `VPNRouter.App/Services/`):
- `ShortcutSelfHeal` — only 1 reference (`VPNRouter.App/Program.cs:EnsureTrampolineTarget()`). Single, narrow usage but legitimate.
- `WindowForegroundHelper` — 2 references. Fine.
- All others (`InstallHealthCheck`, `SelfRepair`, `SingleInstance`, `WindowsServiceHelper`) — 5+ references each.

**Models** (`VPNRouter.Core/Models/`):
- `VPNConfig.cs` is a file holding 17 nested classes (SingBoxConfig, DnsServer, etc.) — class name "VPNConfig" itself is just file naming. The contained classes are all heavily referenced. Not a dead-code issue.
- `AppSettingsSane`, `UpdateInfo`, `WgturnEntry` — 7 refs each; all live.

---

## §7 Comment ratio per project

| Project | LOC | Comment lines | Comment % |
|---|---|---|---|
| VPNRouter.Core | 32,376 | 8,555 | **26.4%** |
| VPNRouter.App | 16,976 | 4,463 | **26.3%** |
| VPNRouter.Android | 21,431 | 5,451 | **25.4%** |
| VPNRouter.Tests | 19,422 | 4,079 | 21.0% |
| VPNRouter.Service | 938 | 175 | 18.7% |
| VPNRouter.CLI | 1,402 | 124 | **8.8%** |

Observations:
- **Core/App/Android are extremely commented** (~26%). Much of that is rationale-comments capturing release-by-release lessons (e.g. `// v2.31.7 — CMD parser bug → ...`, `// v2.28.2 — silent leak fix ...`). This is genuinely useful to keep — it's the project's working memory.
- **CLI is under-documented at 8.8%**. CLI is a thin Spectre.Console wrapper, mostly straightforward command-binding. Probably fine as-is, but adding XML docs on `[Command]` classes would help `dotnet run -- --help` output.
- **No project crosses 30%**, so no clear "over-documented" candidates.

---

## Removable scope estimate

| Category | Files | Removable LOC | Risk |
|---|---|---|---|
| Dead source-link in Android.csproj (VPNRouter.UI glob) | 1 (csproj) | 4 (XML lines) | **None** — glob matches nothing |
| Unused `L_X` wrappers in MWVM.Localization | 1 | ~270 | **Low** — strict unused via codebase grep |
| Strings.cs Core⇄App dedup (App→Core re-export) | 1 | ~1,400 | **Med** — touches all XAML binding paths; needs XAML-pin smoke test |
| Unused Core localization strings | 1 | 4 | None |
| `Worker.cs` (already absent — README mention obsolete) | — | 0 | None |
| `test_parser.csx`, `test-screenshots/`, `LOCALIZATION_PLAN.md`, root build logs | misc | scripts/PNGs, not .cs | None |
| Split UnitTest1.cs (relocate, no delete) | 1→42 | 0 net | None — pure relocation |
| Split AndroidApp.axaml.cs (relocate) | 1→4 | 0 net | None — pure partial extraction |
| Split MainWindowViewModel.cs (relocate) | 1→4 | 0 net | None — pure partial extraction |
| Split Strings.cs by domain (relocate) | 1→10 | 0 net | None |
| **Total deletable .cs LOC** | | **~1,678** | Low-to-med |
| **Total restructuring (relocate-only)** | | **~17,000 LOC moved** | None |

### Recommended ordering

1. **Trivial cleanup** (10 min): delete stale VPNRouter.UI include in Android.csproj; rename `VPNRouter.Android/Controls/StatusCard.cs` namespace; sweep root-level orphans (test_parser.csx, LOCALIZATION_PLAN.md, test-screenshots, build-*.log).
2. **Dead L_X sweep** (30 min): generate the precise list (`comm -23 l_wrappers.txt used_L_wrappers.txt`) and delete the 270 unused wrappers; rebuild + run tests.
3. **Strings.cs dedup** (2-3 hours): turn `VPNRouter.App.Localization.Strings` into pure re-export of `VPNRouter.Core.Localization.Strings`, move the 42 App-only keys to Core, run full Avalonia headless test suite + visual diff baseline.
4. **God-file splits** (one PR each, low-risk):
   - UnitTest1.cs → 42 per-class files (mechanical).
   - MainWindowViewModel.cs → +4 partials.
   - AndroidApp.axaml.cs → +4 partials.
   - Core.Strings.cs → 10 partials.

### Out-of-audit notes

- **VPNRouter.Android and tools/VpnRouterTestMcp are not in VPNRouter.sln.** Anyone running `dotnet build VPNRouter.sln` will not compile them. Worth adding (or removing them from the working set deliberately).
- **`VPNRouter.GUI/` is a Go subproject**, not C#. Its `main.go` is the legacy launcher stub for old shortcuts. Confirm whether v3.0 still depends on this trampoline before retiring.
- The `update-helper.cmd` at repo root and the Go stub work together for users on pre-v2.29 auto-update path. Both should be evaluated together for v3.0 removal candidacy.
