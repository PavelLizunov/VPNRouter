# R-18: design candidate v2.50.0-r18

## Why

The owner asked (2026-10-02) to check every page of the app in every size for fit, breakage, symmetry and design errors. Since the r17 tag, main carries D-11 (contrast, #466),
the audit tooling (#468), D-12 (token text boxes, compact main tabs, servers link row, update/rules texts, #469), D-13 (the fixed width rule for the compact tabs, #470), D-14
(dark success button text, success text contrast, stacked rule combos, #472) and the evidence notes of the owner's crashes folder (#471). The owner's standing mandate covers the release.
This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r18` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate
   (18 assets, integrity, strict CI), `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, plus a fast-click run on the deployed build.

## Not covered

Stable cut, the previous-stable to candidate live-update gate; Servers and Subscribe redesign; real Windows font rendering (the audit probe uses Linux fonts); only the first level of
inner tabs was audited; the service/GUI ownership problem (ledger WIN-SERVICE-GUI-MULTI-OWNER); Android on a Pixel with Android 16; the real update that confirms the driver-lock fix.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Pending.
