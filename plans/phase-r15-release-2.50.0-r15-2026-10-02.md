# R-15: hotfix candidate v2.50.0-r15

## Why

The owner's r14 diagnostics showed two defects: the desktop app crashed once when switching to the Applications tab (D-3: a render-pass
invalidation exception, reproduced on WINBRAT through UI automation) and the update/installer stopped on the locked split-tunnel driver file
(D-4: `xcopy exit=4`, "DeleteFile: code 5"). Both fixes are on main. The owner gave a standing mandate to decide the rest without asking.
This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r15` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the
   tag workflows, prepublication gate (18 assets, integrity, strict CI), `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, plus the UI-automation reproduction of the crash (advanced mode, Settings tab, Applications
   tab) on the deployed build: the process must stay alive.

## Not covered

Stable cut, the previous-stable to candidate live-update gate; the driver-lock fix is covered by a contract test only (the worker's driver is not loaded);
the owner's next update is its real check.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Pending.
