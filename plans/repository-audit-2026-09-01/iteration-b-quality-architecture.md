# Iteration B — Quality/architecture counter-audit index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage: `QA-1` through `QA-4`
Status: independent adversarial counter-audit; signals are not lead verdicts.

## Coverage receipts

| Leaf | Reviews | Fresh lenses | A-candidate checks | New reports |
|---|---:|---|---:|---:|
| QA-1 | 3/3 | false-confidence adversary; isolation/reproduction failure injection; harness negative evidence | 42 | 9 |
| QA-2 | 3/3 | coupling measurement; change-risk failure injection; minimal-split negative evidence | 42 | 9 |
| QA-3 | 3/3 | usage search; deletion-risk measurement; negative-evidence ponytail | 34 | 3 |
| QA-4 | 3/3 | source divergence; operator-safety failure cases; links/upstream negative evidence | 23 | 2 |

## Cross-iteration signals — QA-1 tests

| Candidate | Signal |
|---|---|
| QA-1-1 skipped autostart contract | supported |
| QA-1-2 silent binary/source returns | contradicted as intentional platform isolation |
| QA-1-3 live timers/network nondeterminism | supported |
| QA-1-4 source-regex partial-file blind spots | supported |
| QA-1-5 production-path test writes | contradicted by temporary `AppPaths` isolation |
| QA-1-6 missing IPv6 firewall coverage | contradicted by production/tests |
| QA-1-7 BRAT cleanup omission | contradicted by cleanup block |
| QA-1-8 platform skips as false pass | contradicted as intentional isolation |
| QA-1-9 visual-diff DPI sensitivity | supported |
| QA-1-10 fakes lacking delay/cancellation/interleaving | supported |
| QA-1-11 URI negative-boundary gaps | supported |
| QA-1-12 free-config concurrency coverage gap | supported |
| QA-1-13 live PowerShell execution in contract tests | contradicted; tests inspect source |
| QA-1-14 surface hash omits accessor shifts | supported |

## Cross-iteration signals — QA-2 large files/coupling

| Candidate | Signal |
|---|---|
| QA-2-1 NetworkPage code-behind coupling | contradicted; cited lines do not exist |
| QA-2-2 ViewModel/Aggregator duplication | contradicted by presentation/domain split |
| QA-2-3 localization dictionary duplication | contradicted; App properties pass through to Core |
| QA-2-4 generator/injector duplication | contradicted; typed generation vs raw JSON mutation |
| QA-2-5 VpnEngine CTS leak | contradicted by explicit cancellation/disposal |
| QA-2-6 Android semaphore release | contradicted; `WaitAsync` is before `try` |
| QA-2-7 MainWindowViewModel size | measurement-gated; partials/characterization exist |
| QA-2-8 NetworkPage XAML size | measurement-gated |
| QA-2-9 Android partial size | measurement-gated |
| QA-2-10 platform commands coupled to logic | contradicted by `IProcessRunner` |
| QA-2-11 duplicate MTU/metrics | contradicted; cited code has different responsibilities |
| QA-2-12 UpdateChecker size | measurement-gated |
| QA-2-13 StartupPipeline size | measurement-gated |
| QA-2-14 migration branches as dead code | contradicted; required compatibility path |

## Cross-iteration signals — QA-3 simplification

| Candidate | Signal | Primary evidence |
|---|---|---|
| QA-3-1 CLI ProfileSourceFactory duplication | supported | `ProfileSourceFactory.cs:7-42`; `VpnEngine.cs:1292-1350` |
| QA-3-2 AppPaths bypass | supported/duplicate CL-3 | CLI/Service `%ProgramData%` paths |
| QA-3-3 Avalonia version drift | supported | App/Tests 12.1.1 vs Android 12.0.3 project references |
| QA-3-4 unused ViewLocator | supported pending runtime registration trace | `ViewLocator.cs:15-37`; `App.axaml:8` |
| QA-3-5 dead `ScanResult.HasChanges` | supported | `ProcessScanner.cs:284-289`; zero callers reported |
| QA-3-6 Zapret overload/Lang shim | contradicted by active callers |
| QA-3-7 unscoped `pkill -f` | supported/duplicate SU-3 |
| QA-3-8 direct HttpClient in FreeConfigFetcher | supported/overlaps response-bound finding |

## Cross-iteration signals — QA-4 docs/contracts

| Candidate | Signal |
|---|---|
| QA-4-1 stale open firewall P1 in active review prompt | supported |
| QA-4-2 stale `.Result` claim | contradicted for active docs; cited roadmap is historical |
| QA-4-3 missing/outdated contract paths | contradicted; paths exist and remain active |
| QA-4-4 resolved items left active | contradicted/measurement-gated; checked items preserve history |
| QA-4-5 obsolete interaction property/layout names | contradicted; contracts are abstract behavioral states |

## Materially new Iteration B candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| QA-B-1 | P1 | ETW monitor reuse may retain a set readiness event and orphan a restarted trace session | `EtwProcessMonitor.cs` lifecycle; duplicate PN-1-1 | pending lead trace |
| QA-B-2 | P1 | Free-config verifier output callbacks mutate a shared `StringBuilder` without synchronization | `FreeConfigDeepVerifier` callback paths | pending lead trace |
| QA-B-3 | P2 | Shared mutable static test runners/paths/HTTP clients can race under parallel xUnit execution | `TunAdapterDiagnostics.Runner`; `AppPaths.DataDir`; `SubscriptionFetcher.Http` | pending lead trace |
| QA-B-4 | P2 | Headless GUI smoke tests may instantiate a ViewModel against live host configuration | headless `MainWindowViewModel` test setup | pending lead trace |
| QA-B-5 | P2 | Duplicated administrator checks diverge across App, CLI, and Core | `App/Program.cs:512-519`; `AdminHelper.cs:18-30`; `ZapretAutoStrategy.cs:1195` | pending lead trace |
| QA-B-6 | P3 | `ProcessQuery.CountAlive` has no product callers | `ProcessQuery.cs:69-86` | pending deletion proof |
| QA-B-7 | P1 | Windows installer skips verification when the sidecar is absent | `install.ps1:206-208`; duplicate BR-2-6 | pending lead trace |

## Lead status

Iteration B coverage is complete. This category rejected most generic god-file and coupling claims because they lacked a concrete extraction seam; only measured, source-backed candidates proceed to lead triage.
