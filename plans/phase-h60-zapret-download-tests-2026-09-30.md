# H-60: characterization tests for the Zapret download and install

## Why

Third step of the Zapret test plan (after H-58 parsing and H-59 file switches). `ZapretUpdater.DownloadAndExtractAsync`
(about 400 lines) turns a Flowseal GitHub release into the installed zapret folder and maps every failure to a
`ZapretErrorCategory` that the UI shows; only `CopyDirectoryOverwrite` had a test.

## What

`VPNRouter.Tests/ZapretUpdaterDownloadTests.cs`, 8 tests, no production change, using `FakeHttpClient` and real zip
archives built in memory:

- install (run on Linux only): a release with a single top folder is unwrapped, files land in the zapret folder, the
  version comes from `service.bat`, the status messages come in the documented order and the two HTTP requests carry
  the expected URL, `Accept` header and five-minute timeout; a flat archive without `service.bat` takes the release tag
  as version;
- failures before the install step (run everywhere): no zip asset and no zipball is `Invalid` and downloads nothing; a
  `zipball_url` is used when there is no zip asset; an archive without `bin/winws.exe` is `Invalid`, names what it found
  and installs nothing; a top folder plus a loose root file is not unwrapped and so `Invalid`; bytes that are not a zip
  are `Corrupted`; HTTP 403 from the GitHub API is retried twice (about six seconds) and then reported as
  `GitHubRateLimit`.

Why the install tests skip Windows: the real install step calls `StopWinDivertService`, which kills every `winws`
process, runs `sc stop` and `sc delete` for the WinDivert driver services and sleeps two seconds. On a machine that runs
Zapret that would stop it. There is no seam yet; adding one is part of the next step of the plan.

Behaviour recorded as found: a 403 (rate limit) is retried like any other HTTP error before it is reported.

## Verification

Exact-head CI (Linux runs all eight, Windows runs six and skips two).

## Outcome

Merged after green exact-head CI.
