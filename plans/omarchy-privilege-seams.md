# Omarchy Linux Privilege Seams & Security Architecture

**Document Version:** 1.0.0  
**Date:** 2026-09-17  
**Status:** Approved-scope Architectural Design & Source Evidence Task  
**Target:** Omarchy Quattro Port (`io.github.pavellizunov.vpnrouter` / `VPNRouter.Headless`)  
**Scope:** Read-only architectural evidence, additive Core seam definitions, and security analysis. Coordinator decides implementation.

---

## 1. Executive Summary & Problem Formulation

### 1.1 Context & Objectives
The Omarchy Quattro shell plugin (`omarchy-vpnrouter`) embeds VPNRouter into a native Wayland/Hyprland desktop environment using Quickshell and QML. The backend host is `VPNRouter.Headless`, a .NET 10 console daemon communicating via standard input/output NDJSON framing (`plans/omarchy-protocol-v1.md`).

To achieve true feature parity with the Windows desktop application, the Linux platform must support:
1. **Firewall Kill-Switch** (`block_on_vpn_fail`): Global egress blocking via `nftables` in full-tunnel mode that drops all outbound traffic except loopback, RFC1918/LAN, and active VPN server IPs so the tunnel can reconnect.
2. **DNS Leak Lockdown** (`dns_leak_lockdown`): System resolver pinning via `systemd-resolved` (`resolvectl`) so queries never leak out physical network adapters.
3. **Safe Process Lifecycle & Exact-Owner Stop**: Capability-based user launch of `sing-box`, exact pidfd-based signal escalation, and cross-process exclusive mutual exclusion.
4. **Typed Capability & Readiness Reporting**: Accurate reflection of whether the environment can truthfully support kill-switch and DNS lockdown without optimistic assumptions or false claims.

### 1.2 The Current Privilege Impasse
Analysis of existing Core Linux platform implementations reveals four structural privilege defects:
1. **NOPASSWD Dependency & Fail-Open in Firewall** (`LinuxFirewallManager.cs:38-44, 152-164`):
   The existing firewall manager executes `sudo -n nft -f <rulesetPath>`. It expects a pre-configured `NOPASSWD` sudoers entry for `nft`. When missing, it logs a warning and fails open (traffic is unblocked). In the Omarchy environment, granting arbitrary `NOPASSWD` sudo execution for `nft` is explicitly forbidden by security policy (`plans/OPEN-DEFECTS.md:30`). Furthermore, executing `sudo -n nft -f` against a ruleset written to a user-writable directory (`~/.config/vpnrouter/vpnrouter-nft-killswitch.conf`) creates a severe local privilege escalation vector.
2. **Untruthful / Hardcoded Capability Invalidation** (`PlatformCapabilityVerifier.cs:19-22, 99-105`):
   Because no verified privilege mechanism exists, `PlatformCapabilityVerifier.VerifyKillSwitchSupport()` fails closed (`return false;`), and `PlatformCapabilityVerifier.VerifyDnsLockdownSupport()` unconditionally returns `false` on Linux (`OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()`). Consequently, `RouterSession.cs:153-164` rejects connections with `RouterException("unavailable")` whenever a user enables kill-switch or DNS lockdown.
3. **Broken Cross-Process Mutual Exclusion** (`TunOwnershipLock.cs:78-99`):
   The mutex `Global\VPNRouter-SingBox-Owner` relies on Windows named semaphore namespaces. On Linux, `new Semaphore(1, 1, @"Global\...", out _)` throws `PlatformNotSupportedException` or `IOException`, which is caught and swallowed by `return true; // fail-open`. As a result, cross-process mutual exclusion is completely inoperative on Linux, leaving `LinuxOwnershipGuard.cs` to rely on non-atomic, TOCTOU-vulnerable process and JSON file inspections (`plans/OPEN-DEFECTS.md:29`).
4. **Headless Disconnect Rejection in Elevated Stop** (`SingBoxManager.LinuxStop.cs:211-219`):
   In the elevated kill chain, `ResolveSignalHelperHost()` restricts the trusted helper host executable to `name is "VPNRouter.App" or "VPNRouter.CLI"`. When `VPNRouter.Headless` attempts an elevated stop, the host path resolves to `null` and the stop is refused with an error (`SingBoxManager.LinuxStop.cs:70-75`). Furthermore, `VPNRouter.Headless/Program.cs` does not wire `UnixOwnedProcessSignal.TryHandleHelper(args)`.

### 1.3 Recommended Architectural Strategy
The solution is **not** to run `VPNRouter.Headless` or a broad C# daemon as root. Doing so would violate the principle of least privilege, expose complex configuration parsing to root execution, and create massive attack surfaces.

Instead, the production wiring separates privileges into three strict tiers:
1. **User Engine (Long-Lived, Zero-Privilege)**:
   `VPNRouter.Headless` and the spawned `sing-box` process run entirely as the unprivileged desktop user (UID 1000). The `sing-box` binary is granted narrow file capabilities (`cap_net_admin,cap_net_bind_service=+eip`), enabling it to create the TUN interface `VPNRouter-TUN` and bind port 53/9090 without root authority.
2. **Root-Owned Narrow Broker (Ephemeral, Operation-Specific)**:
   A minimal, audited root-owned helper binary (`/usr/libexec/vpnrouter/vpnrouter-root-helper`) controlled by a dedicated Polkit policy (`io.github.pavellizunov.vpnrouter.policy`). The broker exposes strictly validated, parameterized verbs:
   - `firewall-enable <table-suffix> <iface> <server-ip-list>`
   - `firewall-disable <table-suffix>`
   - `firewall-status <table-suffix>`
   - `dns-pin <iface> <dns-target>`
   - `dns-revert <iface>`
   - `signal-owned <pid> <start-ticks> <exe-path> <signal>`
   The helper rejects arbitrary rule files, arbitrary command arguments, and non-literal IPs.
3. **Flock-Based Cross-Process Mutex**:
   Replace the broken `Global\` semaphore on Unix with a kernel-managed advisory file lock (`flock`) on `/run/user/<uid>/vpnrouter-tun-owner.lock`, guaranteeing atomic mutual exclusion and automatic kernel cleanup on process death.

---

## 2. Source Code Evidence & Analysis

### 2.1 LinuxFirewallManager (`VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`)
- **Execution Mechanism** (`lines 152, 309, 317, 624-642`):
  ```csharp
  // Line 152:
  var load = RunSudo(new[] { "-n", Nft, "-f", _rulesetPath });
  // Line 309:
  if (RunSudo(new[] { "-n", Nft, "delete", "table", "inet", TableName }).ok)
  // Line 317:
  var (ok, stdout, _) = RunSudo(new[] { "-n", Nft, "-j", "list", "tables" });
  ```
- **Ruleset Path** (`lines 89, 141-144`):
  `_rulesetPath` defaults to `System.IO.Path.Combine(AppPaths.DataDir, "vpnrouter-nft-killswitch.conf")`, located in `~/.config/vpnrouter/`.
- **Privilege Vulnerability & Invariant Violation**:
  - Requires `sudo -n` without password. Without a sudoers file granting `tester ALL=(ALL) NOPASSWD: /usr/bin/nft`, every call exits non-zero (`code 1: sudo: a password is required`).
  - Running `sudo nft -f <user-file>` allows an unprivileged user to craft arbitrary `nftables` configurations that root executes verbatim.
  - Fail-safe behavior (`lines 159-164`): On failure, it logs a warning and does **not** block, silently defeating the killswitch guarantee.

### 2.2 LinuxDnsHardening (`VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs`)
- **Execution Mechanism** (`lines 43-44, 101-102, 155, 177-219`):
  ```csharp
  var dnsOk = RunResolvectl(new[] { "dns", iface, dnsTarget }, logger);
  var domainOk = RunResolvectl(new[] { "domain", iface, DefaultRoutingDomain }, logger);
  ...
  RunResolvectl(new[] { "revert", state.Interface }, logger);
  ```
- **Privilege Reality**:
  - `resolvectl` communicates with `systemd-resolved` over D-Bus (`org.freedesktop.resolve1`).
  - Polkit actions `org.freedesktop.resolve1.set-dns-servers` and `org.freedesktop.resolve1.set-domains` govern these calls. On standard desktop systemd installations, active graphical sessions are frequently permitted, but restricted environments or custom Polkit profiles require administrative authentication.
  - Hardcoded refusal (`VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:19-22`):
    ```csharp
    public static bool VerifyDnsLockdownSupport()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }
    ```
    Linux is completely omitted, artificially blocking DNS leak lockdown even when `resolvectl` is fully functional.

### 2.3 SingBoxManager Launch & Stop (`VPNRouter.Core/Services/SingBoxManager.*`)
- **Capability-Mode Launch** (`SingBoxManager.Lifecycle.cs:896-921`):
  ```csharp
  if (HasNetCapability(exePath))
  {
      _logger.Information("[SingBoxManager] Linux: launching as user (CAP_NET_ADMIN present, no pkexec needed)");
      _linuxUsedPkexec = false;
      spawnExe = exePath;
      spawnArgs = new[] { "run", "-c", _currentConfigPath };
  }
  ```
  `HasNetCapability` (`SingBoxManager.Lifecycle.cs:724-760`) shells out to `getcap` to verify `cap_net_admin` and `cap_net_bind_service`.
  When capabilities are present, `sing-box` runs as the unprivileged user.
- **Capability-Mode Stop** (`SingBoxManager.Lifecycle.cs:206-261`):
  When `!_linuxUsedPkexec`, `_handle` points to the real `sing-box` process. `targetHandle.Kill(entireProcessTree: true)` terminates the process directly without elevation.
- **Elevated Stop Escalation Chain** (`SingBoxManager.LinuxStop.cs:22-108`):
  When elevation is used (`_linuxUsedPkexec == true`):
  1. Direct `pidfd` SIGTERM (`UnixOwnedProcessSignal.SignalLinux(owned, signal: 15)`).
  2. If surviving, helper via `pkexec`:
     `pkexec <hostPath> --vpnrouter-internal-signal-owned-v1 <pid> <ticks> <exe> 9`
  3. Final backstop: `sudo -n <hostPath> ... 9`.
- **Defect in Helper Host Resolution** (`SingBoxManager.LinuxStop.cs:211-219`):
  ```csharp
  private static string? ResolveSignalHelperHost()
  {
      var path = Environment.ProcessPath;
      if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
          return null;

      var name = Path.GetFileNameWithoutExtension(path);
      return name is "VPNRouter.App" or "VPNRouter.CLI" ? path : null;
  }
  ```
  `VPNRouter.Headless` is absent from this pattern match. Under headless execution, `hostPath` is `null`, and elevated stop fails immediately (`SingBoxManager.LinuxStop.cs:69-75`).

### 2.4 TunOwnershipLock & LinuxOwnershipGuard
- **Named Semaphore Failure** (`VPNRouter.Core/Services/TunOwnershipLock.cs:78-99`):
  ```csharp
  try {
      _semaphore ??= new Semaphore(1, 1, MutexName, out _);
  } catch (Exception ex) {
      _logger.Warning(ex, "[TunLock] Failed to create semaphore (continuing without lock)");
      return true; // fail-open
  }
  ```
  `MutexName` is `@"Global\VPNRouter-SingBox-Owner"`. Named semaphores with namespaces are a Windows kernel concept. On Linux, this throws, catches, and returns `true`, completely disabling cross-process locking.
- **Non-Atomic Inspection** (`VPNRouter.Headless/Lifecycle/LinuxOwnershipGuard.cs:38-42, 54`):
  `LinuxOwnershipGuard` probes `runtime-owner.json`, scans `/proc` via `Process.GetProcessesByName`, and cross-validates. As documented in its header, this check is non-atomic and suffers from TOCTOU vulnerability.

### 2.5 Headless Protocol Contract (`plans/omarchy-protocol-v1.md`)
- Line 55: Capabilities field in snapshot: `{"connect": true, "killSwitch": false, "dnsLockdown": false}`.
- Line 58: *"Capabilities reflect verified availability, not optimistic platform detection."*
- Lines 121-123: `IRouterSession` interface exposes:
  ```csharp
  bool CanConnect { get; }
  bool SupportsKillSwitch { get; }
  bool SupportsDnsLockdown { get; }
  ```
- Lines 15-16: *"One service-owned foreground helper; panel closure preserves it. EOF/SIGTERM cancels work and stops owned engine. No detached daemon or second shell."*

---

## 3. Threat Model & Security Adversary Matrix

| Adversary / Threat Vector | Description & Entry Point | Vulnerability in Legacy / Naive Design | Mitigation in Proposed Broker Architecture |
|---|---|---|---|
| **ADV-1: Arbitrary nftables Ruleset Injection** | Local malicious unprivileged process modifies ruleset configuration before root execution. | Legacy `LinuxFirewallManager` writes `vpnrouter-nft-killswitch.conf` to user-owned `~/.config/vpnrouter/` and executes `sudo -n nft -f <path>`. User can inject arbitrary firewall chains, NAT redirections, or port forwards. | **Eliminated.** Root broker does not read files from disk. Ruleset is constructed inside the root-owned helper using compiled template strings and strictly validated IP literals. |
| **ADV-2: Command Injection / Argument Tampering** | Attacker passes shell metacharacters (`;`, `\|`, `&`, `\n`) in parameters (e.g., server address, interface name). | Subprocess runners using shell invocation (`/bin/sh -c`) or unsanitized string concatenation allow command execution as root. | **Eliminated.** Broker is invoked with structured argv via `IProcessRunner` (no shell). The broker strictly validates: interface name must match `^[a-zA-Z0-9_.-]{1,16}$`; IP addresses must parse via `inet_pton` (both IPv4 and IPv6). Any invalid character causes exit 64. |
| **ADV-3: Unauthorized Privilege Escalation via Polkit** | Unprivileged remote or unauthorized local user invokes the helper directly via `pkexec` to disrupt system networking. | Overly permissive Polkit policy (`<allow_any>yes</allow_any>`) allows SSH or background sessions to reconfigure system firewall. (Historical CVE in update helper: `plans/phase-harden-linux-update-helper-2026-09-01.md`). | **Controlled.** Polkit action specifies `<allow_active>yes</allow_active>` for local interactive seat, but `<allow_inactive>auth_admin</allow_inactive>` and `<allow_any>auth_admin</allow_any>`. Helper verifies caller UID against target table suffix. |
| **ADV-4: PID Recycling & Target Hijacking on Kill** | Target `sing-box` exits, OS reassigns PID to an unrelated critical system process. Helper signals PID. | Legacy Unix kill scripts use bare PIDs (`kill -9 <pid>`). If PID wrapped, an arbitrary process is terminated. | **Prevented.** Elevated signaling uses Linux `pidfd_open` (syscall 434). The helper opens the `pidfd`, verifies `/proc/<pid>/stat` start time ticks match expected ticks, verifies executable path matches `sing-box`, and only then sends `pidfd_send_signal` (syscall 424). |
| **ADV-5: Cross-Session State Hijacking / Table Clashing** | Multiple Linux desktop users or multiple instances run VPNRouter simultaneously, interfering with each other's firewall. | Hardcoded global table name `vpnrouter_ks`. User A's disconnect deletes User B's killswitch. | **Prevented.** Helper tables are strictly scoped: `inet vpnrouter_ks_<uid>`. Helper operations require `<table-suffix>` and reject cross-UID mutations unless authorized. |
| **ADV-6: Stranded Firewall / Network Brick on Abnormal Exit** | `VPNRouter.Headless` is killed with `SIGKILL` or system crashes while killswitch is active. Host network remains dropped. | If marker file is lost or table is orphaned, user is locked out of internet with no UI recourse. | **Resilient Recovery.** 1) Helper implements `firewall-cleanup` which inspects and clears stale `vpnrouter_ks_*` tables. 2) Sentinels stored in `/run/vpnrouter/` are cleared on boot. 3) Core startup hook calls safe orphan sweep. |

---

## 4. Minimal Production Architecture: The Root-Owned Narrow Broker

### 4.1 Separation of Setup vs. Runtime Implementation
To adhere strictly to project constraints:
- **Source Implementation Authority (Approved)**:
  C# Core additive seams, interfaces, parameter validators, fake test runners, and the specification of the helper contract in this repository.
- **Admin Setup Permission (Requires Deployment Authority, Not Granted in Current Session)**:
  Installation of the helper binary to `/usr/libexec/vpnrouter/vpnrouter-root-helper`, installation of the Polkit policy to `/usr/share/polkit-1/actions/io.github.pavellizunov.vpnrouter.policy`, and applying `setcap cap_net_admin,cap_net_bind_service=+eip` to `/opt/vpnrouter/sing-box`. In production packaging (Debian package `postinst` or Arch Linux PKGBUILD), this is handled automatically during package installation.

### 4.2 Helper Executable Specification
- **Path**: `/usr/libexec/vpnrouter/vpnrouter-root-helper`
- **Ownership & Permissions**: `root:root`, mode `0755` (no setuid bit; elevation is granted via Polkit).
- **Implementation Language**: Minimal standalone C or POSIX shell with built-in parameter validation (C is strongly preferred to eliminate any shell-injection surface).

#### Supported Commands & Signatures:
1. `firewall-enable <table-suffix> <iface> <server-ips...>`
   - `table-suffix`: Alphanumeric identifier, maximum 16 characters (`^[a-zA-Z0-9_]{1,16}$`), typically the caller UID (e.g. `1000`).
   - `iface`: Network device name (`^[a-zA-Z0-9_.-]{1,16}$`, e.g. `VPNRouter-TUN`).
   - `server-ips`: Whitespace-delimited list of 1 to 64 IP address literals.
   - **Internal Execution**:
     Validates all arguments. Constructs the exact nftables ruleset in memory:
     ```nft
     add table inet vpnrouter_ks_<suffix>
     flush table inet vpnrouter_ks_<suffix>
     add chain inet vpnrouter_ks_<suffix> output { type filter hook output priority 0 ; policy drop ; }
     add rule inet vpnrouter_ks_<suffix> output oif "lo" accept
     add rule inet vpnrouter_ks_<suffix> output ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } accept
     add rule inet vpnrouter_ks_<suffix> output ip daddr { <validated-ipv4-list> } accept
     add rule inet vpnrouter_ks_<suffix> output ip6 daddr { <validated-ipv6-list> } accept
     ```
     Pipes ruleset directly into `/usr/bin/nft -f -` via standard input. Returns exit code 0 on success, non-zero on failure.
2. `firewall-disable <table-suffix>`
   - Deletes table `inet vpnrouter_ks_<suffix>` via `/usr/bin/nft delete table inet vpnrouter_ks_<suffix>`.
   - Idempotent: if table is already absent, exits 0.
3. `firewall-status <table-suffix>`
   - Executes `/usr/bin/nft -j list tables`. Parses output to determine if `vpnrouter_ks_<suffix>` exists.
   - Exits 0 if active, 1 if absent, 2 on error.
4. `dns-pin <iface> <dns-target>`
   - Validates `iface` and `dns-target` (must be valid IPv4/IPv6).
   - Executes `/usr/bin/resolvectl dns <iface> <dns-target>`.
   - Executes `/usr/bin/resolvectl domain <iface> ~.`.
   - Executes `/usr/bin/resolvectl flush-caches`.
5. `dns-revert <iface>`
   - Validates `iface`.
   - Executes `/usr/bin/resolvectl revert <iface>`.
   - Executes `/usr/bin/resolvectl flush-caches`.
6. `signal-owned <pid> <start-ticks> <exe-path> <signal>`
   - Validates `signal` is 9 (`SIGKILL`) or 15 (`SIGTERM`).
   - Opens target `pidfd` (`pidfd_open`).
   - Verifies target start ticks and executable path match `/proc/<pid>/exe`.
   - Dispatches `pidfd_send_signal`.

### 4.3 Polkit Policy Definition
File: `/usr/share/polkit-1/actions/io.github.pavellizunov.vpnrouter.policy`
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE policyconfig PUBLIC
 "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
<policyconfig>
    <vendor>VPNRouter</vendor>
    <vendor_url>https://github.com/PavelLizunov/VPNRouter</vendor_url>

    <action id="io.github.pavellizunov.vpnrouter.firewall">
        <description>Manage VPNRouter firewall kill-switch and network protection</description>
        <description xml:lang="ru">Управление аварийной блокировкой сети VPNRouter</description>
        <message>Authentication is required to configure VPN network protection.</message>
        <message xml:lang="ru">Для настройки защиты сети требуется авторизация.</message>
        <defaults>
            <allow_any>auth_admin</allow_any>
            <allow_inactive>auth_admin</allow_inactive>
            <allow_active>yes</allow_active>
        </defaults>
        <annotate key="org.freedesktop.policykit.exec.path">/usr/libexec/vpnrouter/vpnrouter-root-helper</annotate>
        <annotate key="org.freedesktop.policykit.exec.allow_gui">true</annotate>
    </action>
</policyconfig>
```

**Security Analysis of `<allow_active>yes</allow_active>`**:
Unlike `vpnrouter-update-helper` (which allowed arbitrary file copying as root), this helper allows only pre-defined, constrained networking verbs. Granting `allow_active: yes` allows the local active session (logged into the desktop) to engage and lift the killswitch without continuous password prompts during reconnects, matching the UX of NetworkManager (`org.freedesktop.NetworkManager.network-control`). Remote SSH or inactive sessions fall back to `auth_admin`.

---

## 5. Additive Core Seams & Interfaces

### 5.1 ILinuxPrivilegeBroker & LinuxPrivilegeBroker
Add to `VPNRouter.Core/Platform/Linux/ILinuxPrivilegeBroker.cs`:

```csharp
#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.Linux;

/// <summary>
/// Root-privileged operations broker for Linux network security.
/// Dispatches validated requests to the root-owned helper via pkexec or direct capability.
/// </summary>
public interface ILinuxPrivilegeBroker
{
    /// <summary>Returns true if the helper binary and elevation transport are available.</summary>
    bool IsAvailable { get; }

    /// <summary>Atomically arm and enable the nftables egress kill-switch.</summary>
    Task<bool> EnableFirewallAsync(
        string tableSuffix,
        string iface,
        IReadOnlyList<string> serverIps,
        CancellationToken ct = default);

    /// <summary>Atomically remove the nftables egress kill-switch table.</summary>
    Task<bool> DisableFirewallAsync(
        string tableSuffix,
        CancellationToken ct = default);

    /// <summary>Read back whether the dedicated nftables table is currently live.</summary>
    Task<bool> QueryFirewallActiveAsync(
        string tableSuffix,
        CancellationToken ct = default);

    /// <summary>Pin link DNS and default routing domain ~. via systemd-resolved.</summary>
    Task<bool> PinDnsAsync(
        string iface,
        string dnsTarget,
        CancellationToken ct = default);

    /// <summary>Revert link DNS settings.</summary>
    Task<bool> RevertDnsAsync(
        string iface,
        CancellationToken ct = default);

    /// <summary>Send exact-identity pidfd signal to an owned process as root.</summary>
    Task<UnixOwnedSignalResult> SignalOwnedProcessAsync(
        OwnedProcessIdentity identity,
        int signal,
        CancellationToken ct = default);
}
```

### 5.2 LinuxFirewallManager Additive Seam
Modify `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs` to support `ILinuxPrivilegeBroker`:

```csharp
// Additive constructor overload:
public LinuxFirewallManager(
    ILinuxPrivilegeBroker? broker,
    ILogger? logger = null,
    IProcessRunner? runner = null,
    string? currentConfigPath = null,
    string? markerPath = null,
    Func<string, IReadOnlyList<string>>? hostResolver = null)
{
    _broker = broker;
    _logger = logger ?? Log.Logger;
    _runner = runner ?? new ProcessRunner();
    ...
}
```
In `EnableBlockRules()`, if `_broker != null && _broker.IsAvailable`, delegate directly to `_broker.EnableFirewallAsync(...)`. If `_broker` is null, fall back to legacy `RunSudo` for backward compatibility in existing test fixtures.

### 5.3 Typed Readiness Verification
Update `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs`:

```csharp
public static bool VerifyKillSwitchSupport(
    ILinuxPrivilegeBroker? broker = null,
    Func<bool>? linuxNftChecker = null,
    ILogger? logger = null)
{
    if (OperatingSystem.IsWindows()) return true;

    if (OperatingSystem.IsLinux())
    {
        if (broker != null)
            return broker.IsAvailable;

        if (linuxNftChecker != null)
            return linuxNftChecker();

        return ProbeLinuxNftWithoutPassword(logger);
    }

    return false;
}

public static bool VerifyDnsLockdownSupport(
    ILinuxPrivilegeBroker? broker = null,
    ILogger? logger = null)
{
    if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        return true;

    if (OperatingSystem.IsLinux())
    {
        // Truthful verification: supported if broker is available or resolvectl is verified.
        return broker?.IsAvailable == true || ProbeLinuxResolvectlAvailable(logger);
    }

    return false;
}
```

### 5.4 Cross-Process Mutex: UnixFileMutex
Add `VPNRouter.Core/Services/UnixFileMutex.cs` and wire it into `TunOwnershipLock.cs`:

On Linux and macOS, instead of named semaphores with `Global\`, acquire an exclusive non-blocking advisory lock using `FileStream.Lock` / `flock` on:
`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/vpnrouter/tun-owner.lock")` (or `/run/user/<uid>/vpnrouter.lock`).

```csharp
public sealed class UnixFileMutex : IDisposable
{
    private readonly string _lockFilePath;
    private FileStream? _lockStream;

    public UnixFileMutex(string lockFilePath) => _lockFilePath = lockFilePath;

    public bool TryAcquire()
    {
        try
        {
            var dir = Path.GetDirectoryName(_lockFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _lockStream = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _lockStream.Lock(0, 0); // POSIX fcntl / flock exclusive lock
            return true;
        }
        catch (IOException)
        {
            _lockStream?.Dispose();
            _lockStream = null;
            return false; // Held by another process
        }
        catch (Exception)
        {
            _lockStream?.Dispose();
            _lockStream = null;
            return false;
        }
    }

    public void Release()
    {
        try { _lockStream?.Unlock(0, 0); } catch { }
        _lockStream?.Dispose();
        _lockStream = null;
    }
}
```
When the process crashes or terminates, the Linux kernel automatically closes open file descriptors and releases POSIX file locks, guaranteeing crash-clean mutual exclusion without stale lock deadlocks.

### 5.5 Headless Program Seams for Stop Helper
In `VPNRouter.Headless/Program.cs`, add the entry-point interceptor before server execution:
```csharp
if (UnixOwnedProcessSignal.TryHandleHelper(args, out var helperExitCode))
    return helperExitCode;
```
In `VPNRouter.Core/Services/SingBoxManager.LinuxStop.cs:218`:
Update `ResolveSignalHelperHost()`:
```csharp
return name is "VPNRouter.App" or "VPNRouter.CLI" or "VPNRouter.Headless" ? path : null;
```

---

## 6. Privilege State Readback & Cleanup Rules

### 6.1 State Readback Invariants
1. **Never Assume State**: Neither Core nor Headless may assume a firewall table is active simply because `EnableBlockRules()` was called. State must be verified via `QueryFirewallActiveAsync()` or JSON table listing.
2. **Readback Discrepancy Reconciliation**:
   If Core internal state is `_loaded == true` but kernel query indicates the table is missing:
   - Mark `_loaded = false`.
   - Log critical security warning: `[LinuxFirewall] Kernel table vpnrouter_ks disappeared unexpectedly`.
   - Trigger health degradation event (`ConnectionHealthClassifier`).

### 6.2 Cleanup Protocols & Safe Teardown
1. **Orderly Disconnect**:
   1. Stop `sing-box` (unprivileged `Kill()` if capability mode; elevated `pidfd` signal if pkexec).
   2. Remove nftables table (`firewall-disable`).
   3. Revert DNS link settings (`dns-revert`).
   4. Delete sentinel markers.
   5. Release `UnixFileMutex`.
2. **Orphan Cleanup on Next Launch**:
   - `LinuxFirewallManager.TryCleanupOrphanedRulesSafe()` checks for marker file `nft-killswitch-engaged.marker`.
   - If marker exists, invoke `firewall-disable`.
   - Re-check table inventory. If confirmed deleted or absent, remove marker file.
   - Run `LinuxDnsHardening.RestoreStrandedIfAny()` to revert stranded `resolvectl` link rules.

---

## 7. Verification Test Specifications

The proposed seams must be validated with comprehensive unit and integration tests using `FakeProcessRunner`:

### 7.1 LinuxPrivilegeBrokerWireShapeTests
- **Test 1**: `EnableFirewall_Dispatches_Pkexec_With_Strict_Args`
  - Assert executable is `/usr/bin/pkexec`.
  - Assert arguments match: `["/usr/libexec/vpnrouter/vpnrouter-root-helper", "firewall-enable", "1000", "VPNRouter-TUN", "1.2.3.4"]`.
  - Assert no shell invocation or quotes.
- **Test 2**: `EnableFirewall_Rejects_Invalid_Interface_Or_IP`
  - Pass interface `"; rm -rf /"`; assert throws `ArgumentException` before invoking runner.
  - Pass invalid IP `"1.2.3.4.5"`; assert throws `ArgumentException`.
- **Test 3**: `DisableFirewall_Invokes_Disable_Verb`
  - Assert arguments match: `["/usr/libexec/vpnrouter/vpnrouter-root-helper", "firewall-disable", "1000"]`.

### 7.2 PlatformCapabilityVerifierTests
- **Test 4**: `VerifyKillSwitchSupport_WithBrokerAvailable_ReturnsTrue`
  - Inject fake broker with `IsAvailable = true`.
  - Assert `VerifyKillSwitchSupport` returns `true`.
- **Test 5**: `VerifyDnsLockdownSupport_WithBrokerAvailable_ReturnsTrue`
  - Inject fake broker with `IsAvailable = true`.
  - Assert `VerifyDnsLockdownSupport` returns `true`.

### 7.3 UnixFileMutexTests
- **Test 6**: `UnixFileMutex_Provides_Mutual_Exclusion`
  - Instantiate `UnixFileMutex` A and B on the same temp lockfile.
  - Assert `A.TryAcquire()` returns `true`.
  - Assert `B.TryAcquire()` returns `false` (held by another).
  - Call `A.Release()`.
  - Assert `B.TryAcquire()` now returns `true`.

### 7.4 HeadlessStopHelperHostTests
- **Test 7**: `ResolveSignalHelperHost_Recognizes_VPNRouter_Headless`
  - Update `VPNRouter.Tests/UnixStopSourceGuardTests.cs` to assert:
    `Assert.Contains("name is \"VPNRouter.App\" or \"VPNRouter.CLI\" or \"VPNRouter.Headless\"", stopSource)`.

---

## 8. Summary & Recommendation for Coordinator

1. **Adopt Capability-First Execution for `sing-box`**:
   The existing `sing-box` capability launch path in `SingBoxManager.Lifecycle.cs` is already least-privilege and production-ready. No root helper is needed for normal VPN routing when `setcap cap_net_admin,cap_net_bind_service=+eip` is applied at package install time.
2. **Adopt Root Broker for Firewall & DNS Privileges**:
   Eliminate `sudo -n nft` completely. Introduce `/usr/libexec/vpnrouter/vpnrouter-root-helper` with Polkit policy `io.github.pavellizunov.vpnrouter.policy` allowing `allow_active: yes`.
3. **Bridge Headless in Exact Stop Chain**:
   Add `VPNRouter.Headless` to `ResolveSignalHelperHost()` in `SingBoxManager.LinuxStop.cs` and wire `UnixOwnedProcessSignal.TryHandleHelper` in `Program.cs`.
4. **Fix Cross-Process Lock with POSIX `flock`**:
   Implement `UnixFileMutex` to replace the Windows-only `Global\` named semaphore, closing defect `OMARCHY-OWNERSHIP`.
5. **Update `PlatformCapabilityVerifier`**:
   Reflect truthful broker availability so full Linux feature parity (killswitch and DNS lockdown) is activated cleanly.
