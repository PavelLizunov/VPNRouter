# Update reliability strategy

**Trigger**: user complaint 2026-04-29: «Очень важно продумать стратегию,
чтоб в будущем не ломалось обновления, ты уже раз 3 приходишь к тому
что ломаешь обновления, за пару месяцев разработки».

User is right. Update reliability is a recurring failure mode. Every
broken update wave costs trust and (per user's words) "половину
пользователей отвалится". This document captures the recurring failure
patterns + the systemic fixes that prevent them.

## Failure history (months 1-3)

| Cycle | What broke | Why it shipped |
|---|---|---|
| **v2.22.x (Linux)** | `setsid --fork` with `RedirectStandardOutput=true` → SIGPIPE on child after parent exit → relaunch died silently | Tested cold install; never tested upgrade from previous version. |
| **v2.28.7 → v2.29.0-r1..r5 (Windows)** | `ApplyUpdateWindows` silent file-copy failures on locked DLLs → mixed-version DLL set on relaunch → app shows old version | Tested locally on the dev VM where files are seemingly unlocked; never tested from a fresh install of the previous stable. |
| **v2.29.0-r1..r5 dev cycle (meta)** | Local `build.ps1` ran against MAIN repo's working directory (still on v2.28.7) while my code lived in the worktree → r1-r5 binaries were all literally v2.28.7 with fake `-Version` tags. None of the v2.29 changes were in the shipped Windows binaries. | No build-time check that AppVersion.cs matches `-Version` CLI arg. |

Common thread: **never tested "upgrade from previous shipped version
to candidate" before tagging the release.** Every regression would
have been caught by one manual smoke-test run.

## Layered fixes (priority order)

### Layer 1 — fail loud on version mismatch (P0, ~30 min)

`build.ps1` runs at the *start* of the build:

```powershell
$cliVersion = $Version  # from -Version arg
$srcVersion = (Get-Content "$Root\VPNRouter.Core\AppVersion.cs" |
               Select-String 'public const string Version =' |
               ForEach-Object { ($_ -replace '.*"(.+)".*', '$1') }) | Select-Object -First 1

if ($cliVersion -ne $srcVersion) {
    throw "ABORT: -Version '$cliVersion' does not match AppVersion.cs '$srcVersion'. " +
          "Either bump AppVersion.cs to '$cliVersion' or run build with -Version '$srcVersion'. " +
          "(If you're working in a worktree, ensure you've pulled main repo to latest before invoking build.ps1 from main repo path; OR run build.ps1 from the worktree path directly.)"
}
```

This single check would have caught the v2.29.0-r1..r5 fake-tag fiasco
on commit #1.

### Layer 2 — pre-ship smoke test (P0, ~2 hours setup, then 30 sec per release)

Local PowerShell script `tools/smoke-update.ps1`:

```powershell
# Smoke-test the auto-update flow before tagging a release.
# Usage: .\tools\smoke-update.ps1 -PreviousVersion 2.28.7 -CandidateVersion 2.29.0-r6

param([string]$PreviousVersion, [string]$CandidateVersion)

# 1. Download previous stable install ZIP from GitHub Releases.
# 2. Extract to a TEMP install dir (NOT the dev box's real install).
# 3. Stage the candidate's update ZIP locally (build.ps1 produces it).
# 4. Spawn the previous-stable VPNRouter.App.exe with --update-staged-path
#    (need a small CLI hook in App.cs: if env var $VPNROUTER_TEST_STAGED is
#     set, treat its value as the already-extracted update dir and run
#     ApplyUpdate against it without doing an HTTP download).
# 5. Wait up to 60 s for relaunch.
# 6. Read the new VPNRouter.App.exe's version (via PE metadata or via
#    invoking it with --version CLI arg — needs a small CLI hook too).
# 7. Assert version == $CandidateVersion.
# 8. Cleanup TEMP install dir.

# Exit 0 = update flow works. Exit 1 = broken — DO NOT TAG RELEASE.
```

Same flow on Mac (sh script) + Linux (sh script). Each ~2 hours of
initial setup, then 30-60 sec per release verification.

**Run `smoke-update.ps1` as part of `ship-rolling-candidate` skill
before `gh release create`.** Block release creation if smoke fails.

### Layer 3 — fail loud on file-copy errors (P0, ~15 min, applied retroactively to ApplyUpdate*)

Pre-r5 ApplyUpdateWindows had:

```csharp
catch (IOException) {
    var bakPath = destPath + ".bak";
    try { File.Delete(bakPath); } catch { }
    try { File.Move(destPath, bakPath); } catch { }
    try { File.Copy(srcFile, destPath); } catch { }
}
```

Three nested empty catches. ANY failure was invisible.

Replace with:

```csharp
catch (IOException ioEx) {
    var bakPath = destPath + ".bak";
    try { File.Delete(bakPath); } catch { /* ok if missing */ }
    try { File.Move(destPath, bakPath); }
    catch (Exception moveEx) {
        throw new InvalidOperationException(
            $"Update failed: cannot rename locked file {destPath}: {moveEx.Message}", ioEx);
    }
    try { File.Copy(srcFile, destPath); }
    catch (Exception copyEx) {
        // Try to restore .bak so we don't leave the install in a half-replaced state.
        try { File.Move(bakPath, destPath); } catch { }
        throw new InvalidOperationException(
            $"Update failed: cannot copy {srcFile} to {destPath}: {copyEx.Message}", ioEx);
    }
}
```

Surface failure to the UI as an `Update Failed` dialog. Better to have
the user reinstall manually than silently lie about success.

### Layer 4 — verify staged binary version matches expected (P1, ~30 min)

Before kicking off the file copy in `ApplyUpdate*`, read the staged
`VPNRouter.Core.dll`'s `AppVersion.Version` constant (via PE metadata
reflection — `Assembly.LoadFile(stagedDll).GetType("VPNRouter.Core.AppVersion").GetField("Version").GetValue(null)`).
If it's same or older than the currently running version, abort with:

> Downloaded update reports version X, current version is Y. This is
> the same or older — refusing to apply. Re-check the release page;
> the auto-update may have downloaded a stale or wrong asset.

This catches the "v2.29.0-r5 zip actually contains v2.28.7 binary" bug
at apply-time, even if smoke-test wasn't run.

### Layer 5 — separate "compile" from "package" (P2, ~2 hours)

`build.ps1` currently does:
1. dotnet publish → produces binaries
2. zip up binaries

Split into two phases with an explicit handoff:

1. **compile phase**: `tools/compile.ps1` — produces `dist/` with
   binaries. ASSERTS at end: AppVersion.cs Version == filename of one
   of the produced exe metadata fields == git current tag.
2. **package phase**: `tools/package.ps1` — takes a `dist/` from any
   source, packages into install + update zips. ASSERTS the packed
   files are from the requested $Version.

This forces the version-consistency check at the boundary. No more
"compiled v2.28.7, packaged as v2.29.0-r5".

### Layer 6 — never silently filter file-copy failures in updater (P2, ~30 min)

Even with Layer 3 fixed at the throw level, log every file copy with
result. On final tally, if any file failed, surface to UI:

> Update partially applied: N of M files updated successfully, K failed.
> The application may be in an inconsistent state. Please download and
> reinstall manually from {release URL}.

User then clicks a button to open the release page and fixes themselves.
Beats fake "Update successful" pop-up.

### Layer 7 — telemetry / install receipts (P3, ~4 hours)

(Already partially implemented for Linux — `vpnrouter-update.log`,
`.update-installed-version` receipt.)

On Windows:
1. Before applying update, write a receipt at `%LOCALAPPDATA%\VPNRouter\update-receipt.json`
   recording `{tried_at: "...", tried_version: "vN+1", from_version: "vN"}`.
2. On next normal startup, if `tried_version` is set and the running
   binary's version is `<= from_version` → update FAILED. Surface UI banner
   "Last update attempt didn't take effect — please update manually" with
   link to release page. Clear the receipt only when running version
   actually exceeds `from_version`.

This catches silent-success-but-actually-failed updates even if all
above layers fail. Plus gives us telemetry: how often does it happen
in the wild?

### Layer 8 — CI integration test on Windows runner (P3, ~1 day)

GitHub Actions workflow `update-smoke.yml`:
1. Trigger on every release tag push.
2. Spin up a `windows-latest` runner.
3. Download previous stable install ZIP.
4. Extract.
5. Run an `update-from-staged.ps1` script that triggers the auto-
   update flow against the just-built candidate update ZIP (uploaded
   as a workflow artifact).
6. Verify the relaunched binary reports `vN+1`.
7. If anything fails → mark the release as failed (post a comment on
   the release page, optionally auto-yank the prerelease).

Mac and Linux equivalents: similar runners.

This is the gold-standard fix but requires the most work. P3 because
it doesn't replace Layers 1-3, just adds defense in depth.

## Implementation roadmap

| Layer | Effort | Phase |
|---|---|---|
| 1. AppVersion.cs match check in build.ps1 | 30 min | r7 (immediate) |
| 2. Local smoke-update.ps1 + integrate into ship-rolling-candidate skill | 2 h | r7-r8 |
| 3. Fail-loud on file-copy errors in ApplyUpdate* | 15 min | r7 (immediate, sibling to bootstrap fix) |
| 4. Verify staged binary version matches expected | 30 min | r8 |
| 5. Separate compile / package phases | 2 h | v2.30 minor |
| 6. Per-file copy logging + UI surface on partial failure | 30 min | r7 (with #3) |
| 7. Windows install receipts + UI surface | 4 h | v2.30 |
| 8. CI integration test on Windows runner | 1 day | v2.31 |

**v2.29.0-r7 must include Layers 1, 3, 6** before user gets r6 to test.
Otherwise we risk shipping yet another broken update flow.

## What NOT to do

- **Don't ship "fixed" updaters that only help future updates.** v2.29.0-r5
  fell into this trap. The fix was for r5+ users, but the broken users
  were on v2.28.7 and couldn't reach r5. Always think: "the broken
  binary is what runs the update — what does my fix do for THAT
  binary?"
- **Don't trust local-machine testing alone.** Files unlocked here
  may be locked elsewhere; admin/UAC differs per machine; AV product
  differences in lock semantics. Smoke-test from-fresh-install or it
  didn't happen.
- **Don't `try { } catch { }` in updater code.** Silent failure here
  is the worst possible UX — user thinks update worked, comes back
  later wondering why bug X is still there. Fail visibly + give a
  clear recovery path.

## Cross-references

- `plans/release-notes-v2.29.0-r6.md` — the bootstrap fix that
  rescues the user base from the broken v2.28.7 → v2.29.0-r5 cycle.
- `CLAUDE.local.md` — release process. Should add Layer 1+2 checks
  to the canonical ship checklist.
- `.claude/skills/ship-rolling-candidate/SKILL.md` — invoke
  smoke-update.ps1 before tagging.
