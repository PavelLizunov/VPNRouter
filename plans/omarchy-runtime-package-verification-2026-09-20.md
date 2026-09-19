# Omarchy runtime staging package verification

## Scope and verdict

Approved by owner on 2026-09-19 23:40 Europe/Moscow. Source-only Arch x86-64
packaging, no installation, capabilities, root helper, Polkit or VPN activation.
Package construction and integrity verification PASS. Production readiness and
complete redistributable licensing are NOT established. Binary distribution is
blocked by the explicit notices inventory; this is not a waiver of that gate.

Branch: `dsh/omarchy-plugin-2026-09-17`, draft PR #296.
Approved brief commit: `b2054c06bdf490564df1033a91b1a3149bf7d922`.
Its five CI checks and exact-SHA canonical PowerShell verifier passed, no tolerated
failures; temporary verifier checkout removed on WINBRAT.

## Final package evidence

- Packaging snapshot: `d4478ecb7fa3be185a77d3c7a1f4e2a4c2869d6b`.
- Backend source: `05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a` (exact Git export,
  not mutable worktree). SDK 10.0.301, framework-dependent linux-x64.
- Worker: approved `omarchy-test`, tester identity, no root or installation.
- Artifact: `vpnrouter-headless-0.1.0-1-x86_64.pkg.tar.zst`.
- SHA-256: `c66156675639452628e75fe80cb0f11f906a964da7bf9af254d40f0b2f933bfa`.
- Retained in the worker's `vpnrouter-omarchy-checks/<snapshot>/build/pkgbuild/`
  directory with SHA-256 sidecar. No release artifact upload performed.
- Build command (worker-specific paths abbreviated):
  `python3 packaging/arch/build_package.py --repo <exact-object-repo> --work-dir <new-private-dir> --archive-cache <verified-runtime-inputs>`.
- Exit 0 includes mandatory post-makepkg package inspection. Package metadata and
  payload ownership are 0:0 in the archive via fakeroot, not a root build.
- Original archive modes checked before extractor normalization; package forbids
  hooks and any paths outside package metadata and `/usr/lib/vpnrouter-headless`.
- Manifest records exact source, archive and packaging-file hashes, tool versions,
  18 payload files, modes/sizes/hashes, ELF shared-library dependencies and
  `execution_authorized: false`. Manifest is descriptive, not an authorization.
- Static ELF inspection checks x86-64 for apphost, sing-box and Cronet. No sing-box
  execution or dynamic-linker `ldd` invocation was used.

Pinned runtime archive SHA-256:
`5f98eacc95b9ed9c1d53592605d812d9d2cb8c496fa95723cfb7c5f9447fe46e`.
Pinned upstream Cronet source archive SHA-256:
`f48703461a15476951ac4967cdad339d986f4b8096b4eb3ff0829a500502d697`.
Both downloaded on worker and verified before extraction. One initial network
request timed out; bounded IPv4 retry succeeded. Checksums prove selected bytes,
not independent upstream provenance or runtime safety.

## Tests and source equivalence

1. `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s packaging/arch/tests -p 'test_*.py' -q`
   passed **80 tests**, exit 0, on final packaging snapshot. Added the same discovery
   command to existing `headless-contracts` CI, without installing/building a package
   on GitHub runners.
2. Actual non-root makepkg build and mandatory package inspection passed on final
   snapshot. This is separate evidence from mocked subprocess unit tests.
3. `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~HeadlessRuntimePolicyTests' --logger 'console;verbosity=minimal'`
   passed **26/26**, skipped 0, exit 0, isolated HOME/XDG/runtime.
4. `timeout 180 VPNRouter.Headless.Tests/bin/Release/net10.0/VPNRouter.Headless.Tests`
   passed **37 groups**, exit 0, including **41 lifecycle** and **12 runtime-policy**
   subchecks. Test fixtures do not authorize a real TUN process.
5. Backend regression binaries reused from previously built immutable snapshot
   `dabdc8c24220a0a31533659d71ce613baaf15b29`. Verified `git diff --exit-code`
   against backend `05c0bcae` for Core, Headless, both test projects, global.json and
   Directory.Build.props with Markdown excluded: no source differences. The only
   original comparison difference was a README link; it was not ignored as code.
6. Headless was freshly published by the package build; no Avalonia filenames or
   deps.json references allowed. Existing non-Linux packaging and product code
   untouched. Full solution/platform coverage belongs to exact-SHA CI; no new
   live Windows/macOS/Android acceptance is claimed.

## Review and red/green corrections

Three independent Gemini read-only lenses covered archive security, builder
correctness and acceptance tests. Coordinator source-verified findings:

- Fixed Python source interpolation of shell paths with quoted argv + heredoc.
- Fixed cache tamper fallback, SDK cwd selection, mutable-worktree licenses,
  optional package inspection, makepkg environment leakage and ambiguous file
  selection. No offline-NuGet option added: network restore is allowed and now
  accurately documented, so that reviewer request was outside approved scope.
- Fixed two false-green tests that duplicated inline behavior. They now call real
  functions; main's source-tree forwarding and deps.json references are tested.
- Actual first package build `79b4b963...` failed because makepkg creates
  `src/payload.tar` as a symlink. PKGBUILD now uses original regular
  `startdir/payload.tar`, with the same hash and strict no-symlink validator. Tests
  emulate this exact layout and reject a symlinked original. Subsequent actual
  builds `53f17f48...` and final `d4478ecb...` passed.
- Added original-package-mode refusal and regression (group/world-writable modes).
  One path-test fixture initially hit the new mode guard first; corrected the
  fixture mode so path rejection still exercises its own branch. Final 80 pass.
- Deferred minor raw-tar EOF/parser divergence: ignored suffix files are not
  materialized. Recorded in OPEN-DEFECTS; broader validator reuse is not approved.

## Remaining gates and limits

- License inventory is incomplete: actual manifest lists Serilog-LICENSE,
  YamlDotNet-LICENSE and cronet-chromium-notices. Existing upstream LICENSE/README
  and repository notices are preserved; they are not a complete combined license
  grant. Do not publicly distribute this binary until notices/source obligations
  are checked. Source packaging remains reviewable in draft PR.
- No signature/protected installation trust root, constrained privileged broker,
  host-global network ownership, crash recovery policy, authorization lifetime,
  installation/update/removal acceptance, UI activation or live dataplane proof.
- Existing production policy stays unavailable. Supplying this package does not
  turn `connect` into an authorized operation.
- Builder assumes trusted private staging and trusted installed SDK/makepkg/system
  configuration; not a sandbox against a hostile builder or concurrent writer.
- Builds restore NuGet over network, are not claimed offline or bit-reproducible.
- Post-push exact-SHA CI receipt will be attached to PR #296; green CI is not
  permission to merge, publish, install or connect.
