# Codex runbook: ship a rolling candidate vX.Y.Z-rN (VPNRouter)

You are shipping a **rolling release candidate** for VPNRouter from the dev box
(`C:\Project\VPNRouter`). This is autonomous up to (but NOT including) a stable
cut. Follow the steps exactly. Windows shell = PowerShell; `gh` CLI is available.

## Facts about this repo (do not assume the old docs)

- GitHub repo: `PavelLizunov/VPNRouter`. Git remotes:
  - `origin`  -> `https://github.com/PavelLizunov/VPNRouter.git` (GitHub, canonical)
  - `forgejo` -> `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` (mirror, via VPN, may be down)
- Current **stable / Latest** release: **v2.45.0**. Latest shipped prerelease: check
  `gh release list`. A candidate is `vX.Y.Z-rN` (e.g. next patch on r5 = `v2.46.0-r6`).
- Version single source of truth: `VPNRouter.Core/AppVersion.cs`.
- Windows build script: `build.ps1`. It bundles a **custom sing-box-lx core**
  (AmneziaWG + XHTTP) and the **Mullvad split-tunnel driver** — BOTH are required or
  you regress r4/r5. Inputs already present on the box:
  `publish\sing-box-lx.exe` and `tools\driver-cache\cc0affb2...\` (3 driver files).
- A pre-push git hook blocks the push if the **previous** commit's CI is red.

## Step 0 — pre-flight (all must pass; STOP if any fails)

```powershell
# a) previous commit CI must be green
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1   # want exit 0
# b) clean build
dotnet build VPNRouter.sln -c Release                                       # 0 errors
# c) regression + any tests relevant to the change
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter `
  "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"
```
If a UI/behaviour change: also run its own tests and verify the GUI on the test VM
(windows-brat), never on this dev box.

## Step 1 — bump AppVersion (MUST include the -rN suffix)

Edit `VPNRouter.Core/AppVersion.cs`:
```csharp
public static readonly string Version = "X.Y.Z-rN";   // e.g. "2.46.0-r6"
```
CRITICAL: the string must match the release tag EXACTLY, including `-rN`. A bump
without the suffix breaks the in-app update check (SemVer treats a suffixless build
as > the prerelease). Never ship two candidates with the same AppVersion string.

## Step 2 — commit (stage ONLY the change + AppVersion; not settings.json/.gitignore)

```powershell
git add VPNRouter.Core/AppVersion.cs <your changed files>
git commit -m "feat(vX.Y.Z-rN): <one-line summary, <=72 chars>"
```
The commit body should explain WHY. **The message MUST end with the trailer:**
```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```
The commit-msg hook enforces subject <=72 chars + conventional prefix
(feat/fix/docs/refactor/test/chore/ci/build) + the Co-Authored-By trailer. Do NOT
use `--no-verify` / `--no-gpg-sign`. If the pre-commit build gate errors ONLY on
`tools/VpnRouterTestMcp` "file in use", that's a locked dev tool, not the app — the
gate still passes; the release build does not touch it.

## Step 3 — push BOTH remotes

```powershell
git push origin HEAD:main
git push forgejo HEAD:main      # if VPN down, retry later; GitHub is canonical
```

## Step 4 — build + upload Windows artifacts (creates the tag + GitHub release)

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "X.Y.Z-rN" `
  -SingBoxPath "publish\sing-box-lx.exe" -BundleSplitDriver -Upload
```
This tags `vX.Y.Z-rN` on HEAD, creates the release (build.ps1 sets `--latest` — you
undo that next), uploads the 4 Windows assets, and the tag push triggers Mac + Linux
CI. Verify the log shows: `AppVersion match`, `Copied from: publish\sing-box-lx.exe`,
`Bundled split-tunnel driver (3 files, sha256 gate OK)`.

## Step 5 — write release notes

`plans/release-notes-vX.Y.Z-rN.md` — summary of the change + a short test flow for
the user. No emoji anywhere (project rule).

## Step 6 — finalize the release

```powershell
gh release edit vX.Y.Z-rN --repo PavelLizunov/VPNRouter --prerelease `
  --title "VPNRouter vX.Y.Z-rN - <headline>" --notes-file plans/release-notes-vX.Y.Z-rN.md
gh release edit v2.45.0 --repo PavelLizunov/VPNRouter --latest         # restore stable as Latest
gh release delete vX.Y.Z-r(N-1) --repo PavelLizunov/VPNRouter --yes    # delete previous candidate (tag kept)
```
(Only one in-flight prerelease should be visible at a time.)

## Step 7 — verify

```powershell
gh release view vX.Y.Z-rN --repo PavelLizunov/VPNRouter --json assets --jq '.assets|length'
# 4 right after build; 14 once Mac+Linux CI finishes (4 Win + 4 Mac + 6 Linux)
gh api repos/PavelLizunov/VPNRouter/commits/<HEAD-sha>/check-runs `
  --jq '.check_runs[]|select(.conclusion=="failure")|.name'          # must print nothing
```
Wait for Mac+Linux CI to finish -> 14 assets, 0 failures. If `test` fails with a
Linux hash drift, bump `PinnedHashLinux` in
`VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs` to the "Actual:" hash
from the failed job, commit as `chore(tests):`, push, re-verify. Never leave a red X
on main.

## Hard rules

- AppVersion string == tag, including `-rN`.
- Push both remotes; never `--no-verify` / `--no-gpg-sign`.
- Keep the split-driver + lx-core bundle flags on every Windows build.
- Restore `v2.45.0` as Latest after each candidate.
- **Do NOT cut a stable `vX.Y.Z` (no suffix).** Stable promotion is NOT autonomous —
  it requires an explicit user command ("cut" / "ok" / "promote") and a separate
  checklist (live-update gate). Only ship `-rN` candidates here.
