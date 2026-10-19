# Candidate 2.50.5-r3 - 2026-10-19

The owner explicitly requested a prerelease of the Russian IP bypass fix and
updated website help animations. PR #604 is merged at fde1f28a; its product
change passed the full Windows suite (3457 passed, 19 skipped), focused tests,
negative control, real-core IP routing and all five CI checks.

Scope: bump AppVersion to 2.50.5-r3, merge this version through green CI, create
an immutable tag/draft, stage and verify 18 assets, publish a prerelease while
2.50.4 remains Latest, run fixed-WINBRAT post-ship verification. Website guides
are independently owned by PavelLizunov/ninitux-landing and deployed separately.

Invariants: preserve original checkout/index, no stable promotion or retention
cleanup, proxy DNS unchanged. Rollback: existing stable remains available.

Gates: build/tests already passed for the one-line fix; exact version-head CI,
18 assets/hashes, tag integrity and Windows update tests, strict verifier,
post-ship two cold cycles and release outcome are pending.

Worker preflight: WINBRAT identity, 8 logical CPUs, SDK 10.0.301 and 119 GiB free
space previously confirmed. Fresh available RAM 11.1 GiB; recheck jobs and load
before release build. SignPath secret names and repository variables absent.
Unsigned Windows staging is the configured path.

## Outcome

Published https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.50.5-r3 at
19:57:39 UTC. Immutable annotated tag peels to
`6b1304ecfd4f284c2f06fba97755df66cc45dfff`; stable Latest remains v2.50.4.
PR #605 version-head CI green. Exactly 18 assets. Windows unsigned build exit 0;
install ZIP SHA256 `77d2ec22c2325ef58cb8d4229b3bba0ed007b63e7ad2a9bd51d0f0e38c41f244`;
update ZIP `d12fe157dd3682f86fae95dd21d16aeb3185964293c029040c8c6ed6e83802dc`.
Both driver bundles and all sidecars passed integrity verification.

Exact-tag runs: mac 39313398272, Linux 39313397766, Android 39313397748,
Windows update 39313397737, tests 39313397885: success. Prepublication integrity
39314999303 passed. The earlier 39314806429 correctly failed with six missing
Windows files after a local sidecar-format check stopped upload; the two ZIP
sidecars contain bare hashes, not sha256sum lines. All three Windows files were
then explicitly hashed and uploaded without overwrite; no artifact was changed.
Postpublication integrity 39315294034 and APT 39315299163 passed. Strict verifier
on WINBRAT: 9 green, 0 tolerated, 0 in progress, 0 hard red. Open P0/P1 gate passed.

Post-ship-local installed the published SHA-checked ZIP on fixed WINBRAT and
reported PASS for two cold UI connect/disconnect cycles. Final state: one GUI,
no core, TUN absent, direct routes. Lifecycle scan: 150 entries, 2 CoreStarted,
2 TunReady, 2 HealthRecovered, zero error/fatal/unknown-error counts. Installed
Core ProductVersion is `1.0.0+6b1304ecfd4f284c2f06fba97755df66cc45dfff` and
current.json direct bypass has both vpnrouter-geosite-ru and vpnrouter-geoip-ru.
Core-only/not UI-testable product delta. Screenshot suite was not rerun against
the release tag; the full pre-release Windows suite included visual cases.
No real macOS/Linux/Android device or owner's exact-site scenario was verified.

Website: ninitux-landing PR #5, deployed exact content SHA
`b3ad55402456ee83c167d6d7e13c9a9b1827248d`; container healthy. Three-dot menu
navigation in apps/free guides, final frame has selected reserved-address
examples and enabled Connect. Site CI 39315409094: 27 passed, 0 failed. Public
browser check: 30/30 RU/EN frames, no script errors, no horizontal overflow at
390 px. Downloaded live final-frame hash equals the repository file. The help
host is ninitux.com; vpn.ninitux.com root is a separate static install page.
Fixture PR #606 merged independently, not part of this immutable release.
Separate-context review found no website correctness defect; a coverage gap was
closed by exact-window bilingual enabled-Connect assertions in the fixture.

Worker build/render/post-ship task directories and temporary scripts are removed
after evidence capture. The published application stays installed and disconnected.
The original developer checkout/branch/index remain unchanged. No stable cut,
retention cleanup or default-branch migration was performed.
