# Phase 1 — DR-03 Serilog sink ownership

**Owner**: Codex

**Branch**: `codex/dr-03-serilog-sink-ownership`

**Audit ref**: dependency replacement task list DR-03, draft PR #99

**Effort**: 1–2 hours

**Risk**: MEDIUM — manifest-only change, but it affects dependency resolution for five executables/projects

**Blast radius**: 5 project files · about 8 package-reference lines · no intended logging behavior change

**Rollback**: revert the implementation commit or close the branch

## Why

`VPNRouter.Core` currently owns Console and File sinks even though a class library does not configure either sink. Executables receive those packages transitively, while Android declares both without configuring them. Moving each sink reference to the executable that actually calls `WriteTo.Console()` or `WriteTo.File()` makes the dependency graph deterministic and avoids shipping unused sink assemblies.

## What

- Remove Console and File sinks from `VPNRouter.Core/VPNRouter.Core.csproj`.
- Remove Console and File sinks from `VPNRouter.Android/VPNRouter.Android.csproj`.
- Add direct Console and File sink references to `VPNRouter.App/VPNRouter.App.csproj`.
- Add a direct Console sink reference to `VPNRouter.Tools/PoolAggregator/PoolAggregator.csproj`.
- Remove the redundant direct `Serilog.Extensions.Logging` reference from `VPNRouter.CLI/VPNRouter.CLI.csproj` only if dependency and build checks confirm it is unnecessary.
- Leave Service with File only and CLI with Console plus File.

```diff
- Core: Console, File
- Android: Console, File
+ App: Console, File
+ PoolAggregator: Console
- CLI: direct Serilog.Extensions.Logging
```

## How

1. Have Qwen 3.8 independently map all `WriteTo.Console/File` calls and direct/transitive Serilog package references in read-only mode.
2. Verify Qwen's map against every executable project and its composition root.
3. Apply only the package-reference moves listed above; do not change logging code or package versions.
4. Parse all changed project files and inspect the resolved dependency graph.
5. Run Release build/test gates, including Android when the required SDK is available; otherwise require the repository's .NET 10 CI and record the missing local toolchain.
6. Fill the Outcome section, commit, push to `origin`, and open a draft PR.

### Tests written

- None planned: the change is manifest-only and preserves existing logging calls.

### Verification approach

- Full repository search for sink configuration calls.
- XML validation for every changed project file.
- Release solution build and full test suite through the available .NET 10 environment.
- Android Release build when the configured Android toolchain is available.
- Resolved dependency/output comparison for App, CLI, Service, PoolAggregator, Core, and Android.

## Verification gate

- [x] **Gate 1 — Build clean**: solution Release build has 0 errors; Android build included when toolchain is available.
- [ ] **Gate 2 — Tests green**: full repository test suite passes.
- [x] **Gate 3 — Docs**: this brief's Outcome is filled; no README change is expected.
- [x] **Gate 4 — Self-review**: N/A; the implementation is a 5-project manifest/docs diff under 100 LOC and does not touch a security surface.
- [x] **Gate 5 — MCP verify**: N/A — no UI behavior changes.
- [x] **Gate 6 — Characterization diff**: N/A — not a god-file split.

## Outcome

- Qwen 3.8 max-preview independently approved the ownership map before the
  edit and approved the final diff with no blocking findings or new backlog.
- Core and Android no longer own Console/File sinks. App directly owns both;
  PoolAggregator directly owns Console. CLI's direct
  `Serilog.Extensions.Logging` reference was removed because the same 10.0.0
  assembly remains resolved through Service -> `Serilog.Extensions.Hosting`.
- Resolved output matches the target graph; in particular Service no longer
  resolves `Serilog.Sinks.Console`.
- Release solution build: 0 errors. Individual App, CLI, Service, and
  PoolAggregator builds: 0 errors. Android `net10.0-android36.0` Release build
  with the local JDK/SDK/libbox toolchain: 0 errors.
- Accessible local tests: 2640 passed, 2 skipped, 0 failed. The unfiltered run
  also passed 2683 tests but retained 25 documented dev-box failures caused by
  denied writes under `C:\ProgramData\VPNRouter`; clean-environment CI is the
  remaining Gate 2 check.
- Qwen's three out-of-scope suggestions were recorded in
  `plans/refactor-backlog.md`; none expanded DR-03.
