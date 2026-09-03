# Iteration A — Build/release raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `BR-1` through `BR-3`
Status: unverified swarm output; no item below is accepted until lead source verification.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| BR-1 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 3 | 3 |
| BR-2 | 3/3 | correctness; security/fail-closed/supply-chain; tests/platform/upstream | 8 | 7 |
| BR-3 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 7 | 6 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| BR-1-1 | P1 | Android asset naming may diverge between signed workflow, producer, integrity verifier, and landing page | `.github/workflows/sign-android.yml:83,125`; `build-android.ps1:88,100`; `verify-release-integrity.yml:220`; `packaging/android-page/index.html:101` | pending; overlaps BR-3-1 |
| BR-1-2 | P1 | Pre-push hook may treat informational output from a successful CI verifier as failure | `.githooks/pre-push:70`; `tools/verify-last-commit-ci.ps1:47` | pending |
| BR-1-3 | P2 | Multi-ref pre-push tag handling may skip the main-branch CI gate | `.githooks/pre-push:36-38` | pending |
| BR-2-1 | P1 | Local Linux build script may omit `sing-box` and `libcronet.so` from the package | `build-linux.ps1:40-57` | pending |
| BR-2-2 | P1 | Libbox AAR build may omit required gomobile Java package and linkname arguments | `tools/build-libbox-aar.sh:135-141` | pending |
| BR-2-3 | P1 | Windows build downloads a sing-box release archive without verifying a SHA256 sidecar | `build.ps1:354-366` | pending |
| BR-2-4 | P2 | Linux build may clone floating `wgturn-core` main instead of the commit pinned on macOS | `build-linux.ps1:85-97`; `build-mac.sh:77,96` | pending |
| BR-2-5 | P2 | Local Android build downloads `libbox.aar` without a pinned SHA256 check | `build-android.ps1:65-68` | pending |
| BR-2-6 | P1 | Windows installer may continue an administrative install when the checksum sidecar is missing | `packaging/windows/install.ps1:206-208` | pending |
| BR-2-7 | P2 | Local Android publish uses plural `RuntimeIdentifiers` while CI pins singular `RuntimeIdentifier` | `build.ps1:817`; `.github/workflows/build-android.yml:322`; `ReleaseToolingContractTests.cs:85-86` | pending |
| BR-3-1 | P1 | Android sideload asset suffix may ignore canonical `android-arm64.apk` releases | `SideloadSource.cs:243`; `build-android.yml:347`; `ReleaseToolingContractTests.cs:90`; `verify-release-integrity.yml:220` | pending |
| BR-3-2 | P1 | Update sources may demote a failed checksum-sidecar fetch to an unverified update instead of failing closed | `GitHubReleaseSource.cs:247`; `SideloadSource.cs:138`; `UpdateChecker.cs:307` | pending |
| BR-3-3 | P2 | Update smoke test may extract the previous stable archive without checking its sidecar | `tools/smoke-update.ps1:85,101` | pending |
| BR-3-4 | P2 | Post-ship commit resolver may treat untracked files as tracked modifications | `tools/post-ship-verify.ps1:185` | pending |
| BR-3-5 | P2 | Android unsigned staging asset name may conflict with the fixed final 16-asset contract | `tools/post-ship-verify.ps1:143`; `.github/workflows/sign-android.yml:62` | pending |
| BR-3-6 | P2 | Local Android build output naming may differ from CI and verifier naming | `build-android.ps1:88`; `build-android.yml:347`; `verify-release-integrity.yml:220` | pending; overlaps BR-1-1 |

## Lead status

Pending Iteration B and source verification. Producers, staging-only names, final asset renames, release gates, and existing contract tests must be traced end-to-end before accepting any naming or integrity candidate.
