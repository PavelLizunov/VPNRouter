# Phase 3G-4 — VpnEngine factory enforcement (verification pass)

## Триггер

Phase 3G "last item" — task scoped to enforce
`PlatformServices.CreateVpnEngine` factory at all production callers,
per `plans/phase3-3G-service-polish-2026-05-18.md` §3G-4 ("PlatformServices.CreateVpnEngine factory enforcement (some sites bypass it)").

Task brief assumed `VPNRouter.App/ViewModels/MainWindowViewModel.cs`
still constructed `VpnEngine` directly. Verification pass confirms
that assumption is stale — the migration was completed atomically in
commit `db33f10` ("refactor: 3G — service architecture polish") on
2026-05-18 alongside 3G-1 (`ISettingsStore`), 3G-2 (`IHttpClient`
consolidation) and 3G-3 (`.Result` blocking fix).

This brief documents the verification pass and the post-`db33f10`
state of the surface.

## Why

The single-platform Windows codebase historically hand-wired
`new VpnEngine(scanner, firewallFactory, monitorFactory, logger)`
at 3 sites (CLI, Service, App). With macOS + Linux platforms shipped
(Phase 2 → 3F) and Android in port (Phase 5), each direct ctor call
risks drifting between platform-specific wiring choices (e.g. one
caller forgets to swap `ProcessScanner` for `MacProcessScanner` on
non-Windows). Factory centralizes the `#if PLATFORM_WINDOWS` branch
in `VPNRouter.Core/Platform/PlatformServices.cs:22-47` so callers
just say `CreateVpnEngine(logger)`.

The Phase 3G-4 plan added an `[Obsolete(error: false)]` on the
public ctor (warning-only) so existing call sites would surface in
a build pass before being migrated, and the factory itself is the
single approved `#pragma warning disable CS0618` site.

## What — state on 2026-05-21

Grep `new VpnEngine(` across the solution (excluding worktrees
under `.claude/worktrees/*`, which are stale agent snapshots):

| Site | Status |
|---|---|
| `VPNRouter.Core/Services/VpnEngine.cs:123` | the ctor declaration itself — necessarily there, marked `[Obsolete]` |
| `VPNRouter.Core/Platform/PlatformServices.cs:64` | the sole approved suppression site (`#pragma warning disable CS0618`) |
| `VPNRouter.Tests/VpnEngineOrchestratorTests.cs:84` | test seam, also under `#pragma warning disable CS0618` (intentional — tests inject fake `IProcessScanner` / `IFirewallManager` / `IProcessMonitor`) |

Production callers all route through `PlatformServices.CreateVpnEngine`:

| Site | Line |
|---|---|
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` | `2469` |
| `VPNRouter.CLI/Commands/StartCommand.cs` | `121-122` |
| `VPNRouter.Service/VPNRouterService.cs` | `200-201` |

Android (`VPNRouter.Android/`) has zero `VpnEngine` references — that
project uses its own VPN service lifecycle (Android `VpnService`
foundation) and does not load `Core/Services/VpnEngine.cs`.

## How — work performed in this session

1. Grep'ed `new VpnEngine(` across:
   - `VPNRouter.App/` → 0 violators (only `PlatformServices.CreateVpnEngine` at line 2469).
   - `VPNRouter.CLI/` → 0 violators.
   - `VPNRouter.Service/` → 0 violators.
   - `VPNRouter.Android/` → 0 references.
2. Cross-checked test seam at `VPNRouter.Tests/VpnEngineOrchestratorTests.cs:75-89`
   confirms `#pragma warning disable CS0618` wraps the test factory
   helper `BuildIdleEngine` (with stub scanner / firewall / monitor).
   Legitimate per `error: false` attribute setting.
3. Read git log `git log --oneline -S "PlatformServices.CreateVpnEngine"`
   confirms commit `db33f10` is where all 3 production call sites flipped.
4. Read `plans/phase3-3G-service-polish-2026-05-18.md:172-184` confirms
   the Phase 3G-4 outcome was "PASS" at original close.
5. Ran build + regression suite (Verification section below).

## Verification

| Gate | Result |
|---|---|
| `dotnet build VPNRouter.sln -c Release` | **0 errors** (219 warnings, all unrelated to `VpnEngine`: `xUnit1051` + `CA1416` on `TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent` from hotfix-tun-adapter-orphan-pre-enable-2026-05-19) |
| `Select-String CS0618.*VpnEngine` over build log | **0 hits** — factory enforcement clean |
| `Select-String CS0618` over build log (all subjects) | only `TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent` — pre-existing v2.35.0 hotfix deprecation, unrelated |
| `dotnet test VPNRouter.Tests --no-build --filter "FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"` | **1248 passed, 0 failed, 4 skipped** — matches `286b8a5` baseline exactly |

No new tests added — surface is already pinned by
`VpnEngineOrchestratorTests` (74 tests) which exercises the
constructor path through the factory-style stubs.

## Risk

None. No code changes in this verification pass. Brief documents
the post-`db33f10` steady state and pre-empts future audits from
re-triaging the closed work.

## Outcome

- **Production sites migrated**: 3/3 (App, CLI, Service) — completed
  in `db33f10` on 2026-05-18.
- **Android**: not applicable (project doesn't reference `VpnEngine`).
- **CS0618 warning emission**: 0 on `VpnEngine` ctor.
- **Test suite**: 1248 pass (vs baseline 1248), 0 regressions.
- **Brief author note**: this brief is a verification post-mortem,
  not a code-change brief. The task title "enforce factory at all
  callers" was already a no-op when verified. Keeping this file for
  trace continuity per `plans/CLAUDE.md` convention.

## Связь с другими планами

- `plans/phase3-3G-service-polish-2026-05-18.md` — primary Phase 3G
  plan (3G-1 through 3G-4); §3G-4 "Files staged" lists the original
  4 edited files for this sub-task.
- `plans/v3.0-architecture-roadmap.md:22` — original "DI: ad-hoc"
  audit finding that motivated factory introduction.
- `plans/phase3-completion-2026-05-18.md` — Phase 3 completion report.
