# Standard desktop assistant routing commands

## Intent

Expose the assistant input on Simple Home and let a subscriber request that games or Russian sites use the direct route. Show the actual selection, routing mode and effect before applying a change. Use provider-independent RU/EN text.

## Implementation

- Start from the r7 release at 0e57451b. Reuse the desktop routing settings, confirmation, session guards and undo paths.
- Resolve game executables from supported local data. Preview the detected scope and existing exceptions. Use the existing Russian traffic setting and describe its actual coverage.
- Keep the online command contract compatible. Preserve local ownership of the candidate, preview and apply operation.
- Capture synthetic, comparable before/after screenshots including the two requested examples.

## Verification

1. Build the changed client in hosted CI.
2. Test parsing, both route changes, empty results, cancellation and undo.
3. Check both languages and align the user-visible descriptions with the generated rules.
4. Review mode transitions, custom rules, concurrent edits and exact process casing.
5. Inspect narrow-window captures from the base and candidate.
6. Verify the public ViewModel characterization and the existing assistant regression suite.

Routing has user-visible consequences. Rollback is a revert of the feature commit. Installed acceptance belongs to a separately shipped candidate.
