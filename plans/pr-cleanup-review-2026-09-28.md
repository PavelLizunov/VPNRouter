# PR cleanup review, integration and recovery

## Summary

Preserve useful portions of #315, #316, #319 and #322 in integration PR #324.
Do not merge incompatible dependency upgrades or no-op security claims. Omarchy
#296 is explicitly outside cleanup and must remain open with its branch/worktree.

## Independent review

Transport: explicitly authorized one-worker workflows, `ninitux/claude-opus-latest`.
No recursive delegation or reviewer writes. Static source review, not runtime
certification. The coordinator independently checked every accepted finding.

First review inspected these immutable snapshots against main `c2a1fa2e`:

| PR | Head | Coordinator disposition |
| --- | --- | --- |
| 315 | 5228bf5761357dc4fa5d09717d7738557356396d | Preserve 3 Actions pin changes. |
| 316 | 0739ab12e226ef3e81e1b272683850e906bcfb4e | Preserve non-Spectre updates only. |
| 317 | 38e018810cd679fa1d2b1c1883f9f1b8e39d17b5 | Defer SkiaSharp major; failed test CI. |
| 318 | d9c9fa414db3780e6764bbd40aa4377c3f3dff5e | Defer xunit major; both .NET jobs failed. |
| 319 | c2b1fd352ee1f3d22cebd782a244bdaa49645b6f | Preserve ArgumentList; replace weak tests and omit false ledger closure. |
| 320 | 53a91bcf1844cdd898c23537862368999f003fbd | Do not import: no demonstrated caller-controlled exploit; tests do not establish no launch. |
| 321 | 619b04f1fd50de1ec3d495f62b114435d97ccda2 | Do not import: BuildProxyLink already fixes tg://; malformed query test can still reach process launch. |
| 322 | 65a5ee6c362b199aa22c5a417f8c55c3c684b306 | Preserve HTTP(S) guard; remove unsafe malformed-input logging. |

The first worker called #315/#319/#320/#321/#322 READY. Coordinator source checks
found reasons not to accept that conclusion unchanged. In particular, do not
repeat the worker's unproven file:// SSRF claim, general xunit API-change claims,
or claim that mismatched Spectre package version numbers themselves prove an
incompatibility. The confirmed #316 failure is missing CancellationToken
Command/AsyncCommand overrides in Windows CLI compilation (check 108308297482).

Second independent review covered the corrected integration diff at HEAD
`0a713980755510d46d9123f87532e75e218d6a53`, diff SHA256
`5263e6516fc66917b9cbbfb01b7765ef813ceac621c99382081aeae6141c2bb1`.
Verdict: CHANGES_REQUIRED, one confirmed compile issue: the new positive URL test
assigned init-only FreeConfigSource.Url after construction. Confirmed against
FreeConfigModels.cs:156 and corrected exactly as recommended with an object
initializer. Other source changes were accepted. This correction was checked by
the coordinator; do not relabel the original worker verdict as a clean PASS.

## Integration scope

- #315: setup-android v4.0.4 and paired CodeQL init/analyze pins. Upstream GitHub
  API confirms the Android tag SHA and the CodeQL releases/v4 merge commit.
- #316: Avalonia/App+headless 12.1.3, Hosting packages 10.0.12, Test.Sdk 18.10.1.
  Spectre remains 0.49.1, SkiaSharp 3.119.4, xunit.v3 3.2.2.
- #319: pure BuildFlowsealProbeStartInfo factory with ArgumentList. Existing
  OS/admin/script-existence guards, encodings, redirects and launch behavior stay.
  Tests inspect arguments without creating invalid Windows paths or launching a
  process. OpenServiceMenu remains unchanged and its existing debt stays open.
- #322: explicit HTTP(S) guard. Refusal log contains no user-supplied URL, source
  name, properties or exception. Tests cover invalid/empty/null schemes, no HTTP
  send, zero logged input and valid HTTP/uppercase HTTPS through a fake transport.
- Windows characterization CI explicitly adds both affected test classes; Linux
  already runs the suite. This CI-filter extension is new integration work, not
  part of #315's original pin-only diff.

## Verification limits

No product build, GUI, VPN, live hosts edit, PowerShell probe or privileged worker
execution on this control-plane host. No before-fix runtime red/green is claimed.
Integration compilation and regression evidence must come from exact-head GitHub
CI. Android release and scheduled CodeQL workflows are not exercised by ordinary
PR CI; no release, signing, deployment or platform acceptance is claimed.

## Recovery

A complete Git bundle was made before closing PRs or deleting branches:
`/var/lib/dsh/Project/VPNRouter-backups/2026-09-28/before-cleanup.bundle`.
Its adjacent `before-cleanup.json` records exact live origin/local tips, SHA256,
size and preservation exclusions. `git bundle verify` passed. The bundle contains
committed history/refs, not untracked user files. Those files remain untouched in
the original Omarchy checkout.

Recovery example (choose the archived branch ref from the manifest):

```sh
git fetch /var/lib/dsh/Project/VPNRouter-backups/2026-09-28/before-cleanup.bundle refs/remotes/origin/BRANCH:refs/heads/recovered-BRANCH
```

Closures, deletions, exact final CI and baseline receipts will be recorded below.
