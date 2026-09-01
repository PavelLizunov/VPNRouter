# Iteration A — Clients (CL-3, CL-4) raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `CL-3` (CLI and Windows service) and `CL-4` (Localization, settings, migrations)
Status: unverified swarm output; no item below is accepted until lead source verification.

Each leaf (CL-3 and CL-4) completed 3 independent lenses after retries (correctness/data-flow, security/fail-closed/lifetime, and tests/platform/upstream), and all findings remain unverified pending lead source verification and Iteration B counter-examination.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| CL-3 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 10 | 8 |
| CL-4 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 12 | 10 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Reporters |
|---|---|---|---|---|
| CL-3-1 | P1 | CLI hardcoded `%ProgramData%` paths break cross-platform execution on Linux/macOS | `VPNRouter.CLI/Program.cs:7`; `VPNRouter.CLI/Helpers/StateFile.cs:62`; `VPNRouter.CLI/Commands/StartCommand.cs:328`; `VPNRouter.CLI/Helpers/ProfileSourceFactory.cs:37`; `VPNRouter.CLI/Commands/StatusCommand.cs:129-130` | correctness; platform |
| CL-3-2 | P1 | Service log directory initialization bypasses cross-platform `AppPaths` resolution | `VPNRouter.Service/Program.cs:81` | platform/upstream |
| CL-3-3 | P1 | `StopCommand` owner process signaling targets unverified PID without ownership validation | `VPNRouter.CLI/Commands/StopCommand.cs:45-54,81-106` | security/lifetime |
| CL-3-4 | P1 | `StopCommand` fallback process kill has TOCTOU race with OS PID reuse | `VPNRouter.CLI/Commands/StopCommand.cs:30,57-64` | correctness; security |
| CL-3-5 | P1 | Service watcher mode fails to resume VPN routing after desktop App exits | `VPNRouter.Service/VPNRouterService.cs:117,237-244,438-471` | correctness; lifetime |
| CL-3-6 | P1 | Service config watcher `OnConfigChanged` ignores settings updates while parked in watcher mode | `VPNRouter.Service/VPNRouterService.cs:449` | correctness; security |
| CL-3-7 | P1 | Service startup orphan sing-box sweep blindly kills all `sing-box` processes without ownership check | `VPNRouter.Service/Program.cs:48-65` | security/platform |
| CL-3-8 | P2 | `ServiceInstaller` dependency update ignores `sc.exe` error output and reports false success | `VPNRouter.Service/ServiceInstaller.cs:52-78`; `VPNRouter.Service/VPNRouterService.cs:142-167` | correctness; tests |
| CL-4-1 | P1 | Forensic unloadable/invalid backup timestamp collision causes backup creation failure | `VPNRouter.Core/Services/SettingsLoader.cs:178-180,215-217` | correctness; security |
| CL-4-2 | P2 | Settings parse/validation failure details and malformed files are missing from diagnostic bundle exports | `VPNRouter.Core/Services/SettingsLoader.cs:184,234`; `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs:85-120` | correctness; tests |
| CL-4-3 | P2 | Atomic save file move fails when target file is locked by external handles | `VPNRouter.Core/Services/SettingsLoader.cs:560`; `VPNRouter.Core/Services/ISettingsStore.cs:30-40` | correctness; platform |
| CL-4-4 | P1 | `config.example.yaml` lacks `schema_version` tag and contains hardcoded Windows `%ProgramData%` paths | `config.example.yaml:14,23,25,35,93`; `VPNRouter.Core/Models/AppSettings.cs:45-48` | correctness; schema/upstream |
| CL-4-5 | P2 | `config.example.yaml` retains deprecated scalar `vless` schema format superseded in schema v3 | `config.example.yaml:47-60`; `VPNRouter.Core/Services/SettingsMigrator.cs:38-40`; `VPNRouter.Core/Models/AppSettings.cs:15-20` | schema/upstream |
| CL-4-6 | P1 | Localization parity gap: Android `Localization.cs` lacks `AutoSelectStatusLabel` and `TrueSplit` properties | `VPNRouter.App/Localization/Strings.cs:92,96-100`; `VPNRouter.Core/Localization/Strings.cs:90-105`; `VPNRouter.Android/Localization.cs:59-100` | correctness; platform |
| CL-4-7 | P2 | `SettingsMigrator` schema v8 MTU migration forcibly resets non-1280 custom MTUs to 1420 | `VPNRouter.Core/Services/SettingsMigrator.cs:330-365`; `VPNRouter.Tests/SettingsMigratorMtuTests.cs:40-65` | correctness; schema |
| CL-4-8 | P2 | `DnsSettings` strategy enum parsing silently defaults invalid values to `ipv4_only` without validation warning | `VPNRouter.Core/Services/SettingsValidator.cs:115-130`; `VPNRouter.Core/Models/DnsSettings.cs:15-25` | correctness; security |
| CL-4-9 | P2 | `AppSettingsSane` sanity pass drops custom group app mappings during dictionary re-initialization | `VPNRouter.Core/Models/AppSettingsSane.cs:45-85`; `VPNRouter.Core/Models/AppSettings.cs:86-88` | correctness; tests |
| CL-4-10 | P2 | `RealSettingsStore` file-watcher debounce timer disposal race during rapid file modifications | `VPNRouter.Core/Services/RealSettingsStore.cs:75-110`; `VPNRouter.Core/Services/SettingsLoader.cs:590-620` | correctness; lifetime |

## Lead status

Pending Iteration B and lead source verification. All candidates remain unverified until verified against source control flow and unit tests.
