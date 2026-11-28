# Standard desktop assistant routing commands

## Intent

Expose the assistant input on Simple Home and let a subscriber request that games or Russian sites use the direct route. Show the actual selection, routing mode and effect before applying a change. Use provider-independent RU/EN text.

## Implementation

- Start from the r7 release at 0e57451b. Reuse the desktop routing settings, confirmation, session guards and undo paths.
- Resolve game executables from supported local data. Preview the detected scope and existing exceptions. Use the existing Russian traffic setting and describe its actual coverage.
- Keep the online command contract compatible. Preserve local ownership of the candidate, preview and apply operation.
- Capture synthetic, comparable before/after screenshots including the two requested examples.

## Verification

The r8 screenshot follow-up will keep the native diagnostics expander and show its two directions immediately. Check the rendered header after opening and closing, and capture both states on Windows at 380 px.

1. Build the changed client in hosted CI.
2. Test parsing, both route changes, empty results, cancellation and undo.
3. Check both languages and align the user-visible descriptions with the generated rules.
4. Review mode transitions, custom rules, concurrent edits and exact process casing.
5. Inspect narrow-window captures from the base and candidate.
6. Verify the public ViewModel characterization and the existing assistant regression suite.

Routing has user-visible consequences. Rollback is a revert of the feature commit. Installed acceptance belongs to a separately shipped candidate.

## Outcome

The initial Windows test captured the collapsed r7 panel and its expanded state on the same 380 px Russian Simple Home at 01967676. All seven checks passed. The new layout exposes an empty free-text field with Send beside the input, supports Enter, and keeps confirmation, cancellation and undo in the panel. The game and regional routing verification follows this UI block.

The two local commands use installed Steam executable names and the existing regional bypass. Before confirmation the panel names the applicable scope and the resulting routing mode. The accepted tunnel must satisfy the operation-specific route check inside the engine transaction before saving. Candidate tests exercise typed requests, unchanged settings before consent, undo and Windows screenshots of both proposals.

The first integrated source at b99c88e9 passed all nine PR checks, including 55 assistant routing regressions. Windows CI captured both proposals with the confirmation controls visible at 380 px. Independent review requested nested Steam layouts, live evidence for already-saved settings, clearer disclosure and scan errors, filtering of inactive legacy exceptions, editable exclusion rows, and tests for pointer input and transaction rejection.

The follow-up searches common nested game binary directories, skips support and reparse entries during enumeration, and bounds the search to three levels and 256 visited directories per installed title. The confirmation uses the generator's effective exclusions. The app offers reapplication when saved and live rules differ, and checks the proposed routes before replacing the current session as well as before saving. An exclusion row can be removed without deleting the same name from the include list. Stored executable names are preserved for imported custom rows when they are the configured process identity. Tests cover a pointer click on Send, local and online screen-reader notices, unavailable libraries, nested executables, a failed confirmed regional download and transaction rejection with the original process retained.
