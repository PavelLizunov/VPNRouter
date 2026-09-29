# H-21: split MainWindowViewModel.cs by concern (step 1 of the refactor)

## Why

`MainWindowViewModel.cs` was 5265 lines: the residue that earlier partial extractions did not
take. About 2900 lines of it are three self-contained concerns (Zapret tool tab, Telegram proxy
tab, custom rules editor). This is the first, mechanical step: make each concern its own file
without touching behavior, so later steps (extract testable controllers) have clear seams.

## What

- New partials `MainWindowViewModel.Zapret.cs`, `.TgProxy.cs`, `.CustomRules.cs`.
- Moved whole units by script: a member with its attributes, or a whole depth-1 `#if…#endif`
  block. Members are classified by declaration name; the shared field block, constructor and
  everything else stay in `MainWindowViewModel.cs` (5265 lines to about 855 after both steps).
- Step 1b (second commit): the remaining thematic blocks move the same way into new
  `.Apps.cs`, `.ServerList.cs`, `.MacSudo.cs` and into the existing Settings, Connection,
  ThemeAndLogo, Zapret and CustomRules partials (declaration-based classification because some
  property declarations put the brace on the next line). `MainWindowViewModel.cs` ends at about 855 lines.
- Navigation list in `ViewModels/AGENTS.md` updated.

## Not covered

No logic change, no rename, no signature change: the public surface hash does not move and is not
re-pinned. Using directives are copied unpruned. Extracting testable controllers from the moved
code is the next step.

## Verification

- Script check: the multiset of stripped non-blank lines across all `MainWindowViewModel*.cs`
  files is identical before and after, except the three new file headers.
- Windows configuration build and the ViewModel tests (including the characterization hash) on
  `windows-worker` at the exact head SHA; exact-head CI covers the non-Windows configuration and
  the full suite.

## Outcome

Full `VPNRouter.Tests` suite on `windows-worker`, run with no name exclusions:

- Step 1 (a87f4066) and step 1b (1d8bbf4c): 2777 tests, 2753 passed, 17 skipped, 7 failed.
- Baseline `origin/main` (b1679a87) for the same tests: the same 7 failures. Six are
  `PageScreenshotTests` (render-pass exception, present before this change, no CI job runs them;
  see ledger PAGESCREENSHOT-RENDER-INVALIDATION) and one is
  `Restart_DoesNotSpawnUntilQueuedRemovalCompletes` (fixed separately in H-19).
- Targeted run of the ViewModel, Zapret, TgProxy and Autostart tests at a87f4066: 177 of 177
  passed, including the public-surface characterization hash.
- Merged as #357; exact-head CI green.
