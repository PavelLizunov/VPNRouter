# Omarchy Privilege Architecture Decision Input

**Target:** Coordinator Decision for Linux Privilege Broker & Native Parity  
**Status:** Read-Only Architectural Mapping (Implementation Deferred)  
**Constraint:** Max 150 lines, no secrets, source/test authorization only.

## Coordinator disposition (2026-09-18)

This is advisory input, NOT an approved design or installation instruction.
The recommendations below have not been accepted wholesale:
- Reject `allow_active=yes`: local-seat presence is not explicit admin consent.
- `pkexec` does not validate arguments. Any retained/implicit authorization
  requires a separately reviewed constrained API; fixed executable path alone
  is insufficient. See [official pkexec security notes](https://www.freedesktop.org/software/polkit/docs/latest/pkexec.1.html).
- Per-UID nft table names do not isolate global egress effects between users.
- The Linux flock implementation already exists; the replacement recommendation
  below is stale. Production lock path is `/run/user/<uid>/vpnrouter-tun.lock`.
- Do not elevate the plugin-relative Headless executable to reuse stop handling.
  A privileged executable and its dependencies must reside in trusted storage.
- Wire compatibility does NOT establish that a new privileged service is an
  immaterial change. Installation, authorization lifetime, cleanup on owner loss,
  cross-UID coordination and denied authorization remain design/acceptance gates.

Research verdict: Extend Core with existing Polkit and systemd-resolved
mechanisms; no privilege policy or broker has been installed or implemented.

---

## 1. Executive Recommendation: Reuse vs. New Broker

- **Verdict: Build a Dedicated Narrow Root Broker; Reuse Existing Stop & Mutex Seams.**
- **Skeptical Reuse of `vpnrouter-update-helper` (Rejected):**
  - Current helper (`packaging/linux/vpnrouter-update-helper`) is bound to Polkit action `com.vpnrouter.update` with `<allow_active>auth_admin</allow_active>` (requires password on every call).
  - Mixing high-frequency network filtering with software file installation (`cp -rfT`) violates least privilege.
  - The script is Bash; embedding dynamic firewall rules and IP parsing in Bash introduces shell parsing and injection risks.
- **Reuse of `UnixOwnedProcessSignal` (Adopted):**
  - The exact pidfd signal mechanism (`--vpnrouter-internal-signal-owned-v1`) in `UnixOwnedProcessSignal.cs` is secure and fully reusable.
- **New Broker Specification (`vpnrouter-root-helper`):**
  - Minimal root-owned binary at `/usr/libexec/vpnrouter/vpnrouter-root-helper` (mode `0755`, owned by `root:root`).
  - Governed by Polkit action `io.github.pavellizunov.vpnrouter.policy` with `<allow_active>yes</allow_active>` for local interactive seats.
  - Constrained verbs only: `firewall-enable <uid> <iface> <ips...>`, `firewall-disable <uid>`, `firewall-status <uid>`, `dns-pin <iface> <ip>`, `dns-revert <iface>`, `signal-owned <pid> <ticks> <exe> <sig>`.
  - Rejects arbitrary ruleset files, shell wrappers, and unvalidated parameters.

---

## 2. Capabilities Clarification: Cap `sing-box` != Firewall/DNS Support

- `sing-box` file capabilities (`cap_net_admin,cap_net_bind_service=+eip`) grant TUN creation and low-port binding to the `sing-box` executable only.
- They grant **zero** authority to `VPNRouter.Headless` or `VPNRouter.App` to:
  1. Modify host `nftables` tables (requires `CAP_NET_ADMIN` in initial network namespace).
  2. Configure system DNS via `systemd-resolved` (requires D-Bus/Polkit authority for `org.freedesktop.resolve1`).
  3. Stop a root-spawned `sing-box` process (when fallback pkexec launch was used).
- Treating `setcap sing-box` as proof of firewall or DNS readiness is a false premise.

---

## 3. Root-Owned Code Install vs. Live Enabling

- **Root-Owned Code Install (Packaging / Provisioning Phase):**
  - Performed at package installation (`.deb` / RPM / Arch `postinst`) or admin setup.
  - Installs `/usr/libexec/vpnrouter/vpnrouter-root-helper` (`root:root`, `0755`) and Polkit policy `/usr/share/polkit-1/actions/`.
  - Applies `setcap` to `/opt/vpnrouter/sing-box`.
  - Ensures no unprivileged user can modify the elevated helper binary (eliminating CWE-829 / local privilege escalation).
- **Live Enabling (Runtime Authorization Phase):**
  - Unprivileged user daemon (`VPNRouter.Headless`) invokes the helper via `pkexec /usr/libexec/vpnrouter/vpnrouter-root-helper <verb>`.
  - Polkit evaluates `allow_active: yes`; interactive seat executes without prompt; headless/remote session falls back to admin prompt.
- **User Authorization Boundary:**
  - In this workspace, user authorizes source-level implementation and isolated unit tests only.
  - No live root provisioning, package installation, or network route/firewall mutation is authorized.

---

## 4. Global Host NFT (Full-Only) vs. Per-UID Ownership

- **Core Invariant (Full-Tunnel Kill-Switch):**
  - `LinuxFirewallManager` is strictly full-tunnel only (`isFullTunnel == true`). When armed, it applies a global egress block (dropping all host outbound except loopback, RFC1918 LAN, and active VPN server IPs).
- **Per-UID Table Ownership:**
  - Naming tables globally as `inet vpnrouter_ks` causes cross-user clashing and collision during multi-session or crash recovery.
  - Scoping tables to `inet vpnrouter_ks_<uid>` ensures:
    1. The helper verifies caller UID against the target table name.
    2. User A cannot disarm or corrupt User B's firewall table.
    3. Cleanup and orphan sweeps are isolated per user.

---

## 5. Exact Existing Code Seams to Extend

1. **`VPNRouter.Core/Platform/PlatformServices.cs`**:
   - Additive seam: Allow injecting `ILinuxPrivilegeBroker` into `CreateFirewallFactory` and `CreateUnixDnsHardening`.
2. **`VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`**:
   - Add constructor accepting `ILinuxPrivilegeBroker?`.
   - When broker is available, delegate `Enable`/`Disable`/`Query` to broker instead of `RunSudo("nft -f ...")`.
   - Remove dependency on user-writable config file `vpnrouter-nft-killswitch.conf`.
3. **`VPNRouter.Core/Platform/Linux/LinuxDnsHardening.cs`**:
   - Add broker seam for `resolvectl` dispatch when local D-Bus call requires elevation.
4. **`VPNRouter.Core/Services/SingBoxManager.LinuxStop.cs` (Line 218)**:
   - Add `"VPNRouter.Headless"` to `ResolveSignalHelperHost()`:
     `return name is "VPNRouter.App" or "VPNRouter.CLI" or "VPNRouter.Headless" ? path : null;`
5. **`VPNRouter.Headless/Program.cs`**:
   - Wire `UnixOwnedProcessSignal.TryHandleHelper(args, out var code)` before starting the stdio server.
6. **`VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs`**:
   - Wire broker probe to `VerifyKillSwitchSupport` and `VerifyDnsLockdownSupport` (currently hardcoded `false` on Linux).
7. **`VPNRouter.Core/Services/TunOwnershipLock.cs`**:
   - Replace broken Windows `Global\VPNRouter-SingBox-Owner` named semaphore on Linux with POSIX advisory file lock (`flock` on `/run/user/<uid>/vpnrouter.lock`).

---

## 6. Protocol Specification Impact

- **Does this require a material spec amendment to `plans/omarchy-protocol-v1.md`?**
  - **NO.**
  - `omarchy-protocol-v1.md` already defines the capabilities contract (`killSwitch`, `dnsLockdown`), command schemas (`connect`, `disconnect`), and status events.
  - The privilege broker is an internal Core platform adapter. Wire protocol format, NDJSON framing, and method contracts remain completely unchanged.
  - Only deployment/packaging documentation and unit test contracts require updates.

---

## 7. Security Boundaries & Invariants Summary

| Security Boundary | Current Defect / Vulnerability | Mitigated State via Narrow Broker |
|---|---|---|
| **Arbitrary Ruleset Exec** | `sudo -n nft -f ~/.config/vpnrouter/...` (user-writable) | Helper generates rules in-memory from validated IPs. |
| **Sudoers NOPASSWD** | Requires broad `NOPASSWD: /usr/bin/nft` | Eliminated; all elevation mediated by Polkit. |
| **Privilege Escalation** | `pkexec <user-path>` executes unvetted binary | Polkit locks execution path to `/usr/libexec/...`. |
| **PID Reuse on Kill** | Shell kill / broad pattern kill | Syscall `pidfd_open` + exact start ticks verification. |
| **Cross-Process Mutex** | Named semaphore throws and fails open | Kernel-managed POSIX `flock` auto-releases on crash. |
| **Table Hijacking** | Shared global table `inet vpnrouter_ks` | Per-UID table `inet vpnrouter_ks_<uid>` with UID check. |

---

## 8. Remaining Authorizations Required (Deferred to Coordinator)

1. **Broker Implementation Language**: Approve C vs. standalone hardened POSIX binary for `/usr/libexec/vpnrouter/vpnrouter-root-helper`.
2. **Packaging Setup**: Authorize adding helper and Polkit XML to Debian/Arch packaging pipelines.
3. **Live Test Authorization**: Authorize execution on Linux test worker when integration phase begins.
