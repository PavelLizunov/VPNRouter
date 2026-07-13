# Phase 0 — Xiaomi R4ACV2 OpenWrt manual kit

**Owner**: Codex session 2026-07-13
**Branch**: `codex/xiaomi-r4acv2-openwrt-kit`
**Roadmap ref**: N/A — operator tooling outside the v3.0 application roadmap
**Effort**: 2 hours
**Risk**: HIGH — the kit contains an explicitly gated flash command
**Blast radius**: new files under `tools/xiaomi-r4acv2-openwrt/` only; no VPNRouter runtime impact
**Rollback**: delete the branch or revert its commits

## Why

The owner needs a manual, reproducible way to install OpenWrt on a Xiaomi Mi
Router 4A 100M International V2 running stock firmware 3.0.129. The process
must stop on a wrong flash layout, preserve a full device backup, verify every
download, prepare recovery before writing, and leave Wi-Fi out of the final
OpenWrt image.

## What

- Add one Russian runbook covering physical preparation, shell acquisition,
  recovery, backup, first boot, and the later Wi-Fi-free sysupgrade.
- Add one POSIX/BusyBox router script for preflight, named-partition backup,
  image verification, gated OS1 flashing, postcheck, and a self-test.
- Add one Python standard-library PC tool for pinned downloads, exact-length
  backup receipt, atomic writes, hashes, metadata, and a self-test.
- Do not automate OpenWRTInvasion, Xiaomi recovery execution, firewall changes,
  router mode changes, or the flash confirmation.

## How

1. Pin the official OpenWrt 25.12.5 image and the official Xiaomi recovery
   artifacts by URL, size, and SHA-256.
2. Validate the live `/proc/mtd` by partition names and sizes; never use a
   fixed `mtdN` number.
3. Stream read-only backups to the PC and require the full-backup hash during
   the flash confirmation.
4. Permit only `mtd -r write /tmp/fw.bin OS1`, after an exact typed phrase.
5. Document the initial official install separately from the later custom
   Wi-Fi-free `sysupgrade`.

### Tests written

- `router-safe.sh self-test` — accepts the R4ACV2 layout and rejects a changed
  OS1 size without touching `/dev/mtd*`.
- `pc-tool.py self-test` — accepts exact data and rejects truncated or
  oversized streams using local socket pairs.

### Verification approach

Run shell syntax checking, both self-tests, scan the diff for forbidden flash
targets and erase commands, then run the repository build and regression suite.
No router write or application UI action is part of this task.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` has 0 errors.
- [ ] **Gate 2 — Tests green**: project regressions and both kit self-tests pass.
- [ ] **Gate 3 — Docs**: this Outcome and the operator runbook are complete.
- [ ] **Gate 4 — Self-review**: Ponytail review plus manual security review pass.
- [ ] **Gate 5 — MCP verify**: N/A — no application UI change.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split.

## Outcome (filled after implementation)

**Status**: PENDING
**Commits**: pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results:**
- [ ] Gate 1: pending
- [ ] Gate 2: pending
- [ ] Gate 3: pending
- [ ] Gate 4: pending
- [-] Gate 5: N/A — no application UI change
- [-] Gate 6: N/A — not a god-file split

**Surprises encountered**:
- Stock 3.0.129 does not expose the usual OpenWRTInvasion endpoint reliably;
  shell acquisition remains a separate manual stop gate.
- Xiaomi publishes an R4ACV2 2.20.21 recovery image, but not the current global
  3.0.129 image on its public download page.

**Follow-ups spawned**:
- After physical installation, configure and observe the router over SSH before
  deciding whether the existing VPN disconnects were caused by stock firmware.

**Lessons for methodology doc**: none.
