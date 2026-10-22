# Omarchy backend round 5

## Intent and contract

Continue PR #616 from 8194f44bbd13187a36d0a87f5e4a420d112a903d.
Prefer case-insensitive executable identity (desktop ID, reverse-DNS last segment,
or StartupWMClass) over a unique Exec basename. Ambiguous Exec matches have no
metadata. Parse only simple env prefixes; ignore unsupported wrappers rather than
attribute their processes to launched applications. Preserve exact process names.

Build pkgver 0.1.0.r<run_number>.g<source_sha7>, pkgrel <run_attempt>, explicitly
from CI inputs; include both in the manifest and version the artifact name.
Run hermetic regression fixtures and package/version checks in CI only, including
pacman vercmp. Review the diff and verify exact remote/PR head, green checks,
package metadata and test-produced runningApps samples before delivery.
No local .NET SDK/build/tests, VPN/TUN, live desktop changes, merge/tag/release.
Push the PR branch itself; all commits end with the requested Codex trailer.
phase-task-launcher and bug-hunt are absent from the available skill catalog.

## Outcome

Implementation and hermetic fixture tests added. Direct source/diff review,
Python AST syntax parsing and git diff --check passed. No local test or .NET
execution. CI pending the implementation push; external receipt: r5-backend.md.
Documentation delivery gate: grounded claims, direct prose and flow PASS; UI N/A.

## Implementation acceptance

At 469b69a158b41eaafc297bdedb648c6b9e22b3f0 all nine PR checks passed;
gh pr checks 616 --watch exited 0, and local HEAD, PR headRefOid and remote
branch matched. Dotnet run 39859423663: headless 39 aggregate checks passed/0
failed including Round5Checks; Linux 3210 passed/77 skipped; Windows 3379 passed/
19 skipped; Python 126 discovered, 125 passed with vercmp skipped on Ubuntu.
Arch run 39859419326 executed all 126 Python tests, including native vercmp,
then built, inspected and installed the package, passed offline stdio/runtime
policy smoke and verified capability restoration on same-file reinstall.
No TUN started. Android compile, Go and fingerprint checks also passed.

Artifact 12460354392 is vpnrouter-headless-arch-0.1.0.r12.g469b69a-1;
package SHA256 254ef99164e6fceddde82dcce9572f673024841b63b4fbe6b12a0ec05ec2ae50.
Read-only inspection of the downloaded package confirmed the SHA256 sidecar,
.PKGINFO version and packaged manifest source_commit/version/run inputs.
The fixture apps.list returned Ghostty/com.mitchellh.ghostty with the genuine
reverse-DNS entry present, and ghostty/null metadata with only two custom launchers.

This acceptance update changes documentation only. Exact final-head CI and
artifact IDs, hashes and test-produced JSON are recorded in the requested
external r5-backend.md receipt after its CI completes. No independent review
claimed; review was direct source/diff and affected-caller inspection.
