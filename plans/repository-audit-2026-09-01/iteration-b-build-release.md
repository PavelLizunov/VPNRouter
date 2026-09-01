# Iteration B — Build/release counter-audit index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage: `BR-1` through `BR-3`
Status: independent adversarial counter-audit; signals are not lead verdicts.

## Coverage receipts

| Leaf | Reviews | Fresh lenses | A-candidate checks | New reports |
|---|---:|---|---:|---:|
| BR-1 | 3/3 | producer/consumer; multi-ref CI failure injection; upstream Actions/negative evidence | 9 | 8 |
| BR-2 | 3/3 | supply-chain graph; quoting/assets failure injection; upstream platform/negative evidence | 21 | 3 |
| BR-3 | 3/3 | asset lineage; sidecar/feed failure injection; upstream GitHub/negative evidence | 18 | 7 |

## Cross-iteration signals

| Candidate | Iteration B signal | Primary cited evidence |
|---|---|---|
| BR-1-1 | supported by 3/3 | Android producer/signer/verifier/landing-page names |
| BR-1-2 | supported by 3/3 | `.githooks/pre-push:70`; `verify-last-commit-ci.ps1:47-48` |
| BR-1-3 | supported by 3/3 | `.githooks/pre-push:36-39` |
| BR-2-1 | supported by 3/3 | `build-linux.ps1:40-71`; `build-linux.yml:98-119` |
| BR-2-2 | supported by 3/3 | `build-libbox-aar.sh:135-141`; PowerShell counterpart and Java imports |
| BR-2-3 | supported by 3/3 | `build.ps1:354-366` |
| BR-2-4 | supported by 3/3 | `build-linux.ps1:85-97`; `build-mac.sh:77,96-100` |
| BR-2-5 | supported by 3/3 | `build-android.ps1:65-68`; CI hash gate |
| BR-2-6 | supported by 3/3 | `packaging/windows/install.ps1:193-208` |
| BR-2-7 | supported by 3/3 | `build.ps1:817`; `build-android.yml:322`; contract test |
| BR-3-1 | supported by 3/3 | `SideloadSource.cs:242-244`; canonical `android-arm64.apk` consumers |
| BR-3-2 | supported by 3/3 | `GitHubReleaseSource.cs:122-135,247-257`; `SideloadSource.cs:134-205`; `UpdateChecker.cs:307-324` |
| BR-3-3 | supported by 3/3 | `tools/smoke-update.ps1:85-87,101` |
| BR-3-4 | supported by 3/3 | `tools/post-ship-verify.ps1:185-188` |
| BR-3-5 | supported by 3/3 | post-ship 16-asset contract vs Android signing staging/final names |
| BR-3-6 | supported but duplicate of BR-1-1 | local Android output vs CI/verifier naming |

## Materially new Iteration B candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| BR-B-1 | P1 | SignPath tag validation may compare an annotated tag object instead of its peeled commit | `sign-windows.yml:78-80` | pending lead trace |
| BR-B-2 | P1 | Release-integrity failure handler may attempt an invalid draft edit on a published release | `verify-release-integrity.yml:62-63,477` | pending lead trace |
| BR-B-3 | P2 | Strict CI gate may treat path-filtered update workflow absence as failure | `test-windows-update.yml:44-51`; `verify-last-commit-ci.ps1:230-241` | pending lead trace |
| BR-B-4 | P2 | macOS smoke command uses `continue-on-error` around binary/schema checks | `build-mac.yml:86-87,99-106` | pending lead trace |
| BR-B-5 | P2 | Linux build may swallow wgturn CLI build failure and package an incomplete artifact | `build-linux.ps1:103-108` | pending lead trace |
| BR-B-6 | P1/P2 | Post-ship sidecar parser may reject standard `sha256sum` filename form | `post-ship-verify.ps1:106-109,263-267`; platform sidecar producers |
| BR-B-7 | P1/P2 | Release-integrity workflow may leave missing assets as warnings with a green exit | `verify-release-integrity.yml:235-239,409-411,453-456` | pending lead trace |
| BR-B-8 | P1 | Post-ship verifier may not verify content/hashes for 12 non-Windows assets | `post-ship-verify.ps1:136-145,235-247` | pending lead trace |
| BR-B-9 | P2 | Update feed HTTP/JSON failures may be presented as a silent no-update result | `GitHubReleaseSource.cs:97,224-235`; `UpdateChecker.cs:220-225` | pending lead trace |

## Lead status

Iteration B coverage is complete. The high agreement rate is only a prioritization signal; artifact staging names and final published names must still be traced through actual workflow jobs before any change.
