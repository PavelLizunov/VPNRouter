# H-58: characterization tests for the Zapret parsing code

## Why

Zapret (DPI bypass without a VPN) is the least tested feature and holds most of the remaining long functions. It is
built on Flowseal's `zapret-discord-youtube`, which itself wraps bol-van's `zapret` (`winws.exe` on WinDivert):
each release ships several `general*.bat` strategy files, a `service.bat`, `utils/targets.txt` and lists. VPNRouter
turns those files into data (winws arguments, an ordered strategy list, the installed version, probe targets), and
only two cases of that code were tested. Any later refactor of the probe or start flow needs this layer pinned first.

## What

`VPNRouter.Tests/ZapretParsingTests.cs`, 22 tests, no production change:

- `ZapretUpdater.ExtractWinwsArgsFromLines`: a realistic multi-line `general.bat` (path placeholders resolved, game
  filter ports removed with their commas, a segment that only carried the game filter dropped), no `winws` line,
  empty input, game filter inside a list, case-insensitive placeholders, doubled backslashes, only the first command.
- `ZapretUpdater.ParseStrategies`: missing directory, the sort order (ALT3, general, ALTn, others, SIMPLE, FAKE TLS),
  placeholders and bat path kept, files without `winws` and non-`general*` files skipped.
- `ZapretUpdater.GetLocalVersion`: none, `version.txt` first and trimmed, `service.bat` header fallback, header
  beyond the first five lines ignored.
- `ZapretManager.BuildLegacyArgs`: both presets, custom port, unknown strategy.
- `ZapretAutoStrategy.LoadTargets`: defaults, filtering (comments, PING, non-http), the twelve-target cap, fallback.

Behaviour recorded as found, not judged: for example `WINWS.EXE` in capitals is not detected on the command line,
and an unusable `targets.txt` silently falls back to the built-in targets.

## Verification

Exact-head CI (the tests run on Windows and Linux). Expected values for the realistic sample were computed with an
independent port of the algorithm and checked by hand against the code.

## Outcome

Merged after green exact-head CI.
