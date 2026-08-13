# Cut-stable checklist — mandatory pre-cut live update gate

**Status:** enforcement reference.

**Canonical workflow:** `.agents/skills/cut-stable/SKILL.md`.

**Safety boundary:** all application install, launch, update, connection and
cleanup actions run only on the fixed WINBRAT test VM. Never use the developer
workstation as a fallback.

## Why this gate exists

Builds and unit tests do not exercise the exact user upgrade path. v2.31.7
demonstrated that a release can compile successfully while `helper.cmd` breaks
all upgrades. Therefore the previous stable binary must update to the exact
candidate before a stable tag is created.

## Preconditions

- Release commit matches the candidate tag and the checkout is clean.
- Release build, regression tests and exact-SHA CI are green.
- macOS, Linux, Android and Windows update workflows are green.
- The candidate contains exactly 16 canonical assets with valid SHA sidecars.
- `tools/post-ship-verify.ps1` passed on the candidate.
- No open P0/P1 entry remains in `plans/OPEN-DEFECTS.md`.

## WINBRAT live-update gate

1. Resolve the latest stable release and download its full Windows ZIP plus
   `.sha256` sidecar. Recompute and compare SHA256.
2. Verify the immutable target identity:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action identity
   ```

   The result must be `WINBRAT @ 100.115.182.0`.
3. Deploy the previous stable from the verified root-level ZIP:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 `
     -Action deploy -Version <baseline>
   ```

4. Exercise the real updater to the candidate:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 `
     -Action liveupdate -Version <candidate-rN>
   ```

   PASS requires successful helper completion, exact installed semantic
   version, application relaunch, and deletion of the consumed install receipt.
5. Run two complete post-update connection cycles:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-stability.ps1 `
     -Mode ColdCycles -Version <candidate-rN> -Cycles 2
   ```

   Every cycle must connect, prove data-plane routing, disconnect, and pass the
   strict lifecycle/log scan without unexpected restart, wedge, failover or
   health-failure events.
6. Always clean WINBRAT, including failure paths:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-stability.ps1 -Mode Cleanup
   ```

## Failure rule

Any identity, download, SHA, deployment, update, version, receipt, connection,
lifecycle, log, cleanup, CI or asset failure blocks the stable cut. Fix it in a
new `-r(N+1)` candidate and repeat the complete gate.

## PASS report

Record:

- previous stable and target candidate;
- WINBRAT identity result;
- update helper result;
- exact installed version match;
- install receipt consumed;
- two cold connection cycles;
- strict log/lifecycle result;
- cleanup result;
- evidence JSON paths produced by the tools.
