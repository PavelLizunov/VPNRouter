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
