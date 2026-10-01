# tools zone guidelines

`tools/` contains repository maintenance, release verification, CI scripts, and helper utilities.

## Scope and responsibilities

- Commit CI verification (`tools/verify-last-commit-ci.ps1`) and post-push watchers (`tools/watch-after-push.ps1`).
- Post-ship release verification (`tools/post-ship-verify.ps1`) and Windows test VM automation (`tools/brat-verify.ps1`, `tools/testvm-control.ps1`).
- Build helper scripts for sing-box and libbox (`build-singbox-lx.sh`, `build-libbox-aar.ps1`).
- Refactor equivalence harness (`tools/refactor-equivalence/`, see its README): line-multiset check and seeded corpus comparison of a behaviour-preserving change; runs on a worker only.

## WINBRAT remote testing constraint

All live verification goes through `tools/brat-verify.ps1` against the fixed WINBRAT
target defined in the "Release and WINBRAT contract" section of `docs/agent-contract.md`.

## Tracked payloads and generated caches

- `tools/icons/` holds the ORIGINAL mascot art (`src/mascot-black.png`, `src/mascot-white.png`, byte-identical to `VPNRouter.App/Assets/penguin_mascot*.png`) and the script that places it on light or dark tiles for every icon (Android adaptive and legacy, Windows `.ico`, macOS `.icns`, in-app tile and logo). The mascot itself must not be redrawn or altered (owner rule, 2026-09-30). Run `python3 tools/icons/make-icons.py` (needs ImageMagick) and commit the generated files.
- `tools/icons/lucide/` holds the Lucide SVG files used by the Android UI and by the desktop `Pictogram` control (unchanged copies of one Lucide tag, see `VERSION` and `LICENSE`). `python3 tools/icons/lucide-to-cs.py` regenerates `VPNRouter.Core/Services/UiIcons.cs`; `python3 tools/icons/check-icons.py` renders the generated path data, checks mirror symmetry and the 24 grid, and writes contact sheets (needs `rsvg-convert` and ImageMagick); `python3 tools/icons/scan-symbols.py` lists text symbols and emoji left in the Android UI. Take new icons from Lucide, do not draw them by hand.
- Generated source/build caches such as `tools/singbox-cache/` remain untracked. Do not commit or broadly clean generated caches without exact owner approval.

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"
```
