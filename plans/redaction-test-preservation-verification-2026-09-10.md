# Redaction test preservation verification

## Scope and verdict

Verified test-only transfer at `e20869abae20c15a1750c38e00b4f346fc6e2aeb`: existing RuleSet fixture now asserts successful result and no remote path/full URL in logs; existing diagnostics fixture exercises auth_key and client_key. Owner approved through `preserve-simple-redaction-tests`. No production regex, launcher, public API or dependency change. Independent source review passed after correcting a Unix false positive by separating local cache filename from remote URL path.

## Executed evidence

Linux-worker (`debian-xfce`, tester), existing SDK 10.0.301. Fresh isolated checkout fetched the exact SHA from canonical GitHub; checkout log and TRX independently read by parent. Command, run in the worker's `cleanup-e20869ab/repo`:

```sh
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release /m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false --filter 'FullyQualifiedName~VPNRouter.Tests.DiagnosticsRedactorTests.Logs_RedactPrefixedSecretKeys|FullyQualifiedName~VPNRouter.Tests.SubscriptionUrlRedactionTests.RuleSetCacheManager_LogsDoNotContainToken' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=focused.trx' --results-directory /home/tester/vpnrouter-verification/cleanup-e20869ab/evidence/results
```

Full executable path and command environment are in test.log. Exit 0; TRX: total=executed=passed=2, failed=notExecuted=0. Both named tests above actually ran. Completed 2026-09-10 at 21:16:40 UTC. Build warnings existed; no warning-free claim.

All four CI checks at the same SHA passed: Ubuntu test, Windows characterization, Go in [34530917972](https://github.com/PavelLizunov/VPNRouter/actions/runs/34530917972); fingerprint in [34530918316](https://github.com/PavelLizunov/VPNRouter/actions/runs/34530918316).

## Recovery evidence and limits

Collected primary logs: `/var/lib/dsh/Project/VPNRouter-cleanup-archives/2026-09-10-redaction-e20869ab/evidence/` (local, not off-host backup).

- checkout.log SHA256: `ac15a1ccf9c421008c2dc93b2e74904e66917a7ce85b0b9bb1a74a2a7584b27d`
- test.log SHA256: `453132ffcf7afb78dd3bfda8ca72dd20e61ad2812a9df06b0f2bb98b281eb8f9`
- exit.txt SHA256: `65c1c39f1b2713d953ae37655212f3206c9b977ed3c2d997638553250106eaa7`
- results/focused.trx SHA256 (parent independently matched): `557f2401c2e8ee2c598dd4d08b6e2916ea73eb55607b1016d74db9b4eb21e10f`

This is bounded test-preservation acceptance, not broad main regression/performance verification, release authority, merge approval or failure-path logging audit. Subsequent owner approval accepted PR257 exact head bfa2e68f after all four final-head checks passed (runs 34531717019 and 34531717129); merged as 2689ee77a06cbb0274e3dc3adf141570c4377da8. The three dependent RuleSet/prefixed-key remote branches were then removed with exact-SHA leases; see post257-preserved-branches-cleanup-2026-09-10.md. All four collected evidence hashes were independently matched. Worker task checkout `/home/tester/vpnrouter-verification/cleanup-e20869ab/repo` was removed after fresh exact-HEAD, clean-tree and no-build/test-process checks; absence verified. Worker evidence directory and collected local evidence remain retained. Shared SDK/caches were untouched.
