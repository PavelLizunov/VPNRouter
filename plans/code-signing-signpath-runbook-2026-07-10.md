# Code-signing runbook — SignPath.io Foundation (VPNRouter Windows)

Closes the last OPEN-DEFECTS P1 ("disappears after a reboot" = AV quarantine of
UNSIGNED binaries that do TUN/process-scan/firewall). Recommendation + rationale:
`plans/research-seamless-update-findings-2026-07-09.md` §4. Cost: **$0** (SignPath
Foundation is free for OSS). VPNRouter qualifies: public repo + GPL-3.0 + verifiable
builds.

The CI half is **already landed** and inert: `.github/workflows/sign-windows.yml`
(manual `workflow_dispatch`, no-op until the four secrets exist). What remains is
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
   `VPNRouter.App.exe`, `VPNRouter.GUI.exe`, `VPNRouter.CLI.exe`,
   `VPNRouter.Service.exe`, `VPNRouter.Core.dll`, and `sing-box.exe` (the bundled
   lx core — it is built from source in our repo, so it is eligible).
   - `mullvad-split-tunnel.sys` is already WHQL/Mullvad-signed — do NOT re-sign.
   - Upstream-downloaded binaries (none in the bundle today) would be ineligible.
4. Create a **CI user** + its **API token**.

## Step 3 — Add the four repo secrets (owner)

GitHub → repo Settings → Secrets and variables → Actions → New repository secret:

| Secret | Value (from SignPath) |
|---|---|
| `SIGNPATH_API_TOKEN` | CI user API token |
| `SIGNPATH_ORGANIZATION_ID` | organization GUID |
| `SIGNPATH_PROJECT_SLUG` | `vpnrouter` |
| `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` |
| `SIGNPATH_ARTIFACT_CONFIG_SLUG` | `windows-zip` |

(Adding `SIGNPATH_API_TOKEN` is what flips `sign-windows.yml` from its
"secrets not configured" guard to active.)

## Step 4 — Sign a release (per ship, right after build.ps1 upload)

The Windows ZIPs are built locally by `build.ps1` and uploaded to the release.
After that upload, run the signer exactly like `sign-android`:

```
gh workflow run "Sign Windows (SignPath)" -f version=2.47.0-r6
gh run watch <run-id> --exit-status
```

It downloads the unsigned `VPNRouter-vX.Y.Z-win.zip` + `-update-` ZIP, submits
each to SignPath (→ **approve the request in the SignPath UI**), downloads the
signed ZIPs, recomputes the `.sha256` sidecars, and re-uploads with `--clobber`
(same tagged URLs). Then re-run the live-update gate (`cut-stable` §6.5) on the
signed artifacts.

## Step 5 — Verify

```
# after download of the signed ZIP + extract:
powershell -c "Get-AuthenticodeSignature .\VPNRouter.App.exe | fl Status,SignerCertificate"
# Status must be 'Valid', signer = your SignPath cert (was 'NotSigned' before).
```

## Notes / caveats (from the research)

- Signing does NOT instantly clear SmartScreen — reputation accrues over clean
  downloads (weeks+); EV no longer bypasses it either (2024). But signing
  (1) stops the AV false-quarantine that causes the "disappears after reboot"
  reports, (2) is the prerequisite for reputation, (3) makes the updater/service
  trusted.
- Wire signing into the ship SKILLs once live: add Step 4 to
  `ship-rolling-candidate` (after the build.ps1 upload) and `cut-stable`
  (before the live-update gate). Left out of the skills until enrollment so they
  don't reference a non-working step.
- Azure Trusted Signing was rejected: geo-blocked to US/Canada individuals
  (research §4). SignPath Foundation is the path.
```
