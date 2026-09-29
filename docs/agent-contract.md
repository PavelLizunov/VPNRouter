# VPNRouter agent contract

This file owns VPNRouter repository rules. Root `AGENTS.md` contains entry points
and task routing; zone files add local details without duplicating this contract.
System, developer, direct user instructions and global Harness safety/model-routing
rules still apply. Resolve conflicting project guidance within the authorized task;
this document does not grant tools, permissions or authority beyond that scope.

## Project and ownership

VPNRouter is a process-based split-tunnel VPN router for Windows, macOS, Linux
and Android. Desktop code uses .NET, Avalonia and sing-box. Read `global.json`
for the SDK, owning `.csproj` files for frameworks/packages, and platform build
scripts/workflows for bundled runtime versions; do not infer one version for all targets.

All repository zones are owned by Pavel Lizunov (`PavelLizunov`) and may be
edited. `tools/zapret/` is a legacy vendored Zapret copy: the app downloads
Zapret on demand and no build or test consumes these files; do not delete it
without an owner decision. Generated upstream source/build caches such as `tools/singbox-cache/`
remain untracked.

Read the relevant zone document before changing that area:

| Area | Zone document |
|---|---|
| Core, sing-box, subscriptions, public configs | `VPNRouter.Core/AGENTS.md` |
| Core lifecycle and service entry points | `VPNRouter.Core/Services/AGENTS.md` |
| OS adapters, firewall and DNS boundaries | `VPNRouter.Core/Platform/AGENTS.md` |
| Avalonia desktop UI | `VPNRouter.App/AGENTS.md` |
| ViewModels and connection-state ownership | `VPNRouter.App/ViewModels/AGENTS.md` |
| Android | `VPNRouter.Android/AGENTS.md` |
| CLI | `VPNRouter.CLI/AGENTS.md` |
| Windows service | `VPNRouter.Service/AGENTS.md` |
| Tests | `VPNRouter.Tests/AGENTS.md` |
| Go launcher/trampoline | `VPNRouter.GUI/AGENTS.md` |
| Developer utilities | `VPNRouter.Tools/AGENTS.md` |
| Repository scripts | `tools/AGENTS.md` |
| GitHub Actions | `.github/workflows/AGENTS.md` |
| Git hooks | `.githooks/AGENTS.md` |
| Installers and packages | `packaging/AGENTS.md` |
| Plans and outcomes | `plans/AGENTS.md` |
| Project DSH skills | `.dsh/AGENTS.md` |
| Documentation and contracts | `docs/AGENTS.md` |

## Working model

- Record intent, scope, invariants, verification and material unknowns under `plans/`
  before edits. Reuse the task record and applicable global `sdd` guidance.
- Follow global Harness model-routing rules and live tool permissions. Repository
  skills cannot authorize implicit self-spawning, recursive worker delegation or
  an unavailable tool. Report missing independent review instead of claiming it.
- Treat architecture maps and dated plans as snapshot-bound navigation. Verify
  claims against current source and the defect ledger; do not import another
  branch's components or resolved findings into the current contract.
- Work autonomously inside a task branch through a green pull request. Normal
  code edits, tests, commits, branch pushes and PR updates need no intermediate
  approval.
- Tags, releases, deployments, merges and stable cuts require an explicit owner
  command. Verification readiness never grants release authority by itself.
- Default flow: branch -> implementation -> build/tests -> commit -> immediate
  `git push -u origin HEAD` -> PR to `main` -> actual green checks.
- Make a concrete, low-risk assumption when scope is clear. Ask only for a real
  semantic choice, new authority or a destructive action whose target cannot be
  proven.
- For changes over 30 lines, use `phase-task-launcher`. User-reported
  release hotfixes use `ship-rolling-candidate` only after explicit ship
  authorization. Run `bug-hunt` after non-trivial features and before stable.

## Safety and Git

1. `main` is protected. Never push directly or force-push it. Published stable
   and candidate tags are immutable; fixes use a new version, not a rewritten tag.
   [Tag and release retention](tag-retention-policy.md) owns naming, preservation,
   service exceptions and separately authorized cleanup. No automatic tag pruning.
2. Canonical remote is `origin` (GitHub). Forgejo is a mirror synchronized only
   after an accepted merge or release. Push only the current task branch with
   `git push -u origin HEAD`; never add, rename or rewrite remotes automatically.
   Avoid broad fetches when remote auxiliary refs are unhealthy; prefer `gh api`
   or targeted refs.
3. Never use `--no-verify` or `--no-gpg-sign` without an explicit owner request.
   Fix failing hooks instead of bypassing them.
4. Preserve user changes in a dirty worktree. Do not use destructive reset,
   checkout or broad recursive deletion to make the tree clean.
5. Never put credentials, subscription URLs, tokens, UUIDs or raw live logs in
   commits, screenshots, PR text or chat. Generated external binaries remain
   untracked unless the repository explicitly owns them as a bundled payload.
6. Do not add emoji to code, config or documentation. Existing public README
   platform icons may remain until a dedicated presentation edit replaces them.
7. `process_name` matching in sing-box is case-sensitive. Deduplicate with
   `StringComparer.OrdinalIgnoreCase` without lowercasing stored values.
8. `AppVersion.Version` must exactly match every release tag, including `-rN`.

## Test oracle

Use the smallest relevant slice while iterating, then the applicable full gate
before handoff. For instruction-only changes, check links, referenced test names,
skill frontmatter and the scoped diff, and observe PR CI; do not claim these checks
prove runtime behavior. Commands assume the SDK pinned by `global.json`. On `harness-test`,
coordinate these commands on an authorized exact-SHA worker only after the
read-only identity/job/CPU/RAM/disk/SDK preflight; do not provision the control
plane.

```powershell
# Core config and connection lifecycle
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ConfigGeneratorTests|FullyQualifiedName~VpnEngineStartAsyncSeamTests|FullyQualifiedName~SingBoxManagerProcessRunnerTests"

# Desktop ViewModel and binding contract
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelCharacterizationTests|FullyQualifiedName~MainWindowViewModelAppsModeTests|FullyQualifiedName~MainWindowViewModelTests"

# Android shared behavior (APK build still requires the Android workload)
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~AndroidStorageSaneTests|FullyQualifiedName~AndroidDpiBypassInjectorTests"

# CLI and service contracts
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~P07CliStopSourceGuardTests|FullyQualifiedName~ServiceAppCoexistenceTests|FullyQualifiedName~AutostartContractTests"

# Release, remote verification and packaging contracts
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"

# Full gate on an authorized preflighted build worker
dotnet build VPNRouter.sln -c Release
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build
```

UI changes additionally require headless screenshots and the exact end-to-end
user scenario on WINBRAT after a candidate is shipped. Android changes require
the Release APK build command from `VPNRouter.Android/AGENTS.md`.

## Commit and CI discipline

- Commit each verified task block separately with a conventional message and
  outcome update. Push immediately after the commit.
- After every push, before the next code change, run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1
```

  Exit 0 permits the next block. Any in-progress or red result means stop and
  wait/fix. Never accumulate known-red commits. If PowerShell is unavailable,
  use `gh pr checks <number> --watch` for the task PR and confirm its `headRefOid`
  equals local `git rev-parse HEAD` with `gh pr view <number> --json headRefOid`.
  Missing expected checks, a different head or unavailable evidence is not green.
  This is a development-check path, not a substitute for strict release gates.
  Git has no native post-push hook; ordinary task-branch pushes do not run the
  optional `.githooks/post-push` watcher. Do not assume it verified CI.
- At session start and before a candidate ship, inspect recent commit checks and
  `.git-suggested-hash-bump.txt`. Fix any red check before new product work.
- Every audit/review/research finding goes into `plans/OPEN-DEFECTS.md` before it
  is implemented or deferred, with evidence, severity, status and eventual
  implementation/PR reference. Existing P0/P1 gate semantics are authoritative.

## Release and WINBRAT contract

Use the release skills; do not reproduce their commands from memory.

- `ship-rolling-candidate`: only after explicit owner authorization. The
  candidate version and tag must match. Wait for all platform workflows and the
  exact expected asset set.
- `cut-stable`: only after an explicit `cut`, `ok` or `promote` command. Readiness
  requires a clean Release build, full green tests, exact-SHA CI, macOS/Linux/
  Android/Windows-update gates, 16 expected assets, remote verification and the
  previous-stable -> candidate live-update test.
- After every candidate ship, immediately run `post-ship-mcp-verify`. A tiny or
  Core-only change is not an exemption; mark it explicitly not UI-testable and
  still run the applicable binary/log/dataplane gates.

All install, launch, connect, stop, UIA and live-log operations target only the
fixed test VM `WINBRAT` at `100.115.182.0`, via `tools/brat-verify.ps1`. Every
remote mutation must re-check the machine identity. Never install or control
VPNRouter under `C:\Program Files\VPNRouter` on the developer machine, and never
use local mouse/screen tools as a fallback. If WINBRAT, WinRM or credentials are
unavailable, stop and report the blocker.

Remote verification must exercise the complete user scenario, all interactive
elements in scope, the viewport bottom, expected strings, proxy HTTPS/UDP where
applicable, lifecycle classification and sanitized logs. Live desktop capture
is forbidden because the active config is secret-bearing; visual evidence uses
isolated headless page screenshots.

## Infrastructure reference

| Resource | Canonical location |
|---|---|
| GitHub | `PavelLizunov/VPNRouter` |
| Forgejo mirror | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` |
| Worker inventory and aliases | `docs/test-workers.md` |
| WINBRAT fixed verification identity | `100.115.182.0`, MachineName `WINBRAT`, Proxmox VM 100 |
| Install domain | `vpn.ninitux.com` |
| Homebrew tap | `PavelLizunov/homebrew-vpnrouter` |
| APT repository | `vpn.ninitux.com/apt/` |

Private credentials and expanded host details stay outside tracked repository content. Use DSH session goals and `plans/` task outcomes for durable state; never copy secrets into either. Harness runtime, settings, hooks, caches, and auto-managed memory are not project content unless the owner explicitly asks to change them.
