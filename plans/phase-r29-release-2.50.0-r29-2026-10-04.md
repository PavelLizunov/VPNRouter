# R-29: security hardening and the repository tidy, candidate v2.50.0-r29

## Why

The owner asked for a full verification of the new version and a release. Since r28 the product changed in four small places, all of them about not leaking or not launching bad input; everything else is documentation, tests, CI and tools. A complete check of the whole product, not only of those lines, is part of the request.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r29` (this PR).
2. Contents since r28:
   - #515 secret redaction without separators: `usersecret`, `userpassword`, `authsecret`, `authtoken` (and their separated forms) in `CrashReporter` and `DiagnosticsRedactor`; the same code is source-linked into Android.
   - #451 the AboutWindow exception log goes through `CrashReporter.ScrubSecrets`.
   - #485 and #483 `TgProxyManager.OpenInTelegram` refuses a port outside 1..65535 and a host or secret that is empty or has spaces, quotes or control characters.
   - Not product: README front page, guide pages, images and clips (#538, #539, #540), the Windows build of the UI renderer in CI, live harness fixes (#536), docs (#537).
3. Verification before the tag, on the exact merged main SHA on `windows-worker`: Release solution build, the full `VPNRouter.Tests` run, the Android Release APK build, PowerShell 5.1 parse of the scripts, `check-open-p0.ps1`, the headless UI audit of every surface in both themes and languages, an independent read of the four product diffs by an external model (`pi`, owner-approved), a timing of the redactor on a large log against r28.
4. Then: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, publish as a prerelease with `--latest=false`, post-ship on WINBRAT (`tools/post-ship-local.ps1`), live scenarios (tabs, cycles, servers, ping, modes, dump, versions, autoselect), the fast-click run, an install of the released APK on an emulator, static checks of the macOS and Linux packages.

## Not covered

macOS, Linux and Android on real devices, a real tunnel on Android, urltest switching under a degrading server, real screenshots of the installed app, the stable-channel live-update gate.

## Verification

Exact-head CI on this PR; everything else is recorded in the Outcome.

## Outcome

Published 2026-10-04 as prerelease `v2.50.0-r29` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `99e6b09a`, 18 assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and sidecar before upload; macOS, Linux and Android from the tag workflows), integrity run green before and after publication, strict CI gate `OK` with 8 green, waiver gate applied (the same three owner-gated P1 lines as r28), `POSTSHIP-LOCAL: PASS` (2 cold cycles, first attempt). The APT run and the Windows update test on the release event were green.

Checks before the tag, all at main `99e6b09a` (the tree equals the tested bump PR head):

| check | result |
|---|---|
| Release solution build on WINBRAT | 0 errors, 229 warnings (r28 at `8f7e3e97`: 229) |
| full test suite on WINBRAT | 3355 passed, 0 failed, 19 skipped (tests for other systems), 5 m 24 s; this includes the six `PageScreenshotTests` |
| Android build on WINBRAT (android-x64, Release) | 0 errors, 83 warnings; installs on the emulator, `versionName` 2.50.0-r29, starts, stays alive, no `FATAL EXCEPTION` |
| redaction compared with r28 (both `VPNRouter.Core.dll` loaded by one console app, 50 MB of log text) | `RedactLogText` 1.84 s (r28 2.00 s), `ScrubSecrets` 1.32 s (r28 1.41 s), pathological 500 000 characters 6 ms and 5 ms, same marker count for `ScrubSecrets` |
| redaction behavior | `usersecret`, `userpassword`, `authsecret`, `authtoken` without separators are hidden in log text now (r28 left them); `author`, `the authentic authority`, `username`, `password hint` are untouched |
| headless audit of every screen (`tools/ui-mcp/audit.py`, Linux renderer built by CI at the release commit) | 468 cells, 0 warnings, 0 broken, 0 sweep failures |
| PowerShell 5.1 parse of all 26 scripts | 22 parse; 4 fail on non-ASCII text in files without a byte-order mark (see the ledger entry); none is on the release path |
| `git diff --check`, secret scan of the files changed since r28 | clean; the only hits are fake credentials in test fixtures |
| `check-open-p0.ps1` | 3 open P1 lines, waived |

Live evidence on WINBRAT with the installed r29 (r28 numbers from its own Outcome):

| | r28 | r29 |
|---|---|---|
| connect from the home screen, Hysteria2 server selected | 3.7-4.1 s | 2.7-4.5 s (4.5, 2.8, 2.7) |
| stop | 4.6-4.9 s | 3.9-4.4 s |
| connect to each of the 6 subscription servers | not recorded | 2.6-4.8 s, public IP = the server's, restored after each stop |
| auto-select, Hysteria2 server selected | urltest of 3, delays 59-166 ms | urltest of 3, 59-163 ms, public IP = picked server |
| auto-select, VLESS server selected | group of 4 | group of 4, 52-183 ms, public IP = picked server |
| Other versions on the experimental channel | r27..r23 + 3 stable | installed r29, r28..r24 and v2.49.3, v2.49.2, v2.49.1 |
| Test all, VPN off / on | 2.1 s / 2.1 s | 2.1 s / 2.1 s |

Also: 6 mode switches never showed "Connect", tabs 0 failures, 600 fast clicks through the tabs 0 errors with the app alive, no warnings or errors in the log and no crash events in any run. The static checks of the published packages: all sidecars match, the macOS zip and the Linux tarball, deb and APK carry version 2.50.0-r29, the APK certificate (SHA-256 starting `6e50af0f`) is the same as in r28 and verifies with v2 and v3.

The external read of the four product diffs (pi) found one P2: log redaction of a quoted value with a space (`password="alpha beta"`) hides the part before the space and leaves the rest. The same input behaves the same in r28, so it is not a regression; it is recorded in the ledger as a P3 and left for a separate change.

Not verified: macOS and Linux on real machines, Android on a device and a real tunnel on Android (the published arm64 APK was only checked statically), installing an older version from the Other versions list, the stable-channel update path to r29, urltest switching under a degrading server, real screenshots of the installed app. The leftover `src-*` folders on WINBRAT are empty directories from earlier tasks; `C:\android-build\apk` holds about 7 GB of old APKs of this campaign and was not cleaned (free space 11.6 GB).
