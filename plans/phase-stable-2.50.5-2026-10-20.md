# Stable 2.50.5 from candidate 2.50.5-r3

## Authority and scope

The owner tested r3 and explicitly authorized stable publication: "Everything
seems fine, you can release." Cut stable 2.50.5 with no new product behavior.
Since 2.50.4: protocol icons in server lists and compact home mode, and the
Russian IP bypass correction. Website help is already deployed independently.

Base main 0faa1a19 differs from candidate 6b1304ec only in documentation and an
offline UiProbe fixture/test, not shipped application behavior. Keep the original
worktree/branch/index intact. No retention cleanup or mirror publication.
Rollback is the previous immutable stable release, not retagging.

## Preconditions and checks

Candidate: 18 assets, all platform/tag/test/update/integrity/APT runs green,
strict verifier 9 green/0 red, post-ship two cold cycles PASS, 0 lifecycle errors.
The one-line product fix passed full Windows tests (3457/19 skipped) and real
core IP routing. Previous-stable 2.50.4 -> r3 real updater with two cycles is
completed before the stable tag: helper completion, installed version, relaunch,
receipt consumption and two cold cycles PASS, lifecycle 172 entries/zero errors. No open P0/P1, no waiver.

Worker preflight 2026-10-20: WINBRAT, 8 logical CPUs, 1% CPU, 13.4 GiB free
RAM, 118.6 GiB free disk, private SDK and Inno Setup present, no build/test/core
processes. SignPath secrets/variables absent; configured unsigned Windows path.

Stable gates: version-only product diff, exact-SHA build/full tests/visual tests,
PR/tag CI, 18 assets and drivers, strict prepublication checks, publish Latest,
APT/Homebrew/Android/Windows consumers and stable post-ship cycles. Pending.

## Outcome

Published v2.50.5 as public/non-prerelease/Latest at 2026-10-20 00:37:56 UTC.
PR #610 merged at `097393a79641848e84cb76aa324b8bc873da3ed6`; annotated tag
peeled SHA verified equal. No shipped behavior added after r3 except version.
The owner explicitly resumed publication after the site-specific follow-up;
that report remains bounded by `phase-ru-bypass-diagnostics-2026-10-20.md`:
Avito direct versus 2ip.io proxy, not a demonstrated lost geo bypass.

Windows Release build: 0 errors, 231 existing warnings. Full suite at PR head
and again exact stable tag: 3459 passed, 19 skipped, 0 failed. Exact-tag visual
and protocol slice: 26 passed. PowerShell 5.1 parser and open P0/P1 gate PASS.
Separate-context version/documentation review: no findings, same-family only.
The first queued extra tag-check worker job exited before building because a
reusable dotnet node was still present; no result claimed from that job. The
later exact-tag visual build and full tests supply the actual check evidence.

18 assets: 6 Windows, 4 macOS, 6 Linux, 2 Android. Windows build exit 0.
Install ZIP SHA256 `9e2c0277074e0613e9409a742e8513575de7ecf7f021b2efa20e9fc3f6dffec7`;
update ZIP `d9053ea06a8abc0ef7bee09b52f83d035b662e3e838818a254f7077aae6068c2`.
Both True Split bundles and every sidecar PASS. SignPath absent: Windows unsigned.
Canonical tag runs success: Linux 39347602129, macOS 39347602211, Android
39347602218, Windows update 39347602186, tests 39347602225. Integrity before
publication 39348595209 and after 39348786842 PASS. APT 39348789718 PASS;
public Packages contains version 2.50.5 and its canonical .deb path.
Strict verifier: 8 green prepublication, 9 green postpublication, 0 red/tolerated.
Homebrew dispatch 39348794101 PASS, tap commit
`ad5d4b9ee909710756194c9965b23965dbf66991`; 2.50.5 DMG checksum equals release
sidecar `d091375d012688df7aa1672d9a6990dfee57eaeeaa3edbd2ca2abd41117f802a`.
Windows canonical installer URL returns 302; Android page uses latest stable API.

After an unrelated worker build completed, published-binary post-ship-local ran
on fixed WINBRAT: SHA-verified install, two cold UI/data-plane cycles PASS.
Lifecycle 152 entries: 2 CoreStarted, 2 TunReady, 2 HealthRecovered, zero error,
fatal or unknown-error counts. Final state one GUI, no core, TUN absent/direct.
The full maintainer-PC post-ship controller was not run; local-shim binary gate,
strict/integrity and exact-tag visual tests were run separately as documented.
Candidate-to-stable updater not separately exercised (update tooling unchanged).
Real macOS/Linux/Android device runtime remains unverified; their packaging/CI
PASS is not a real-device claim. No external mirror publication requested.

Task-owned worker checkouts/artifacts/logs/scripts are removed after evidence
capture and verifier Cleanup. Published 2.50.5 stays installed/disconnected.
Original developer checkout and index preserved. No release/tag retention cleanup.
