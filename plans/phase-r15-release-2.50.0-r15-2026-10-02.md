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

Published 2026-10-01 as a prerelease (tag on `01e70644`, 18 assets, integrity, APT and update runs green, strict CI 8 green).

Post-ship on WINBRAT with `tools/post-ship-local.ps1` failed twice and did not pass at the time of this note:

1. Attempt 1: both cold cycles connected and every probe passed, but the whole-run lifecycle check counted one error-level line. The line was an `[INF]` `TcpTlsProbe` entry whose text contained `OperationCanceledException`; the verifier matched the bare word `Exception` at any level. Verifier defect, fixed in the follow-up PR (an exception name on an INF/DBG/VRB line is no longer an error signal; `[FTL]` is now counted).
2. Attempt 2: sing-box crashed 0.5 s after start (access violation inside Windows CryptoAPI via Go `crypto/x509`), the health monitor restarted it, but the 20 s warm-up gave up first, so the UI went back to Connect and the verifier could not find the Disconnect button. Not an r15 UI regression: the app process stayed alive and the render-pass crash did not recur. Recorded as `SINGBOX-WIN-CRYPT32-AV-ON-START` (P2, observed once) in `plans/OPEN-DEFECTS.md`.

Attempt 3 is run with the fixed verifier; its result is added below.
