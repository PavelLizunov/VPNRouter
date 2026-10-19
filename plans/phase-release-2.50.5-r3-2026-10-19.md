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

Outcome: in progress; no candidate yet published.
