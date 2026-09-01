# Test workers and resource contract

This is the repository source of truth for VPNRouter worker roles. Homelab documentation remains authoritative for credentials, network topology, hypervisor details, and current connectivity. Always use the trusted aliases below rather than copying volatile LAN endpoints into plans or commands.

## Control plane

### `harness-test`

- Control plane only: coordinates DSH sessions, subagents, and remote worker jobs.
- Never install platform SDKs here to compensate for a missing worker dependency.
- Never run VPNRouter, platform packaging, heavy builds, or mutable VPN/UI scenarios here.
- Lightweight repository inspection and orchestration checks are allowed.

## Verified worker observations

Observed on 2026-08-30. Capacity and installed-tool facts are point-in-time observations, not readiness guarantees; repeat preflight immediately before work.

| Alias | Identity and role | Observed capacity | Observed tools |
|---|---|---|---|
| `windows-worker` | `WINBRAT`, Proxmox VM 100; fixed Windows UI/dataplane verification target | Windows 10 Enterprise LTSC 10.0.17763; 8 logical CPUs; 16 GiB RAM; about 20 GiB free | Git 2.55.0; .NET absent |
| `linux-worker` | Debian build/test worker | Debian 12; 4 CPUs; about 4 GiB RAM; about 10 GiB free | Git 2.39.5; .NET absent |
| `mac-worker` | macOS build/test worker | macOS 26.5.2; 10 CPUs; 16 GiB RAM; about 8 GiB free | Xcode 26.6; .NET and adb absent |

The fixed post-ship verifier may pin WINBRAT's canonical address and MachineName as a fail-closed identity check. Other worker access details come from the homelab runtime, not this repository.

## Mandatory preflight

Before a build, deployment, package operation, or test scenario:

1. Resolve the requested trusted alias through the homelab tooling.
2. Confirm the exact repository commit SHA to be tested. Never use a shared mutable source tree or an unspecified branch tip.
3. Inspect CPU load, available RAM, free disk, required SDK/tool availability, and conflicting jobs using read-only commands.
4. Treat Pulse/Beszel and hypervisor metrics as read-only observations. Do not add host memory and guest memory as though they were separate capacity.
5. Stop before heavy work when the required SDK is absent or free resources are insufficient; report the concrete blocker instead of provisioning or cleaning automatically.

## Execution and mutation limits

- Allow only one mutable VPN/UI/deployment scenario on a worker at a time.
- Do not change VM CPU, RAM, disk allocation, lifecycle, networking, monitoring, firewall topology, or DSH services.
- Do not install SDKs, workloads, package managers, or persistent services unless the owner explicitly authorizes that infrastructure change.
- Do not use the development workstation or `harness-test` as a fallback for WINBRAT verification.
- All install, launch, connect, stop, UIA, and live-log operations for shipped Windows builds go through `tools/brat-verify.ps1`; identity mismatch is a hard stop.

## Cleanup rules

- Always remove artifacts created by the current deployment/test scenario from the exact task-owned paths, including temporary install folders and transient test payloads, on both PASS and FAIL paths.
- Never run broad cache, TEMP, Docker, Cargo, NuGet, package-manager, or system cleanup automatically.
- When capacity is low, first report cleanup candidates with path/category, size, and age. Clean only after explicit owner permission or after a confirmed blocker approval that names the exact targets.
- Never delete credentials, shared caches, unrelated worktrees, logs outside the scenario window, or another job's artifacts.

## Scheduling guidance

- `windows-worker`: reserve for Windows-only UI, installer, updater, VPN, firewall, and dataplane verification.
- `linux-worker`: use only after confirming the required SDK/toolchain and sufficient disk/RAM; keep parallelism conservative on the 4 GiB node.
- `mac-worker`: use only after a fresh disk check; its observed free space is constrained, so do not start heavy builds until dependencies and output headroom are proven.
