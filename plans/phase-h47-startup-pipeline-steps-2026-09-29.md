# H-47: split StartupPipeline.ResolveProfileAndServersAsync into steps

## Why

`ResolveProfileAndServersAsync` (173 lines) validated the custom config or resolved the servers, loaded and merged the
profile catalogue, chose the active profile (full tunnel, named profiles, custom config, safe mode) and added the
user's custom apps, in one body with three cancellation checks between the parts.

## What

The method keeps the mode bookkeeping, the three `ct.ThrowIfCancellationRequested()` calls, the status lines and the
final `SetActiveProfile`, and calls, in the original order: `ResolveServerSource` (custom config validation or server
resolution, returns the raw custom JSON), `LoadProfileCatalogueAsync` (quarantine, sources, load, safe-mode notes,
user customisation merge, log), `SelectActiveProfile` (the full-tunnel / named / custom / default decision incl. the
safe-mode override of the routing mode and the sanitising of unknown profile names) and `AddCustomAppsToProfile`.
The statements moved unchanged; a multiset comparison of the trimmed lines of the file before and after shows
nothing removed and only method headers, parameter lines, braces, the four calls and two `return` lines added.

## Verification

Exact-head CI (full suite, including `StartupPipelineTests` and the safe-mode test added in H-19); read of the diff.

## Outcome

Pending CI.
