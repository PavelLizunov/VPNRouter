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

Merged as PR #573 (squash, main `52eca5d1`), candidate `v2.50.3-r1` published 2026-10-11 as a prerelease.

Files: new `VPNRouter.Core/Services/RuleSetFile.cs` (72 lines); `GeoDataDownloader.cs` (validity in
`AreGeoFilesAvailable` and `EnsureFileAsync`, one-step replace of the destination); tests `RuleSetFileTests`,
`GeoDataDownloaderDamagedFileTests`, `RuleSetSamples`, one new case in `ConfigGeneratorTests`; two existing tests moved
from zero-filled stand-ins to valid ones; `AppVersion` 2.50.3-r1.

| check | result |
|---|---|
| PR CI at the head `946be46f` | all green (test, characterization-windows, go-test-windows, grep, compile, test-update) |
| Release build + full suite on `windows-worker` at `946be46f` (same tree as the merge commit) | 0 errors, 229 warnings; 3396 passed, 0 failed, 19 skipped (v2.50.2-r1: 3369) |
| worker preflight before that run | WINBRAT, CPU 0-10 % of 8, 14.1 of 16 GB free, 117 GB disk, no build process, SDK 10.0.301 |
| tag workflows at `v2.50.3-r1` (macOS, Linux, Android, `dotnet test`, Windows update test) | all success |
| draft and published integrity, strict commit gate, `check-open-p0.ps1` | success; `OK`, 8 green; `OK: no open P0/P1` |
| release event: APT, integrity, Windows update test | success |
| `post-ship-local` on WINBRAT | PASS, 2 cold cycles |

Reproduction and live proof on WINBRAT (the app there had `bypass_russian_traffic: true`; its healthy
`geosite-ru.srs` is 155 bytes starting with `SRS`, written 2026-08-02 and never refreshed):

- Build of `07097020` (the v2.50.2 tree), geosite overwritten with 155 zero bytes, one Connect through the UI: the same
  line as in the user's bundles (`parse rule-set[1]: invalid sing-box rule-set file`; index 1 because ad blocking is
  off there), `sing-box crashed (exit code: 1)`, `Failed to start VPN`, the file stays damaged.
- Candidate, same damage, one Connect: `[GeoData] geosite-ru is damaged (155 bytes, not a readable rule-set) -
  re-downloading`, `geosite-ru downloaded (155 bytes)`, core started, connected in 3.6 s, the public IP of routed apps
  changed, disconnect clean, the file starts with `SRS` again. The stale `geoip-ru.srs` (70 days) was refreshed in the
  same pass.
- Live update: v2.50.2 stable deployed, geosite zeroed, updater to `2.50.3-r1` PASS (helper done, version matches,
  receipt consumed), 2 cold cycles PASS, the file repaired, cleanup PASS.

Independent read by a second model family (gpt-sol through `xmodel.sh`, two passes). First pass: DO NOT SHIP, because
the check trusts an intact zlib stream and does not parse the rule structure, a version byte above 5 passed, a stream
cut short could pass (the runtime does not report it), the old copy was deleted before the move, and the generated test
stand-in was called valid. Fixed in the second commit: version 1-5 only, trailing Adler-32 compared, one-step replace,
honest name for the stand-in, new cases. Left open in the ledger as P3: `RULESET-STRUCTURE-NOT-PARSED`,
`GEO-DOWNLOAD-TWO-PROCESS-RACE` (plus `RULESET-CACHE-NO-CONTENT-CHECK`, `GEO-REFRESH-UNREACHABLE`,
`DIAG-RULESET-NOT-SURFACED` from the diagnosis). Second pass on the final files: SHIP, no blocking defect.

Surprises: the first worker run died after the preflight line with an empty checkout (the fetch did not complete; a
manual fetch worked) and its waiter kept the lock, removed by hand; another project's job was using the worker, so the
release build waited for it and the desktop checks were agreed with that session.

Not verified: macOS, Linux and Android on real devices; a download failure in the field (covered by unit tests only);
signed Windows files.
