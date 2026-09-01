# Iteration A — Quality & architecture (QA-1, QA-2) raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `QA-1` (Tests and characterization) and `QA-2` (Large files, duplication, and coupling)
Status: unverified swarm output; no item below is accepted until lead source verification.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| QA-1 | 3/3 | correctness/determinism; test-harness/isolation; boundary-coverage/upstreams | 14 | 14 |
| QA-2 | 3/3 | code-coupling/god-files; duplication/seams; dependency-direction/partials | 17 | 14 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Reporters | Status |
|---|---|---|---|---|---|
| QA-1-1 | P1 | Permanently skipped autostart contract test suppresses DBG-2 verification | `VPNRouter.Tests/AutostartContractTests.cs:224` | test-harness/isolation | pending |
| QA-1-2 | P1 | Integration tests use silent early returns on missing binary/source without assertion or skip receipts | `VPNRouter.Tests/ConfigGeneratorDnsTunnelTests.cs:195`; `VPNRouter.Tests/CustomConfigInjectorTests.cs:961,1068,1138,1253,1299`; `VPNRouter.Tests/TgProxyManagerProcessRunnerTests.cs:131`; `VPNRouter.Tests/MainWindowViewModelAppsModeTests.cs:343,374,524` | correctness/determinism | pending |
| QA-1-3 | P1 | Test suite nondeterminism from live wall-clock timers, shared static state, and live network expectations | `VPNRouter.Tests/WgturnUpdaterTests.cs:244-259`; `VPNRouter.Tests/SingBoxManagerCronetTests.cs:37-53`; `VPNRouter.Tests/VisualDiffTests.cs:66-86`; `VPNRouter.Tests/FreeConfigAggregatorPreserveTests.cs:180` | correctness/determinism | pending |
| QA-1-4 | P1 | Source-scanning partial-file blind spots in source-pin regex tests | `VPNRouter.Tests/ProcessHandleDisposeOrderingTests.cs:69`; `VPNRouter.Tests/AutostartContractTests.cs:88,116,159,185`; `VPNRouter.Tests/HelperCmdParserGuardTests.cs:56,312`; `VPNRouter.Tests/SingBoxManagerStateMachineTests.cs:546` | test-harness/isolation | pending |
| QA-1-5 | P2 | Production-path writes in diagnostic test code and temporary file helpers | `VPNRouter.Tests/AndroidAppDumpMembersFact.cs:19`; `VPNRouter.Tests/TestEnvironmentSafety.cs:52` | test-harness/isolation | pending |
| QA-1-6 | P1 | IPv6 firewall coverage gaps in wire-shape and netsh/nftables test suites | `VPNRouter.Tests/FirewallManagerProcessRunnerWireShapeTests.cs:136-180`; `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:206-212`; `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:336-343` | boundary-coverage/upstreams | pending |
| QA-1-7 | P2 | BRAT verifier request-file cleanup missing on test abort or premature exit | `tools/brat-verify.ps1:185`; `tools/smoke-update.ps1:85`; `VPNRouter.Tests/PostShipVerifierContractTests.cs:285,510` | test-harness/isolation | pending |
| QA-1-8 | P1 | Platform-gated test skips mask Linux/macOS cross-platform runtime failures | `VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs:32,160,246,320,357`; `VPNRouter.Tests/TunAdapterReadinessTests.cs:54`; `VPNRouter.Tests/VpnEngineLifecycleTests.cs:450,474,511` | boundary-coverage/upstreams | pending |
| QA-1-9 | P2 | SkiaSharp visual diff tests rely on static rendering thresholds vulnerable to OS DPI variance | `VPNRouter.Tests/VisualDiffTests.cs:66-86`; `VPNRouter.Tests/VisualDiffHelper.cs:40-95` | correctness/determinism | pending |
| QA-1-10 | P2 | Fakes in test suite lack delay/cancellation simulation and stderr interleaving capabilities | `VPNRouter.Tests/Fakes/FakeProcessRunner.cs:246`; `VPNRouter.Tests/Fakes/FakeSingBoxApi.cs:111`; `VPNRouter.Tests/Fakes/InMemorySettingsStore.cs:105` | test-harness/isolation | pending |
| QA-1-11 | P2 | Server URI multi-parser tests omit boundary verification for invalid base64 and parameter injection | `VPNRouter.Tests/ServerUriParserTests.cs:166`; `VPNRouter.Tests/VlessUriParserTests.cs:139` | boundary-coverage/upstreams | pending |
| QA-1-12 | P2 | FreeConfig aggregator tests lack concurrency race coverage during rapid pool updates | `VPNRouter.Tests/FreeConfigAggregatorPreserveTests.cs:180`; `VPNRouter.Tests/FreeConfigDeepVerifyCheckpointTests.cs:42-110` | correctness/determinism | pending |
| QA-1-13 | P2 | Script execution contract tests invoke external powershell commands without environment isolation | `VPNRouter.Tests/PostShipVerifierContractTests.cs:285,510`; `VPNRouter.Tests/HelperCmdParserGuardTests.cs:56-94` | test-harness/isolation | pending |
| QA-1-14 | P2 | Public surface hash helpers ignore compiler-generated members and accessor method contract shifts | `VPNRouter.Tests/PublicSurfaceHashHelper.cs:80-92`; `VPNRouter.Tests/AndroidAppSourceSurfaceHashHelper.cs:334,462,547` | boundary-coverage/upstreams | pending |
| QA-2-1 | P1 | NetworkPage state coupling directly binds and mutates connection and adapter ViewModels | `VPNRouter.App/Views/Pages/NetworkPage.axaml:2317-2329`; `VPNRouter.App/Views/Pages/NetworkPage.axaml.cs:80,150-220`; `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs` | code-coupling/god-files | pending |
| QA-2-2 | P1 | FreeConfigs duplicated orchestration and cancellation tracking between ViewModel and Core Aggregator | `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:187-203,383,600,685,1266-1292`; `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs:87-98,185-198` | duplication/seams | pending |
| QA-2-3 | P1 | Localization duplication re-declares string dictionaries across Core, App, and Android assemblies | `VPNRouter.Core/Localization/Strings.cs:90-105`; `VPNRouter.App/Localization/Strings.cs:92,96-100`; `VPNRouter.Android/Localization.cs:59-100` | duplication/seams | pending |
| QA-2-4 | P1 | ConfigGenerator and CustomConfigInjector duplication and order-dependent rule processing passes | `VPNRouter.Core/Services/ConfigGenerator.cs:369,1599,1990-2049`; `VPNRouter.Core/Services/CustomConfigInjector.cs:155,288,834-848,1282-1306` | duplication/seams | pending |
| QA-2-5 | P1 | VpnEngine CancellationTokenSource lifetime across reconnects risks memory leaks and orphan TUN state | `VPNRouter.Core/Services/VpnEngine.cs:990-1010,1625`; `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs:393,429`; `VPNRouter.Core/Services/AutoFailoverEngine.cs:35` | dependency-direction/partials | pending |
| QA-2-6 | P1 | Android server testing and subscription tasks release SemaphoreSlim instances without ownership checks | `VPNRouter.Android/AndroidApp.ServerList.cs:1363`; `VPNRouter.Android/AndroidApp.SubscribePage.cs:746` | dependency-direction/partials | pending |
| QA-2-7 | P1 | Monolithic MainWindowViewModel god-file handles presentation, connection, updates, and IPC in 7,300+ lines | `VPNRouter.App/ViewModels/MainWindowViewModel.cs:1-7335`; `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:1-985` | code-coupling/god-files | pending |
| QA-2-8 | P2 | Oversized NetworkPage.axaml view file contains 2,400+ lines of inline control definitions and overrides | `VPNRouter.App/Views/Pages/NetworkPage.axaml:1-2478` | code-coupling/god-files | pending |
| QA-2-9 | P1 | AndroidApp activity partial classes cross-depend on mutable global static state and UI controls | `VPNRouter.Android/AndroidStorage.cs:1-1694`; `VPNRouter.Android/MainActivity.cs:1-1420`; `VPNRouter.Android/AndroidApp.ServerList.cs:1-1555` | dependency-direction/partials | pending |
| QA-2-10 | P1 | Platform process and firewall management in Linux and macOS adapters bundles OS commands with logic | `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:1-269`; `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:1-412`; `VPNRouter.Core/Services/SingBoxManager.LinuxStop.cs:1-86` | code-coupling/god-files | pending |
| QA-2-11 | P2 | Duplicate MTU calculation and interface metric detection across Core engines and platform helpers | `VPNRouter.Core/Services/SettingsMigrator.cs:330-365`; `VPNRouter.Core/Services/TunAdapterDiagnostics.cs:1-993`; `VPNRouter.Core/Services/SplitTunnelDriverManager.cs:1-1190` | duplication/seams | pending |
| QA-2-12 | P1 | UpdateChecker couples download, payload extraction, backup restore, and elevated helper spawning | `VPNRouter.Core/Services/UpdateChecker.cs:1-1512`; `VPNRouter.Core/Services/UpdateBackup.cs:1-266` | code-coupling/god-files | pending |
| QA-2-13 | P2 | StartupPipeline coordinates 12+ sequential startup phases with direct static method calls | `VPNRouter.Core/Services/StartupPipeline.cs:1-1419` | dependency-direction/partials | pending |
| QA-2-14 | P2 | Obsolete legacy config migration branches and deprecated schema handlers persist in core services | `VPNRouter.Core/Services/SettingsMigrator.cs:38-40,330-365`; `VPNRouter.Core/Models/AppSettings.cs:15-20,86-88` | duplication/seams | pending |

## Lead status

Pending Iteration B and source verification. Similar-looking candidates remain separate until the lead traces their actual control flow.
