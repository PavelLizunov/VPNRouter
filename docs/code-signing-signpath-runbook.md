# Code-signing runbook — SignPath.io Foundation (VPNRouter Windows)

Closes the last OPEN-DEFECTS P1 ("disappears after a reboot" = AV quarantine of
UNSIGNED binaries that do TUN/process-scan/firewall). Recommendation + rationale:
`plans/research-seamless-update-findings-2026-07-09.md` §4. Cost: **$0** (SignPath
Foundation is free for OSS). VPNRouter qualifies: public repo + GPL-3.0 + verifiable
builds.

The CI half is **already landed** and inert: `.github/workflows/sign-windows.yml`
(manual `workflow_dispatch`, fails fast until all five secrets exist). What remains is
the **enrollment**, which only the repo owner can do (account + OSS application —
not automatable). These are the exact steps.

## Step 1 — Apply to SignPath Foundation (owner, ~15 min + wait for approval)

1. Go to https://signpath.org/ (Foundation, EU) → "Apply for the OSS program"
   (or via https://signpath.io/solutions/open-source-community).
2. Submit `PavelLizunov/VPNRouter`: public, GPL-3.0, has releases, builds
   verifiably from source (Win EXEs via `build.ps1`; `sing-box-lx.exe` via
   `tools/build-singbox-lx.ps1` from pinned commits — now HEAD-pin-asserted).
3. Manual approval (days). Approval creates your **organization**.

## Step 2 — Create the SignPath project + artifact config (owner, in SignPath UI)

1. New **Project** slug `vpnrouter`.
2. **Signing policy** slug e.g. `release-signing` (OSS = each release manually
   approved in the SignPath UI — fits our rolling-rN + user-gated cut flow).
3. **Artifact configuration** slug e.g. `windows-zip`: a ZIP configuration that
   signs the PE files inside and repackages. Include (Authenticode):
   `VPNRouter.App.exe`, `VPNRouter.App.dll`, `VPNRouter.GUI.exe`,
   `VPNRouter.CLI.exe`, `VPNRouter.CLI.dll`, `VPNRouter.Service.exe`,
   `VPNRouter.Service.dll`, `VPNRouter.Core.dll`, and `sing-box.exe` (the bundled
   lx core — it is built from source in our repo, so it is eligible).
   - `mullvad-split-tunnel.sys` is already WHQL/Mullvad-signed — do NOT re-sign.
   - Upstream-downloaded binaries (none in the bundle today) would be ineligible.
4. Create a **CI user** + its **API token**.

## Step 3 — Add five secrets and the signer subject (owner)

GitHub → repo Settings → Secrets and variables → Actions → New repository secret:

| Secret | Value (from SignPath) |
|---|---|
| `SIGNPATH_API_TOKEN` | CI user API token |
| `SIGNPATH_ORGANIZATION_ID` | organization GUID |
| `SIGNPATH_PROJECT_SLUG` | `vpnrouter` |
| `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` |
| `SIGNPATH_ARTIFACT_CONFIG_SLUG` | `windows-zip` |

The workflow guard checks all five values before building or modifying a
release. Also add the non-secret Actions variable
`SIGNPATH_EXPECTED_SUBJECT` with the stable identifying part of the certificate
subject issued to VPNRouter. The Windows verification job requires every signed
PE in both ZIPs to have `Status=Valid`, that expected subject and one consistent
certificate thumbprint (18 checks total). Repository secret names can be audited with
`gh secret list --repo PavelLizunov/VPNRouter`; their values remain unreadable.

## Step 4 — Sign a draft release from its immutable tag

Do not upload locally built unsigned ZIPs for signing and do not run the signer
against a public release. After the accepted release commit is on `main`:

1. Obtain explicit release authorization; record the accepted main commit SHA,
   require clean HEAD and matching full AppVersion, then create and push a new
   immutable tag `vX.Y.Z[-rN]`. Verify the remote peeled tag SHA equals that commit.
2. Create the release with `gh release create TAG --verify-tag --draft
   --latest=false` and release notes; add `--prerelease` for a candidate.
   Keep it draft while all platform assets are assembled. An existing tag/draft
   must be inspected, never silently recreated.
3. Run the signer with the release tag as workflow ref:

```
gh workflow run sign-windows.yml --ref vX.Y.Z-rN -f version=X.Y.Z-rN
gh run watch <run-id> --exit-status
```

The workflow checks out `github.sha` on a GitHub-hosted Windows runner, proves
`HEAD == run SHA == remote tag commit` and full AppVersion equals the requested
version, then builds sing-box-lx and both Windows ZIPs
from source, and passes that immutable Actions artifact ID directly to SignPath.
Approve the request in the SignPath UI. Before any release mutation, a separate
Windows job extracts both results and checks all 18 required App/GUI/CLI/
Service/Core/sing-box signatures, certificate subject and thumbprint.

Only verified ZIPs and newly computed sidecars are uploaded, and only while the
release is still draft, after rechecking the remote tag SHA immediately before
upload. If a signature, signer, build identity or draft check
fails, no public release is modified. Uploads never overwrite existing assets;
inspect partial results and explicitly recover only missing exact-tag files,
or STOP if provenance or consistency cannot be proven.

Wait for all platform staging and tag-bound test/update jobs, all 16 canonical
assets and every matching sidecar, then dispatch and await:

```
gh workflow run verify-release-integrity.yml --ref TAG -f tag=TAG -f auto_draft_on_failure=false
```

Strict CI requires `-ReleaseTag TAG` and canonical tag/SHA-bound runs. Publish
only with explicit owner authority: candidate `gh release edit TAG --draft=false
--prerelease --latest=false`; stable `gh release edit TAG --draft=false
--prerelease=false --latest`. APT and full fixed-WINBRAT post-ship verification
follow publication. If token publication did not trigger integrity/APT, dispatch
them explicitly with `--ref TAG -f tag=TAG` (integrity also uses
`-f auto_draft_on_failure=false`). Follow `cut-stable` for explicit postpublish
Homebrew notification; macOS draft staging suppresses the tap event.
Published corrections require a new version/tag, not overwritten assets.

## Step 5 — Verify

```powershell
# optional manual spot-check after downloading the still-draft signed ZIP:
powershell -c "Get-AuthenticodeSignature .\VPNRouter.App.exe | fl Status,SignerCertificate"
# Status must be 'Valid', signer = your SignPath cert (was 'NotSigned' before).
```

## Notes / caveats (from the research)

- Signing does NOT instantly clear SmartScreen — reputation accrues over clean
  downloads (weeks+); EV no longer bypasses it either (2024). But signing
  (1) stops the AV false-quarantine that causes the "disappears after reboot"
  reports, (2) is the prerequisite for reputation, (3) makes the updater/service
  trusted.
- Every release uses draft-first staging. `build.ps1 -Upload` only stages
  unsigned Windows assets to an existing draft/tag at accepted main's exact SHA;
  it cannot create or publish releases. Any SignPath secret or expected-subject
  variable, even partial configuration, blocks that unsigned path. Complete
  enrollment or STOP; there is no unsigned fallback. Stable signing completes
  before the stable post-ship gate; the previous-stable -> candidate gate remains
  mandatory before cutting stable.
- Current status (2026-08-13): none of the five `SIGNPATH_*` repository secrets
  is configured. Windows releases remain unsigned until the owner enrollment is
  approved and this runbook's verification returns `Valid`.
- Azure Trusted Signing was rejected: geo-blocked to US/Canada individuals
  (research §4). SignPath Foundation is the path.
