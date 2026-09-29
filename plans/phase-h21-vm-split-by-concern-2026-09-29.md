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
  everything else stay in `MainWindowViewModel.cs` (5265 to 2616 lines).
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

Pending.
