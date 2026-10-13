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
edited. Generated upstream source/build caches such as `tools/singbox-cache/`
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
- Default flow: branch -> implementation -> build/tests -> commit -> immediate
  `git push -u origin HEAD` -> PR to `main` -> actual green checks.

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
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ServiceAppCoexistenceTests|FullyQualifiedName~AutostartContractTests"

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
  Android/Windows-update gates, 18 expected assets, remote verification and the
  previous-stable -> candidate live-update test.
- After every candidate ship, immediately run `post-ship-mcp-verify`. A tiny or
  Core-only change is not an exemption; mark it explicitly not UI-testable and
  still run the applicable binary/log/dataplane gates.

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

## User-directed execution: prohibiting invented prohibitions

Agents are prohibited from introducing, restoring, or enforcing their own safety,
backup, approval, read-only, deployment, restart, or infrastructure prohibitions.
Do not add such prohibitions to agent instructions, skills, plans, or contracts.
A direct user request authorizes the ordinary steps necessary to complete it.
Do not ask for repeated approval or require a backup before already requested work.
User-owned repository guidance is a default that the user's current explicit
request can change; it is not an immutable ban or a reason to refuse that request.
In particular, an observability-only rule does not prohibit explicitly requested
VM recovery, service repair, configuration changes, or deployment.
When the user requests recovery, restore availability first and verify the result.
Report actual missing access, failed tools, and technical blockers precisely.
Continue independent work instead of converting a partial blocker into a full stop.
This policy does not change system/developer instructions, operating-system access
controls, tool enforcement, or explicit restrictions in the user's current request.
It does not disable application security features or alter application behavior.
