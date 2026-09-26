# Omarchy license evidence verification — 2026-09-20

## Scope and verdict

Approved brief: `phase-omarchy-license-bundle-2026-09-20.md`.
Baseline: `3b24e131dde8587e409a58e6faf0ac5cb21eac28` (brief; five checks and
canonical exact-SHA verifier passed). Implementation extends Arch staging only.
Verdict: scoped implementation verified; **distribution licensing remains incomplete**.
No runtime versions, production runtime-policy, privileges or installation changed.

## Immutable build and consumer evidence

- Successful exported Git snapshot: `aaa3965b66d97ee67d818b6bc0f9de13a52dc421`.
- Backend source: `05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a`.
- Worker: approved `omarchy-test`, non-root tester, identity checked before mutation.
- Preflight: approximately 23 GiB free disk / 5.5 GiB available RAM; SDK10.0.301.
- Command: `PYTHONDONTWRITEBYTECODE=1 python3 packaging/arch/build_package.py --repo <exact-object-repo> --work-dir <new-private-directory> --archive-cache <approved-read-only-cache>`.
- Package: `vpnrouter-headless-0.1.0-1-x86_64.pkg.tar.zst`.
- Package SHA-256: `fa435081ed94d611cdf8423ff6e86454f663b3e4f8d46b69be2651398556b51a`.
- Retained privately under `/home/tester/vpnrouter-omarchy-checks/aaa3965b66d97ee67d818b6bc0f9de13a52dc421/build/pkgbuild/`; no binary uploaded or installed.
- Fresh publish/makepkg and mandatory final archive inspector: exit0.
- Independent re-invocation of `build_package.inspect_package`: PASS.
- Git snapshot checked against all51 catalog notice SHA-256 values before export.
- Final inventory70files; notice catalog51texts/5third-party components.
- Actual manifest and component inventory both remain incomplete; `execution_authorized=false`.
- Compared byte-for-byte to prior package snapshot d4478ecb: `runtime/sing-box`,
  `runtime/libcronet.so`, apphost `VPNRouter.Headless`, `Serilog.dll`, `YamlDotNet.dll`
  unchanged. Core and Headless source changes: none.

## Actual component results

| Component | Version | Notice files | Evidence state |
|---|---|---:|---|
| Serilog | 4.4.0 | 1 | verified_texts: exact nuspec, runtime asset and pinned source |
| YamlDotNet | 18.1.0 | 2 | verified_texts, includes LICENSE-libyaml |
| Microsoft.NETCore.App.Host.linux-x64 | 10.0.9 | 2 | verified_texts and exact customized template byte comparison |
| Cronet (donor release label) | 1.13.14 | 45 | unresolved, candidate source-tree superset, not linked credits |
| sing-box-vpnctl | 1.14.0-vpnctl.5 | 1 | unresolved embedded dependencies/source-distribution |

Project LICENSE (full GPLv3) and NOTICE are copied from the pinned backend source,
not the mutable checkout; source hash remains in root manifest. The five-component
inventory covers third-party payloads; it is not a replacement project SBOM.

Remaining manifest reasons (not waived):
- `Cronet:fork-vs-donor-abi-mismatch`
- `Cronet:generated-Chromium-credits`
- `Cronet:linked-dependency-selection`
- `Cronet:source-chain-unverified`
- `sing-box-vpnctl:corresponding-source-distribution`
- `sing-box-vpnctl:embedded-go-notices`

## Mechanical and adversarial evidence

`PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s packaging/arch/tests -p 'test_*.py' -q`
passed121tests, zero failures/errors/skips locally and on worker snapshot (0.896s).
This is the prior80tests plus41new license/integration tests. CI uses the same discovery.
Four regression fixtures ran isolated against snapshot7eb5cbc3: four expected RED
failures (unknown ID/version falsely verified, native-only/mixed runtime assets lost).
Same fixtures GREEN against final module. Temporary old-module loader was removed
from permanent tests; no skipped historical tests remain.

Independent security and acceptance reviewers initially required changes. Lead
source-verified the findings, recorded them in OPEN-DEFECTS, fixed them and obtained
bounded independent `scoped_verified` follow-up in
`omarchy-license-review-fixes-2026-09-20.md`. This is not legal approval or a claim
that the pipeline resists a hostile same-UID process concurrently mutating its files.
Skills: change-verification, security-review, repository-readme; adversarial findings
were consolidated rather than launching a duplicate full audit.

Important failed attempts preserved:
1. Snapshot7eb5cbc3 invocation used unsupported driver flags / omitted --repo; exit2
   before publish. Corrected orchestration to existing CLI, no new interface added.
2. Snapshotf7de5226 publish succeeded but collector refused missing AUTHORS because
   the packaging src/ ignore pattern excluded vendored upstream notice paths. Fixed
   with scoped notices exception; exported51-hash check prevents repeating omission.
3. No validator was relaxed to make either failure green.

Final edits after successful build are test import cleanup and reports only; verify
build inputs equal snapshot before commit. CI/exact-SHA receipt is attached to draft
PR296 after task push; release, merge and installation remain unauthorized.
