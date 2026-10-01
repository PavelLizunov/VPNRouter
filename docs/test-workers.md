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

Observed on 2026-09-29 (historical): `windows-worker` has 16 GiB RAM and about 17 GiB free on `C:` with a private Android/.NET toolchain in `C:\android-build\`. Its Proxmox host runs with `nested_amd=1` and VM 100 uses `cpu: host`, so AMD-V is visible in the guest, but the Windows Hypervisor Platform feature is disabled: hardware-accelerated Android emulators would need that feature (a reboot) and more disk, which requires owner authorization.

The fixed post-ship verifier may pin WINBRAT's canonical address and MachineName as a fail-closed identity check. Other worker access details come from the homelab runtime, not this repository.

## Mandatory preflight

Before a build, deployment, package operation, or test scenario:

1. Resolve the requested trusted alias through the homelab tooling.
2. Confirm the exact repository commit SHA to be tested. Never use a shared mutable source tree or an unspecified branch tip.
3. Inspect load and capacity with read-only commands and record the numbers before any compile or test:
   - CPU: load average against the logical CPU count, and whether a `dotnet`, `msbuild`, `java` or `VBCSCompiler` process is already running.
   - Memory: available RAM and swap in use. The 4 GiB `linux-worker` takes one build at a time and no parallel test runs.
   - Disk: free space on the build volume, with headroom for outputs and NuGet/Android caches.
   - Required SDK/tool availability and conflicting jobs.
   Start only when the numbers show clear headroom; otherwise stop and report them.
4. Treat Pulse/Beszel and hypervisor metrics as read-only observations. Do not add host memory and guest memory as though they were separate capacity.
5. Stop before heavy work when the required SDK is absent or free resources are insufficient; report the concrete blocker instead of provisioning or cleaning automatically.

## Execution and mutation limits

- Allow only one mutable VPN/UI/deployment scenario on a worker at a time.
- Do not change VM CPU, RAM, disk allocation, lifecycle, networking, monitoring, firewall topology, or DSH services.
- Do not install SDKs, workloads, package managers, or persistent services unless the owner explicitly authorizes that infrastructure change.
- Do not use the development workstation or `harness-test` as a fallback for WINBRAT verification.
- Do not install SDKs or run builds, packaging or full test runs on the development workstation as a substitute for a worker. A local "does it compile" check counts. If no authorized worker fits, report the blocker or use GitHub CI.
- All install, launch, connect, stop, UIA, and live-log operations for shipped Windows builds go through `tools/brat-verify.ps1`; identity mismatch is a hard stop.

## Cleanup rules

- Always remove artifacts created by the current deployment/test scenario from the exact task-owned paths, including temporary install folders and transient test payloads, on both PASS and FAIL paths.
- Never run broad cache, TEMP, Docker, Cargo, NuGet, package-manager, or system cleanup automatically.
- When capacity is low, first report cleanup candidates with path/category, size, and age. Clean only after explicit owner permission or after a confirmed blocker approval that names the exact targets.
- Never delete credentials, shared caches, unrelated worktrees, logs outside the scenario window, or another job's artifacts.

## Scheduling guidance

- `windows-worker`: main worker for Windows verification and, by owner decision on 2026-09-29, for Android builds. Android builds use the private toolchain in `C:\android-build\` (own .NET, JDK 17, Android SDK 36; the shared `C:\dotnet` is not touched), run unsigned on an exact SHA, one job at a time and never during a WINBRAT live scenario. Remove the per-run source checkout afterwards.
  Windows release staging (r11 onward) also builds the Inno Setup installer there: a portable Inno Setup 6 sits in `C:\android-build\tools\innosetup` (not installed system-wide) and `tools/build-installer.ps1` finds it; `build.ps1 -Upload` stops if it is missing. Release builds there run as `C:\android-build\relbuild2.ps1 -Sha <sha> -Version <v> -Name <label>` (private Go 1.21.13, `-BundleSplitDriver -Installer`, output in `C:\android-build\out-<label>`) and are started through `Invoke-CimMethod Win32_Process Create`, because a process started from an ssh session dies with it.
- `linux-worker`: use only after confirming the required SDK/toolchain and sufficient disk/RAM; keep parallelism conservative on the 4 GiB node.
- `mac-worker`: use only after a fresh disk check; its observed free space is constrained, so do not start heavy builds until dependencies and output headroom are proven.
