# Damaged geo rule-set file stops every connect (hotfix after stable v2.50.2)

## Why

Owner report 2026-10-11: for one Windows user VPNRouter "does not work at all". Two diagnostics bundles of that
machine (stable 2.50.2, Windows build 26100, generated 16:53 and 17:07 local time) show the same line on every
connect attempt, from the Windows service and from the desktop app:

`FATAL create service: initialize router: parse rule-set[2]: invalid sing-box rule-set file`

followed by `sing-box crashed (exit code: 1)` and "sing-box failed to start within 5 seconds".

## Facts checked (2026-10-11)

- `rule-set[2]` in the loaded config of both bundles is `vpnrouter-geosite-ru` (order: adblock, geoip-ru, geosite-ru);
  the index is the zero-based position in `route.rule_set` (`route/router.go` of the pinned core `v1.14.2-vpnctl.2`).
- The message has one origin in that core: `common/srs/binary.go` `Read` returns it when the first three bytes of the
  file are not `SRS`. So the file on that machine is not a rule-set any more.
- The bundle lists `geosite-ru.srs` as 155 bytes, written 2026-05-22 together with `geoip-ru.srs`. The current upstream
  file (`SagerNet/sing-geosite`, branch `rule-set`, `geosite-tld-ru.srs`) is also exactly 155 bytes and is a valid
  version 1 rule-set. Same length, wrong content: the file was damaged on disk (the bundle cannot show how; the app log
  reports unclean shutdowns on that machine).
- `GeoDataDownloader.AreGeoFilesAvailable` and `EnsureFileAsync` accept a file by size only (100 bytes for geosite), so
  the damaged file counts as present. `StartupPipeline.PreflightGeoDataAsync` downloads only when the files are
  "not available", so nothing ever replaces it, and `ConfigGenerator` and `CustomConfigInjector` keep referencing it.
  The connection stays broken until the user deletes the file or turns the Russian bypass off.
- The health check of both bundles reports 0 errors; the geo manifest shows only size and date.
- Before the update that day another client (nekobox) was running and the service refused to start with the expected
  conflicting-VPN message. That is a separate, intended refusal and not part of this defect.

## What

1. `RuleSetFile.IsValidBinary`: a binary rule-set is usable when it starts with `SRS`, has a version the pinned core
   reads (1-5), its zlib payload inflates to the end and the trailing Adler-32 matches the inflated bytes (so a zeroed,
   garbled or cut-off body is caught too).
2. `GeoDataDownloader`: `AreGeoFilesAvailable` requires that check for both files; `EnsureFileAsync` deletes and
   re-downloads an existing damaged file and refuses to install a downloaded one that fails the check.
3. Result on the affected machine: the next connect sees the damaged file, downloads a fresh copy and starts; when the
   download is not possible the config is generated without the Russian bypass (existing fallback) instead of crashing.
4. Version `2.50.3-r1`.

Out of scope (ledger entries): the same size-only trust in `RuleSetCacheManager` (adblock and custom rule-sets), the
weekly geo refresh that is unreachable from the startup path, diagnostics that do not show a damaged rule-set.

## Invariants

No change to the generated config when the files are intact. A read error (locked file, I/O failure) never deletes a
file: only content that fails the format check does. No URL or secret in the new log lines. The pinned core stays
`v1.14.2-vpnctl.2`.

## Verification plan

- Unit tests (`RuleSetFileTests`, `GeoDataDownloaderDamagedFileTests`): the real upstream 155-byte file is accepted; a
  zero-filled file of the same size, an HTML body and a file with a valid header and a zeroed body are rejected; a
  damaged geosite makes `AreGeoFilesAvailable` false and `ConfigGenerator.Generate` emit no geo rule-set; the downloader
  replaces a damaged file and rejects a damaged download.
- Existing tests that used zero-filled stand-ins for geo files move to valid ones.
- PR CI (ubuntu and Windows jobs) at the exact head; Release build and full suite on the build worker before a tag.
- Independent read by a second model family.
- After a candidate: on WINBRAT overwrite `geosite-ru.srs` with zeros of the same length, connect, expect the repair
  line in the log and a running core.

## Unknowns

- How the file got damaged on that machine. Not needed for the repair.
- Whether .NET reports a cleanly truncated zlib stream. The check does not rely on it: it compares the trailing
  checksum itself (the two published geo files end exactly at that checksum, verified 2026-10-11).

## Rollback

Revert the PR: the validator has no other callers.

## Outcome

Pending.
