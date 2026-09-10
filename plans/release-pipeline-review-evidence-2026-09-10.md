# Release pipeline review evidence

## Scope

Approved Micro-Spec, PR #255, implementation working tree compared with brief commit `5f3d37e2`, accepted product baseline `43138f7c36e8dbe42aa2eb47d8e2216eb120707f`. No release, tag, merge, deployment, credentials or infrastructure changes performed. Unrelated untracked files preserved.

## Independent review

Initial separate workflow and verifier audits traced source, signing, artifact identity, CI provenance, cleanup and instructions. Confirmed findings registered in OPEN-DEFECTS before repairs. Three independent final reviewers examined correctness, security and test coverage against full scoped diff and owning source.

Confirmed integration findings and repairs:

- Stale root-hash assertion: updated to hash-object comparison.
- PowerShell function mock comma-array semantics: quote native JSON field argument.
- Updater fixture used wrong root path: Core and copy probe now use `_bootstrap`, launcher remains root.
- Process ownership was checked before pinning native handle: moved handle acquisition before path validation; retained explicit test ordering.
- Integrity final names-only comparison missed same-name replacement: snapshot release ID/state plus asset ID/name/size/update/digest/state, download asset IDs, recompare final snapshot.
- Removed unreachable three-sample soak branch after adopting immediate failure.

Security limits: GitHub draft checks and upload/publication are not atomic; owner must serialize staging, recovery and publication. No-clobber prevents overwriting existing assets but does not create a transaction. Sidecars establish consistency, not independent publisher authentication. AppImage is hash-only; non-Windows embedded-version inspection remains soft. Live WINBRAT process/UI actions were not run.

## Observed checks

- Baseline main check-runs: success, paginated GitHub API.
- Brief PR CI: test, characterization-windows, go-test-windows and grep all passed; workflow 34467332627 (implementation not included).
- Lead `git diff --check`: passed after integrated repairs.
- Lead PyYAML parsing plus `bash -n`: all repository workflows parsed, 58 Bash blocks passed before final identity snapshot delta; repeat before handoff.
- Independent workflow reviewer executed benign embedded Python integrity fixture successfully and APT paginated jq selection fixture successfully; these are reviewer evidence, not .NET suite execution.
- Actual read-only `gh release view v2.49.3 --json assets` schema includes id, size, updatedAt and name; initial reviewer concern about absent id was refuted.

## Environment preflight

Windows worker identity WINBRAT confirmed read-only; no dotnet/MSBuild/testhost job, CPU 0%, about 14 GiB available RAM and 110 GiB free disk. SDK list is 10.0.302 and 10.0.400, not exact 10.0.301. `global.json` permits latestPatch, but canonical release instructions and current resolver explicitly require exact SDK; do not silently relax that contract. No SDK installation performed.

Linux worker identity debian-xfce confirmed read-only, low load, about 6.6 GiB available RAM and 53 GiB free disk; dotnet absent from PATH. No build or provisioning performed.

## Implementation CI round 1

Commit `c7e3043bd42707ac2be7bc45bde264645c131958`, run 34470114730: Windows contracts 151 passed / 3 failed; Ubuntu discovered 3050, 2991 passed / 2 failed (remaining skipped per suite). Separate Windows update run 34470114613 passed, including staged-copy sentinel and exact receipt assertions. Go and grep passed.

Failures were two stale source assertions after integration (native-stderr helper variable names and `_bootstrap` sentinel path), plus an existing installer PowerShell 5.1 parse failure exposed by adding ReleaseToolingContractTests to Windows CI. The installer has a UTF-8-no-BOM em dash inside a quoted warning; ANSI decoding interprets its bytes as smart-quote syntax and causes later parse cascades. Minimal repair substitutes ASCII hyphen only. Tests remain enabled and await rerun.

macOS worker preflight: mm4.local, 41 GiB free disk, no dotnet on PATH; no SDK provisioning/build performed.

## Verified corrective snapshot

Commit `26b0bdb8` on PR #255: all five check groups passed. Run 34470761742 has 154/154 Windows contracts (zero skipped), Ubuntu build zero errors and 3050 discovered tests: 2993 passed, 57 skipped, zero failures. Windows updater run 34470761749 passed; Go Windows and grep passed. These exercise real PowerShell 5.1 isolated gates, parse checks, hash/inventory/soak fixtures, Linux embedded Python integrity fixtures and the packaged Windows updater. New tests were not disabled to obtain green.

Skills applied: change-verification (executed CI evidence), security-review (differential workflow/script review and reachable trust boundaries), bug-hunt (independent correctness/security/test reviewers), repository-readme (both source-build sections updated). Final review corrections independently checked; runtime service changes and secrets were outside scope.

## Remaining acceptance limits

Implementation review and PR CI are verified within the exercised scope. The full Release solution command and unfiltered visual suite were not run on the local workers because the exact SDK was unavailable; CI builds the test graph and Windows packages and runs its documented filtered suites. No full platform release builds, production signing, APT publication, Homebrew notification or fixed-WINBRAT live update/dataplane/cleanup operation was performed. Those require an explicit release/deployment authorization. Some ledger entries remain unchecked as external acceptance follow-ups; no waiver is implied.

The code and procedure repairs are ready for owner review, not a claim that a candidate has shipped or that all future releases are problem-free. GitHub draft/upload/publish operations are not transactional: serial owner operation and immutable release policy remain required. A future release must pass all documented exact-tag gates. Post-ship currently insists on exact SDK 10.0.301 despite global.json latestPatch; reconcile or provide that SDK explicitly before the real gate rather than silently bypass it.
