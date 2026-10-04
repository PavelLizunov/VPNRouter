# D-10: what the owner's r16 diagnostics show, and the crash safety net

## Evidence (bundle `VPNRouter-diagnostics-20261002-114352.zip`, Windows 10.0.26300, r16, local time +03:00)

- 08:59:43 the GUI (PID 8600) starts; 8 s later `ToggleConnectionAsync.Connect.Subscription` connects the saved subscription without a click
  (auto-connect on start with `active_sub='Iceland New XHTTP ~ninitux'`; the exit IP is Iceland). `TrueSplit state="Fallback"`: none of the excluded apps
  was running at connect, so the driver was not engaged ("not loaded"). After a stop and start at 11:40 two excluded apps were running and the driver engaged
  (`engage=true (2 excluded path(s))`).
- The Windows service `VPNRouter` is installed, `AUTO_START`, running as LocalSystem from `C:\Program Files\VPNRouter\app`. It started at 11:42:09 and again at
  11:43:11 and connected the same subscription on its own (`Resilient VPN: attempt 1/5 succeeded`, driver engaged inside the service). A GUI that starts while the
  service owns the tunnel shows a disconnected state with no selected config (the health check even warns "multi-owner state"), and its True Split indicator
  cannot see the service's driver. This matches "connected by itself, no config selected, driver not loaded, fine after stop and start from the app".
- 11:42:31 a new GUI (PID 15800) starts: `Previous run (PID 8600 ...) did not shut down cleanly`. Tab changes are logged every ~250 ms from 11:42:39
  (tabs 2,3,2,1,0,1,2,3,4,3,2,1,0,1,2,3); the last line of that process is the change to tab 3 (Applications) at 11:42:43.563. The next start (11:43:16) says
  `Previous run (PID 15800 ...) did not shut down cleanly`. So PID 15800 ended abruptly during fast tab switching, with nothing in the normal log. Its third
  visit to tab 3 had worked; the fourth did not.
- The bundle contains no crash report, because `DiagnosticsExporter` never included `<data>\crashes\crash-*.txt`, the only place `CrashReporter` records an
  unhandled exception. `update.log` shows no r16 update (last entry is the 2026-10-01 22:16 r14 failure), so r16 was installed with the Inno setup.
- The installer's finish page launched the app as the original (non-elevated) user: the app then needs its own UAC prompt, which would explain "did not start
  after the install".
- Not reproduced here: 500 fast UI Automation selects (about 8 per second) across all tabs on the deployed r16 on `windows-worker` (Server 2019), and a
  150-click no-delay headless sweep, raised nothing.

## What

1. `UiExceptionGuard` (`VPNRouter.App/Services`): an exception on the UI thread is logged, written as a crash report and swallowed (at most 8 per 10 s, then the
   process ends as before), so a failed frame cannot take the tunnel owner down. `Program.BuildAvaloniaApp` installs it after setup.
2. The diagnostics bundle now carries the last 5 `crash-*.txt`, `crash-index.txt` (crash and graceful-shutdown markers, newest first) and the launcher stub
   log `trampoline.log`.
3. Inno finish page: `runascurrentuser`, so the app starts elevated from the elevated installer without a second prompt.
4. True Split "no excluded app is running" message is localized and says what happens next.

## Not covered / open

- The cause of PID 15800's death is unproven. The guard turns an exception on the UI thread into a log line and a crash report; if the process is killed
  from outside (the installer's stop script, the service, an antivirus) nothing is written, and `crash-index.txt` shows a start without its shutdown marker.
  Needed from the owner: `C:\ProgramData\VPNRouter\crashes\` and the Windows Application event log around 11:42:43.
- The GUI and the Windows service fighting over one tunnel (GUI start cleans the service's firewall rules and orphan processes; the service restarts without
  the GUI knowing) is a design problem; recorded in the ledger.
- True Split does not engage later when an excluded app starts after the connect (ledger).

## Verification

121 of 121 on `windows-worker` at the exact SHA (`UiExceptionGuardTests`, `DiagnosticsExporterTests`, `WindowsInstallerContractTests`, `HeadlessGuiTests`,
`PageScreenshotTests`), CI on the PR.

## Outcome

Pending.

## Update after the `crashes` folder (2026-10-02)

See the brief's Outcome: no unhandled-exception report exists for 11:42 on r16; the abrupt exits coincide with the setup's stop script (two setup runs in a row). The only
crash report is the r14 Applications-tab exception already fixed in D-3.
