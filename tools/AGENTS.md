# tools zone guidelines

`tools/` contains repository maintenance, release verification, CI scripts, and helper utilities.

## Scope and responsibilities

- Commit CI verification (`tools/verify-last-commit-ci.ps1`) and post-push watchers (`tools/watch-after-push.ps1`).
- Post-ship release verification (`tools/post-ship-verify.ps1`) and Windows test VM automation (`tools/brat-verify.ps1`, `tools/testvm-control.ps1`).
- Build helper scripts for sing-box and libbox (`build-singbox-lx.sh`, `build-libbox-aar.ps1`).

## WINBRAT remote testing constraint

All live verification goes through `tools/brat-verify.ps1` against the fixed WINBRAT
target defined in the "Release and WINBRAT contract" section of `docs/agent-contract.md`.

## Tracked payloads and generated caches

- Generated source/build caches such as `tools/singbox-cache/` remain untracked. Do not commit or broadly clean generated caches without exact owner approval.

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"
```
