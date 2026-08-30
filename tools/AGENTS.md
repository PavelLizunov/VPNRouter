# tools zone guidelines

`tools/` contains repository maintenance, release verification, CI scripts, and helper utilities.

## Scope and responsibilities

- Commit CI verification (`tools/verify-last-commit-ci.ps1`) and post-push watchers (`tools/watch-after-push.ps1`).
- Post-ship release verification (`tools/post-ship-verify.ps1`) and Windows test VM automation (`tools/brat-verify.ps1`, `tools/testvm-control.ps1`).
- Build helper scripts for sing-box and libbox (`build-singbox-lx.sh`, `build-libbox-aar.ps1`).

## WINBRAT remote testing constraint

- All remote VPN, install, launch, connect, UIA, and live log verification operations must target ONLY the dedicated test VM `WINBRAT` (`100.115.182.0`) via `tools/brat-verify.ps1`.
- Never install, run, or verify VPNRouter on the local development workstation under `C:\Program Files\VPNRouter`.

## Tracked payloads and generated caches

- `tools/zapret/` is a tracked bundled runtime payload; preserve its committed binaries and support files.
- Generated source/build caches such as `tools/singbox-cache/` remain untracked. Do not commit or broadly clean generated caches without exact owner approval.

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"
```
