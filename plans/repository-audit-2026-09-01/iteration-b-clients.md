# Iteration B — Client counter-audit index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage: `CL-1` through `CL-4`
Status: independent adversarial counter-audit; signals are not lead verdicts.

## Coverage receipts

| Leaf | Reviews | Fresh lenses | A-candidate checks | New reports |
|---|---:|---|---:|---:|
| CL-1 | 3/3 | state transitions; failure concurrency/disposal; binding tests/negative evidence | 30 | 8 |
| CL-2 | 3/3 | layout/bindings; failure lifetime/accessibility; upstream Avalonia/negative evidence | 30 | 5 |
| CL-3 | 3/3 | state/ownership; failure PID/watcher/cleanup; upstream platform/negative evidence | 28 | 9 |
| CL-4 | 3/3 | data/migration; failure atomicity/recovery; schema/localization/negative evidence | 30 | 7 |

## Cross-iteration signals

| Candidate | Iteration B signal | Primary cited evidence |
|---|---|---|
| CL-1-1 | supported | `FreeConfigsPageViewModel.cs:187-203,1213,1776`; both tab bindings in `FreeConfigsPage.axaml` |
| CL-1-2 | supported | `MainWindowViewModel.Subscriptions.cs:271-292`; `MainWindowViewModel.cs:7095-7106` |
| CL-1-3 | contradictory | `MainWindowViewModel.Subscriptions.cs:248-401`; connection mode transitions |
| CL-1-4 | contradictory | `MainWindowViewModel.ServerTesting.cs:24-25,195-199,378-382` |
| CL-1-5 | mostly contradicted by explicit disposal | `MainWindowViewModel.Settings.cs:370-399`; `MainWindowViewModel.cs:2691` |
| CL-1-6 | contradictory | `FreeConfigsPageViewModel.cs:1257-1260,1620-1628,1830-1857,1914` |
| CL-1-7 | contradicted; cited subscription does not exist | `FreeConfigsPage.axaml.cs:26-34` |
| CL-1-8 | contradictory | `FreeConfigsPageViewModel.cs:383,597-609,683-690,1349` |
| CL-1-9 | contradicted; nullable integer contract present | `FreeConfigsPageViewModel.cs:112,126,286,289`; XAML bindings |
| CL-1-10 | supported | `MainWindowViewModel.cs:4943-4971,5255,5582,7155` |
| CL-2-11 | source shape supported, impact still bounded by tiny version output | `AboutWindow.axaml.cs:97-98` |
| CL-2-12 | supported | `AboutWindow.axaml.cs:99-100` |
| CL-2-13 | contradicted; content already wraps | `SubscribePage.axaml:274-278`; `DpiBypassPage.axaml:614-620` |
| CL-2-14 | contradicted; no `TabControl` at cited locations | `MainWindow.axaml:779,790`; `FreeConfigsPage.axaml:180` |
| CL-2-15 | measurement-gated framework behavior | `FreeConfigsPage.axaml.cs:18`; `SimplePage.axaml.cs:13`; `App.axaml.cs:26` |
| CL-2-16 | supported | icon-only buttons in `ApplicationsPage`, `ServersPage`, `NetworkPage` lack automation names |
| CL-2-17 | contradicted by actual ancestor binding | `FreeConfigsPage.axaml:379,388` |
| CL-2-18 | contradicted; cited brushes are semantic tokens | `NetworkPage.axaml:2317,2323,2329`; `Tokens.axaml:88,191` |
| CL-2-19 | measurement-gated | cited localized button content requires a 360px render measurement |
| CL-2-20 | contradicted by `try/catch` | `AboutWindow.axaml.cs:45-57` |
| CL-3-1 | supported | hardcoded `%ProgramData%` in CLI paths |
| CL-3-2 | supported | `VPNRouter.Service/Program.cs:81` |
| CL-3-3 | supported | `StopCommand.cs:45-54,81-106` |
| CL-3-4 | supported | `StopCommand.cs:30,47-64` |
| CL-3-5 | supported | `VPNRouterService.cs:117,237-244,438-471` |
| CL-3-6 | supported | `VPNRouterService.cs:449` |
| CL-3-7 | supported | `VPNRouter.Service/Program.cs:48-65` |
| CL-3-8 | contradictory | `ServiceInstaller.cs:52-85`; service dependency callers |
| CL-4-1 | supported | `SettingsLoader.cs:178-180,215-217` |
| CL-4-2 | contradicted; backups are exported | `DiagnosticsExporter.cs:88,442-469`; `SettingsLoader.cs:184,234` |
| CL-4-3 | supported | `SettingsLoader.cs:560`; `ISettingsStore.cs:30-40,127-128` |
| CL-4-4 | supported | `config.example.yaml:14,23,25,35,93`; `AppSettings.cs:45-48` |
| CL-4-5 | mostly contradicted by migration support | `VlessConfig.cs:9-88`; `SettingsMigrator.cs:38-40,446` |
| CL-4-6 | supported | App/Core/Android localization wrapper comparison |
| CL-4-7 | contradicted by migration test | `SettingsMigrator.cs:694-712`; `SettingsMigratorMtuTests.cs:102-122` |
| CL-4-8 | contradicted by validation test | `SettingsValidator.cs:256-267`; `SettingsValidatorTests.cs:239-247` |
| CL-4-9 | contradicted | `AppSettingsSane.cs:47,63-67` |
| CL-4-10 | supported | `SettingsLoader.cs:590-660`; `ISettingsStore.cs:116-148` |

## Materially new Iteration B candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| CL-B-1 | P1 | CLI state writes are non-atomic and may be quarantined by a concurrent read | `StateFile.cs:71,79-86` | pending lead trace |
| CL-B-2 | P1 | Owner shutdown exceeding five seconds may trigger fallback child kill during active cleanup | `StopCommand.cs:47-63,95-96` | pending lead trace |
| CL-B-3 | P1 | Service TUN-lock parking may not resume after desktop ownership ends | `VPNRouterService` ownership-listener path | pending lead trace |
| CL-B-4 | P1 | Validation-backup collision may overwrite or lose an invalid configuration | `SettingsLoader` quarantine and recovery paths | pending lead trace |
| CL-B-5 | P1 | Placeholder pruning may assign VLESS port zero and trigger destructive recovery | `SettingsMigrator` placeholder cleanup | pending lead trace |
| CL-B-6 | P2 | `RunSc` reads `ExitCode` after a timed-out wait while the process may still run | `ServiceInstaller.cs:227-231` | pending lead trace |
| CL-B-7 | P2 | Reset-to-defaults backup copy may fail on timestamp collision | `SettingsLoader` reset backup path | pending lead trace |

## Lead status

Iteration B coverage is complete. This wave rejected multiple visually plausible but source-false XAML and settings findings; the contradictory rows remain explicit to prevent accidental implementation of swarm hallucinations.
