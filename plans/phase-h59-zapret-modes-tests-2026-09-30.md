# H-59: characterization tests for the Zapret file switches

## Why

Second step of the Zapret test plan (after H-58). Flowseal's `service.bat` keeps three switches in files, and
VPNRouter reads and writes the same files so the two stay in step: `utils\game_filter.enabled` (which ports the
"game filter" adds), `lists\ipset-all.txt` with a `.backup` (which IP set the filter uses) and
`utils\check_updates.enabled`. `ZapretActions` had tests for its service and netsh helpers but none for these.

## What

`VPNRouter.Tests/ZapretModesTests.cs`, 15 test methods, no production change:

- game filter: no file, set and read back all three modes, off deletes the file, case and whitespace tolerance, an
  unknown word means off while the file still counts as configured;
- ipset: classification of the list content (missing or empty or blank is `Any`, the sentinel `203.0.113.113/32` is
  `None`, anything else is `Loaded`), loaded to none and to any make a backup, the none/loaded round trip restores the
  list, `Loaded` without a backup does nothing, any to none makes no backup, setting the current mode touches nothing,
  a newer backup replaces an older one;
- update-check flag: default off, enable writes `ENABLED`, disable deletes, both idempotent.

Also fixes the test count in the H-58 brief (21, not 22).

## Verification

Exact-head CI (Windows and Linux). The tests use a temporary data directory like the neighbouring Zapret tests.

## Outcome

Merged after green exact-head CI.
