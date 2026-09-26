# Phase: Omarchy runtime staging package

Approval: direct owner `approve`, 2026-09-19 23:40 Europe/Moscow, for the
Micro-Spec below. Continuation on `dsh/omarchy-plugin-2026-09-17`, accepted
base `05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a`, draft PR #296.
Why: native QML integration lacks a reviewable system Headless/runtime package.
Risk: HIGH for archive/file supply-chain boundaries; no runtime authority added.
Rollback: revert task-only packaging commits; no installed state to revert.

## 1. Intent & Invariants

- Add Arch/Omarchy x86-64 package sources for Headless and sing-box, using
  `/usr/lib/vpnrouter-headless/` separately from user-owned QML plugin files.
- Build backend from exact Git commit; select pinned runtime archives, no latest.
- Account for bundled libraries, licenses and hashes.
- Preserve Windows/macOS/Android packaging and existing Debian package.
- No package installation, setcap, Polkit, sudoers, service activation, root
  helper, shell restart, VPN/TUN/firewall/DNS mutation, release or merge.
- Do not enable production runtime: artifact delivery and network authorization
  remain separate gates. Preserve DefaultProduction unavailable.

## 2. Interface / Data Contract

- Versioned manifest: source commit, architecture, runtime version, source and
  pinned archive SHA-256, payload file inventory and per-file hashes.
- Verify archives before extraction; reject path escapes, unexpected links,
  special files and wrong architecture; bound archive size/extraction work.
- Build only into an isolated staging directory. No user config or secrets.
- Manifest describes payload; it does not grant execution authority or become
  trusted merely by being adjacent to an executable.
- Document future helper boundary (user authorization, constrained configuration,
  network ownership, failure recovery). No helper implementation in this block.

## 3. Verification Checklist

- [ ] Non-root package build with makepkg on approved omarchy-test, no install.
- [ ] Automated package inventory/path/mode/hash checks.
- [ ] Negative tests: tamper, wrong architecture, unsafe paths and links.
- [ ] No install-hooks granting privilege or activating services.
- [ ] Avalonia-free Headless payload; runtime-policy and protocol regressions.
- [ ] Independent review, task commit, immediate push, exact-SHA CI verifier.
- [ ] Report separates prepared artifact from authorized/verified VPN operation.

## Implementation and six gates

Use existing .NET10 publish, pinned sing-box-vpnctl Linux archive, Arch makepkg
and Python standard-library validation rather than a package-management service.
New files under packaging/arch plus scoped CI and tests/documentation as needed.
Do not alter shipped Core behavior or accept a manifest as production authority.
Worker preflight: makepkg, fakeroot, bsdtar, Python and readelf present; resources
available. Exact source and packaging snapshots recorded with every actual check.

1. Build: PASS fresh Headless publish and non-root Arch package on omarchy-test.
2. Tests: PASS 80 packaging tests, 26 Core runtime-policy tests and 37 Headless
   groups (including 41 lifecycle and 12 runtime subchecks).
3. Docs: PASS package README, helper boundary and exact-snapshot evidence report.
4. Review: PASS scoped independent correctness/security/test review; confirmed
   blockers fixed. Minor tar EOF divergence recorded in OPEN-DEFECTS.
5. UI/live: N/A, source/staging only; live installation explicitly excluded.
6. Integration: PASS mandatory artifact inspection, modes/ownership/manifest/hash;
   final exact-SHA CI receipt follows push in PR #296.

Outcome: final packaging snapshot d4478ecb7fa3be185a77d3c7a1f4e2a4c2869d6b built
and inspected successfully. See omarchy-runtime-package-verification-2026-09-20.md.
Package files remain on the test worker, not published. LICENSE inventory is
explicitly incomplete (Serilog/YamlDotNet/Chromium notices), a blocking gate for
binary distribution rather than an assertion that runtime supply is finished.
Production runtime remains unavailable; approval did not authorize privileged
execution, installation or deployment. Rollback is task-only source revert.
Unrelated dirty worktree content preserved.
