# AI Agent Task Routing Guide

This guide maps common engineering tasks to exact source paths, data flows, invariants, and test fixtures across the VPNRouter ecosystem.

---

## 1. Routing by Task Type

### 1.1 "Change or Fix DNS Resolution & Routing"
- **Key Flow**: User Setting / Profile → `ConfigGenerator.Dns.cs` → Sing-box DNS outbounds & rules → Platform DNS hardening.
- **Entry Points**:
  - Setting: `VPNRouter.Core/Models/AppSettings.cs` (`DnsMode`, `StrictDnsOverride`)
  - Generator: `VPNRouter.Core/Services/ConfigGenerator.Dns.cs` (`BuildDnsObject`, `BuildDnsRules`)
  - Platform Hardening:
    - Windows: `VPNRouter.Core/Services/WindowsDnsHardening.cs`
    - Linux: `VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs`
    - macOS: `VPNRouter.Core/Platform/macOS/MacDnsHardening.cs`
  - Headless API: `VPNRouter.Headless/Features/SettingsFeature.cs` (`dnsMode`, `dnsModeSemantics`)
  - QML UI: `omarchy-vpnrouter/ui/SettingsView.qml` (`dnsModeVal`, `strictDnsVal`)
- **Key Invariants**:
  - `DnsMode.VpnOnly` routes DNS queries strictly through TUN to prevent ISP leakage.
  - Fail-closed: If DNS lockdown is active, port 53 outbound on physical adapters must be blocked.
- **Verification Tests**:
  - `VPNRouter.Tests/ConfigGeneratorStrictDnsOverrideTests.cs`
  - `VPNRouter.Tests/DnsLockdownPolicyTests.cs`
  - `VPNRouter.Tests/WindowsDnsHardeningTests.cs`
  - `VPNRouter.Tests/LinuxDnsHardeningTests.cs`

---

### 1.2 "Fix Process Lifecycles, Crashes, or Reconnects"
- **Key Flow**: `VpnEngine.StartAsync()` → `SingBoxManager.StartAsync()` → Child Process / TUN Lock → `HealthMonitor` / `AutoFailoverEngine`.
- **Entry Points**:
  - Orchestrator: `VPNRouter.Core/Services/VpnEngine.cs`
  - Sing-box Manager: `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs`, `SingBoxManager.CrashDetect.cs`
  - TUN Ownership:
    - Linux: `VPNRouter.Core/Services/LinuxTunOwnership.cs` (`flock` on `/run/user/<uid>/vpnrouter-tun.lock`)
    - Windows: `VPNRouter.Core/Services/TunOwnershipLock.cs` (`Global\VPNRouter-SingBox-Owner`)
  - Headless Session: `VPNRouter.Headless/RouterSession.cs`
  - Health & Recovery: `VPNRouter.Core/Services/HealthMonitor.cs`, `AutoFailoverEngine.cs`
- **Key Invariants**:
  - Generation Tracking: Every engine start increments `_engineGeneration`. Stale process exit callbacks matching old generations must be discarded.
  - Mutual Exclusion: Only one VPNRouter process may hold the TUN device at a time.
- **Verification Tests**:
  - `VPNRouter.Tests/VpnEngineLifecycleTests.cs`
  - `VPNRouter.Tests/SingBoxManagerLifecycleStressTests.cs`
  - `VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs`
  - `VPNRouter.Headless.Tests/LifecycleChecks.cs`

---

### 1.3 "Modify Headless Protocol v1 or Add RPC Methods"
- **Key Flow**: Client Stdin → `ProtocolLineReader` → `ProtocolParser` → `ProtocolServer` → `ProtocolDispatcher` → `RouterBackend` → Feature Handler → `ProtocolOutputQueue` → Client Stdout.
- **Entry Points**:
  - Protocol Specification: `plans/omarchy-protocol-v1.md`
  - Method Constants: `VPNRouter.Headless/Protocol/ProtocolConstants.cs` (37 allowed methods)
  - Parser & Request: `VPNRouter.Headless/Protocol/ProtocolParser.cs`, `ProtocolRequest.cs`
  - Concurrency & Dispatch: `VPNRouter.Headless/Protocol/ProtocolDispatcher.cs`
  - Backend Facade: `VPNRouter.Headless/RouterBackend.cs`
  - Feature Handlers: `VPNRouter.Headless/Features/*Feature.cs`
  - Storage & CAS: `VPNRouter.Headless/Storage/ConfigStorage.cs`
  - QML Client: `omarchy-vpnrouter/Service.qml`, `omarchy-vpnrouter/lib/Protocol.js`
- **Key Invariants**:
  - Max input frame: 256 KiB; max nesting depth: 32; duplicate JSON keys strictly forbidden.
  - Urgent methods (`cancel`, `disconnect`) bypass `busy` concurrency locks; ordinary methods return `busy` if an operation is active.
  - Zero-secret-leak: Exceptions must never expose paths, credentials, or stack traces on stdout.
- **Verification Tests**:
  - `VPNRouter.Headless.Tests/ProtocolTests.cs` (32 tests)
  - `VPNRouter.Headless.Tests/FeatureChecks.cs`
  - `VPNRouter.Headless.Tests/StorageChecks.cs`
  - `omarchy-vpnrouter/tests/test-ui-protocol.js`

---

### 1.4 "Debug Omarchy Plugin QML UI or Setup Script"
- **Key Flow**: Hyprland Shell / Quickshell → `Panel.qml` / `BarWidget.qml` → `Service.qml` (Process stdio) → `setup` (Arch packaging / environment).
- **Entry Points**:
  - Root Component: `omarchy-vpnrouter/Panel.qml`, `BarWidget.qml`
  - Backend Daemon Manager: `omarchy-vpnrouter/Service.qml`
  - Feature Views: `omarchy-vpnrouter/ui/*View.qml`
  - Shared Models & I18n: `omarchy-vpnrouter/lib/Model.js`, `I18n.js`
  - Packaging & Setup: `omarchy-vpnrouter/setup`
- **Key Invariants**:
  - No detached daemons: Closing shell/EOF on stdin must terminate `vpnrouter-headless`.
  - Stale mutations prevention: Mutation requests capture `revision` at dispatch time, not enqueue time.
- **Verification Tests**:
  - `omarchy-vpnrouter/tests/qml-test-runner.sh`
  - `omarchy-vpnrouter/tests/test-ui-service.js`
  - `omarchy-vpnrouter/tests/test-packaging.py`
