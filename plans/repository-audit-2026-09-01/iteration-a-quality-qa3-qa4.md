# Iteration A — Quality/architecture (QA-3, QA-4) raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `QA-3` (Dependencies, dead code, duplication) and `QA-4` (Docs, contracts, plans)
Status: unverified swarm output; no item below is accepted until lead source verification.

Each leaf (QA-3 and QA-4) completed 3 independent reviews across all lenses (correctness/data-flow, security/fail-closed/lifetime, and tests/platform/upstream), yielding 15 raw findings synthesized into 13 distinct candidate items. All candidates remain unverified pending lead source verification and Iteration B counter-examination. Note: PN-2-4 is a known duplicate fixed in green PR #204 but not merged.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| QA-3 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 10 | 8 |
| QA-4 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 5 | 5 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Reporters |
|---|---|---|---|---|
| QA-3-1 | P1 | CLI ProfileSourceFactory duplicates core profile source resolution logic | `VPNRouter.CLI/Helpers/ProfileSourceFactory.cs:7-42`; `VPNRouter.Core/Services/VpnEngine.cs:1292-1350` | correctness; duplication |
| QA-3-2 | P1 | CLI and Service hardcoded `%ProgramData%` paths bypass `AppPaths` cross-platform resolution | `VPNRouter.CLI/Program.cs:7`; `VPNRouter.Service/Program.cs:81`; `VPNRouter.CLI/Helpers/StateFile.cs:62`; `VPNRouter.CLI/Commands/StartCommand.cs:328`; `VPNRouter.CLI/Helpers/ProfileSourceFactory.cs:37` | correctness; platform |
| QA-3-3 | P2 | Avalonia NuGet package version drift between Desktop/Tests (12.1.1) and Android (12.0.3) | `VPNRouter.App/VPNRouter.App.csproj:39-43`; `VPNRouter.Tests/VPNRouter.Tests.csproj:52-53`; `VPNRouter.Android/VPNRouter.Android.csproj:130-133` | platform/upstream; dependencies |
| QA-3-4 | P2 | Reflection-based `ViewLocator` is registered in `App.axaml` but unused by strongly-typed ViewModel views | `VPNRouter.App/ViewLocator.cs:15-37`; `VPNRouter.App/App.axaml:8` | dead code; architecture |
| QA-3-5 | P2 | Dead `ScanResult.HasChanges` method retained in process scanner model | `VPNRouter.Core/Services/ProcessScanner.cs:284-289` | dead code; correctness |
| QA-3-6 | P2 | Redundant `ZapretUpdater` constructor overload and settable `Strings.Lang` shim | `VPNRouter.Core/Services/ZapretUpdater.cs:91-104`; `VPNRouter.App/Localization/Strings.cs:11-14` | duplication; architecture |
| QA-3-7 | P1 | Unscoped `pkill -f sing-box` in Linux stop escalation and helper scripts may terminate unrelated processes | `VPNRouter.Core/Services/SingBoxManager.LinuxStop.cs:48,65,86`; `packaging/linux/vpnrouter-update-helper:45` | security/lifetime; platform |
| QA-3-8 | P2 | Duplicate `IHttpClient` instantiation across background services bypassing shared `PolicyHttpClient` | `VPNRouter.Core/Services/PolicyHttpClient.cs:15-30`; `VPNRouter.Core/Services/FreeConfigs/FreeConfigFetcher.cs:25` | duplication; performance |
| QA-4-1 | P2 | `docs/REVIEW_AGENT_PROMPT.md` retains stale claim of open firewall kill-switch P1 resolved in v2.47.0-r3 | `docs/REVIEW_AGENT_PROMPT.md:38`; `plans/OPEN-DEFECTS.md:170` | docs/contracts; correctness |
| QA-4-2 | P2 | Stale documentation claims alleging `VpnEngine.cs` contains blocking `.Result` call | `docs/REVIEW_AGENT_PROMPT.md`; `plans/v3.0-architecture-roadmap.md:278`; `VPNRouter.Core/Services/VpnEngine.cs` | docs/contracts; architecture |
| QA-4-3 | P2 | `v3.0-execution-methodology.md` references outdated gating requirements and missing review prompt paths | `plans/v3.0-execution-methodology.md:141` | docs/contracts |
| QA-4-4 | P2 | `OPEN-DEFECTS.md` active P1 list retains candidate items superseded by fixed PRs | `plans/OPEN-DEFECTS.md:38-45` | docs/contracts; trace |
| QA-4-5 | P2 | Interaction contracts reference obsolete ViewModel property names and superseded layout structures | `plans/interaction-contracts/README.md:15-40` | docs/contracts |

## Lead status

Pending Iteration B and lead source verification. All candidates remain unverified until verified against source control flow and documentation. Note: PN-2-4 is a known duplicate fixed in green PR #204 but not merged.
