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

macOS, Linux and Android on real devices, a real tunnel on Android, urltest switching under a degrading server, the screenshot gate (known failing, `PAGESCREENSHOT-RENDER-INVALIDATION`), the stable-channel live-update gate.

## Verification

Exact-head CI on this PR; everything else is recorded in the Outcome.

## Outcome

Pending.
