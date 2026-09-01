# Control-plane to worker build/test workflow

**Status:** active

**Worker source of truth:** [`docs/test-workers.md`](../docs/test-workers.md)

This workflow supersedes machine-specific LAN instructions for repository agents. `README-VM.md` remains a standalone guide for contributors who create their own isolated VirtualBox guest; it does not define the managed worker fleet.

## Roles

- `harness-test`: DSH control plane only. It coordinates repository work and remote jobs; it is not a platform build or VPN test machine.
- `windows-worker` / `WINBRAT`: fixed Windows install, updater, UIA, VPN, firewall, and dataplane verifier.
- `linux-worker`: Linux build/test target when preflight proves the required toolchain and headroom.
- `mac-worker`: macOS build/test target when preflight proves the required toolchain and disk headroom.

Use trusted aliases through homelab tooling. Do not copy volatile LAN addresses, credentials, or dated access commands into task plans.

## Required sequence

1. **Choose an exact source revision.** Build and test a committed exact SHA, never an unspecified working tree or mutable branch tip.
2. **Run read-only preflight.** Check target identity, active jobs, CPU, available RAM, free disk, and required SDK/tool versions. Follow `docs/test-workers.md` for resource limits.
3. **Stop on missing prerequisites.** Do not install SDKs on `harness-test`, repurpose the development workstation, resize VMs, or clean shared caches to make a worker appear ready.
4. **Prepare an isolated payload.** Use an exact-SHA checkout or immutable release artifact. Do not share a mutable source directory between workers.
5. **Run one mutable scenario per worker.** Queue other deployments, VPN/UI runs, or packaging jobs.
6. **Collect sanitized evidence.** Keep secrets and raw live configuration/log content on the trusted worker. Report test outcomes, semantic state, and sanitized classifications.
7. **Clean task-owned artifacts.** Remove only the current scenario's deployment/test payloads in PASS and FAIL paths. Never run broad cache, TEMP, Docker, Cargo, NuGet, or system cleanup without explicit approval.

## Windows deployment and verification

All VPNRouter install, launch, UIA, connection, and live-log scenarios target only the fixed WINBRAT identity through `tools/brat-verify.ps1` or its canonical coordinator. Do not use `deploy-to-testpc.ps1` as an alternate worker/developer-machine path.

Shipped candidate/stable verification is fail-closed and must use:

```powershell
powershell -ExecutionPolicy Bypass -File tools/post-ship-verify.ps1 `
  -Version X.Y.Z-rN -Cycles 2
```

That coordinator delegates mutable Windows actions through `tools/brat-verify.ps1`, which pins the fixed WINBRAT identity. There is no local-machine fallback.

## Worker observations

The installed SDK and free-space figures in `docs/test-workers.md` are dated observations. Re-check immediately before every heavy run. In particular:

- an absent .NET SDK means the worker is not currently a generic .NET build node;
- constrained free disk on `mac-worker` is a hard preflight concern;
- `linux-worker` should use concurrency justified by its fresh RAM/load preflight;
- Pulse/Beszel and hypervisor values are read-only observations, and host plus guest memory must not be double-counted.

## Blocker and cleanup report

When a run cannot proceed, report:

- worker alias and exact commit SHA;
- missing tool or measured CPU/RAM/disk condition;
- conflicting mutable job, if any;
- cleanup candidates with exact path/category, size, and age;
- the smallest owner decision needed.

Do not perform the cleanup or infrastructure mutation until the owner approves the named targets.
