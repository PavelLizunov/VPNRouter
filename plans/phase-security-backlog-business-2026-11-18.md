# Close the confirmed security gaps from the issue queue

Base business at aee8eae29e963dd79fa609b6e14c89e685d1357c. Address the affected SDK-bundled VSTest version (#673),
Android local control and per-app isolation (#689), and the elevated Windows
service-menu launch boundary (#661). The Android report needs independent
verification. Treat a successful configuration test as configuration evidence.
Keep #664 (partially available routing sets) and #666 (desktop API coverage)
separate from security closure unless a privacy or authorization impact is shown.

Retain the .NET 10 target, single-tunnel lifecycle, local authenticated traffic
statistics and liveness. Preserve existing settings and safe repair/support
operations. Keep already issued clients and public releases outside this source
change. Rollback is a revert of the relevant repair on the owning line.

1. Build the affected targets with the fixed SDK in CI, including Android.
2. Verify the actual SDK/test runner and the defensive regression cases.
3. Record the reviewed source changes and observed verification here.
4. Review trust boundaries, lifecycle compatibility and tests independently.
5. Check Android isolation on an authorized Android environment before claiming
   the reported symptom resolved. Use synthetic inputs and ordinary app actions.
6. Compare both maintained branches, existing saved settings and production
   entry points. Record unverified conditions and keep their issues open.

## Confirmed scope after triage

Protect explicit geographic rules, port ranges and transport lists (#664). Review all twelve CodeQL log warnings and scan the resulting commit. Include transient proxy authentication and Windows execution of the installed Zapret package. Keep public-surface test maintenance (#666) in its own issue. Merge both editions after independent review and passing checks as directed by the owner.

Windows ownership repair covers GUI shutdown, service removal, silent upgrade, and explicit purge. Preserve each edition's service start mode and selected strategy through an interrupted upgrade. Verify path decisions, retry behavior, and shared-service conflicts with fakes. Windows build and installer execution belong to the integration worker. PowerShell fixtures passed for exact process and SCM paths, protected-root purge, standalone Prepare and fail-before-copy, and the protected Standard publisher. Roslyn parsed the Windows sources. Windows xUnit and the compiled installers await integration. The Business publisher also passed packaged and historical helper selection, preparation failure, missing helper and checksum mismatch fixtures.

## Delivery and acceptance

The SDK block proceeds separately in PR #695. This worktree holds the runtime changes. Independent review found additional package, diagnostic, native shutdown, cache, and import boundaries. Test the runtime commit on hosted Linux, Windows and Android and use the private ARM64 Android 14 emulator for benign checks of app selection, reconnection and statistics. Do not attribute the exit-IP report to a particular cause without device evidence.

## SDK delivery block

Ship the SDK fix independently while the client trust-boundary fixes undergo review on the runtime branch. Both a review of the Microsoft advisory and the published SDK manifests confirmed affected VSTest 18.6.0 in SDK 10.0.303 and fixed 18.7.1-hotfix-26529-119 in 10.0.404. Root and Android SDK selection, signing, packaging provenance and the hosted test jobs now agree. The Linux and Windows jobs report the actual commit and bundled runner; the Android compile job checks workload compatibility. The Arch reporting unit test and synthetic SDK inventory parser passed locally. The private Mac has only SDK 8.0.125, and the Windows and Linux workers were powered down during the inventory. The exact-commit hosted tests passed with SDK 10.0.404 and bundled VSTest 18.7.1-hotfix-26529-119.

## Diagnostic privacy

Review of the twelve open C# cleartext-storage alerts identified eleven distinct logging sites in subscription parsing, server selection and failover. Record counts, status codes and fixed event messages in application and service logs. Keep the selected server and the reason for a rejected startup available to the explicit UI event. Regression coverage inspects structured log properties, exception details and rendered text. Independent source review accepted the changes in both editions. Run the regression suite and CodeQL against the delivery commit before closing the alerts. GitHub returned no prior branch analysis for refs/heads/business. Its first scan of the delivery commit must be checked independently of main.

The first branch scans cleared all twelve original findings. The Business scan reported a further sensitive-data flow in Slipstream logging. Both editions use the same transport code. The repair sends private arguments only to the child, logs fixed transport events and excludes the previous raw transport files from diagnostic exports. The Linux suite also exposed an obsolete test expectation that an invalid link was logged as an exception. The test now checks the fixed warning, the absence of an exception and empty structured properties.

## Runtime integration

Windows uses an edition-owned package, checked strategy arguments and first-party maintenance and test actions. Migration checkpoints service ownership and start mode before replacing files. Filter updates take the same lock as package publication. On Android, all VPN profiles require a single established TUN before the service saves the session or reports Connected. Restores retain the effective package selection. Verification uses an authenticated loopback transport and confirms child shutdown. Previously saved profiles remain available for rechecking, and changed URIs require a new result. Geographic policy validation covers both custom and built-in sets, DNS precedence and replacement of a running tunnel. Hosted Windows, Linux, Android and the private ARM64 device provide the runtime acceptance evidence.

The privacy fixes merged in Standard #696 and Business #697. The PR analyses (2195595835 and 2195599137) and both post-merge CodeQL analyses (main 2195680746 at 510234a3, business 2195678537 at bf9e822f) reported zero findings. GitHub marks alerts 1 through 12 fixed on main. The runtime review cleared the Android TUN descriptor check and identified remaining live replacement and subscription persistence paths. Runtime CI also identified an archive extraction warning. Track these in the defect ledger and recheck each delivery commit.

Business CI first failed the local HTTPS verification test while reading the proxy CONNECT response (job 134805677717). The handler now sends authentication on the first CONNECT after validating the owned proxy certificate. The loopback test checks a single proxy connection and that the destination receives no proxy credentials. Windows business-surface job 134805674407 failed while initializing Avalonia before the test body. Run it again on the updated PR.

Headless and Android now persist confirmed subscription-chain rejections before returning an error. The storage regressions reload the saved state and include a transient HTTP failure as a control. Stale-selection and preference-repair logs on Android no longer include server labels or raw recovery details. Windows staging accepts the historical install ZIP selected by release discovery and its exact checksum sidecar. Review identified further Windows migration, repair and shutdown paths in #661. The first update initiated by an older client still uses that client's publisher.

Windows #661 review follow-up covers migration preflight, acquisition of a verified package on Business repair, and protected automatic repair following publication. Recheck using synthetic registrations and offline publisher/repair fixtures.

The Headless save/reload regression exposed placeholder cleanup discarding an active server held only by an enabled subscription. Reload now includes enabled subscription rows when checking the active subscription selection. The regression verifies each selection field and subsequent offline resolution.

On the Business ARM64 test build 89b5a6f4 (CI 44305441476), Android 14 reported the expected UID ranges in Off, Include and Exclude after connection. Each selection survived a cold app relaunch. The screen showed a saved Include-to-Exclude change before reconnect applied it. No external IP measurement was made.

Live replacement validates the selected rules and profile before retiring the original tunnel. A forced restart stages the candidate beside the active configuration and publishes it only after the old process stops. The refused-stop fixture verifies that the previous file and lease survive. Settings diagnostics keep counts and fixed status messages while the UI retains its recovery notice.

The verifier now authenticates one CONNECT per physical connection after checking the private proxy's certificate. Local HTTP and HTTPS fixtures cover origin requests, a redirect and concurrent cancellation. The PR review also identified four remaining live-replacement paths and two runtime/CI coverage gaps tracked in the defect ledger. Linux and Windows both reported a stale reflection call in the policy admission test. The fixture now passes the ordinary Apply selection flag and checks the cold-start outcome for the compiled edition.

Confirmed chain rejection clears duplicate registrations for the same URL and identifiable legacy copies in the saved revision. Transient refresh failure preserves the previous saved state. Android and Headless fixtures reopen storage and exercise both refresh orderings.

Android now persists the per-app selection accepted by VpnService.Builder for the active session. The main screen and picker use it to distinguish active policy from saved edits until reconnect. A fixture checks both languages. The Windows GUI takes ownership of the exact service image and keeps its winws handle when exit is uncertain. The updater checks the four required AMD64 images and imports supported legacy filter data on first installation into the protected location. Windows and ARM64 execution evidence remains pending.

The Android ARM64 compilation failed on an ambiguous RadioButton reference in both editions; the picker now names the Avalonia control explicitly. Independent review accepted CONNECT credential isolation and Android subscription persistence. Headless eviction now compares the fields stored in the legacy scalar, and a refused escalated Unix stop retains the original process handle and lease. The affected regression fixtures reopen a legacy-only feed and refuse replacement under both Unix launch modes.

## Windows #661 review integration and #664 service compatibility

Silent Prepare and native migration check service, process and driver ownership before service changes. Explicit repair downloads the verified package and retries the retained strategy and startup mode. Automatic launchers and publishers use trusted Windows paths and protected repair staging. The standalone installer restores the original service state and repairs the selected application directory.

The Business GUI and service host use the same image-verified SCM registration and PID for settings acknowledgements. The installer remembers the exact running service name for restart. Explicit CLI service installation still accepts its chosen executable path.

- Migration and installer preflight includes `VPNRouter.App/Program.cs`, `VPNRouter.Core/Services/ZapretLegacyServices.cs`, `packaging/windows/install.ps1`, `packaging/windows/installer/{purge-zapret-runtime.iss,stop-vpnrouter.ps1,zapret-maintenance.ps1}`.
- Automatic repair and publication includes `VPNRouter.GUI/{main.go,repair.go,repair-bootstrap.ps1}`, `VPNRouter.App/{Services/SelfRepair.cs,VPNRouter.App.csproj}`, `VPNRouter.Core/{Services/UpdateChecker.Windows.cs,VPNRouter.Core.csproj}`, `packaging/windows/installer/{publish-windows-update.ps1,repair-environment.ps1}`, `build.ps1`, `.github/workflows/publish-pages.yml`.
- Offline coverage includes `VPNRouter.GUI/repair_test.go`, `VPNRouter.Tests/{SelfRepairTests.cs,ReleaseToolingContractTests.cs,ZapretServiceMigrationTests.cs}`, `VPNRouter.Tests/Windows/{Test-AutomaticRepair,Test-InstallerPrepare,Test-InstallerRepair,Test-RepairEnvironment,Test-WindowsUpdatePublisher,Test-ZapretMaintenance}.ps1`.
- Business publisher and SCM aliases includes `VPNRouter.Core/Services/{UpdateChecker.Business,MainServiceRegistration,WindowsServiceRegistration,ServiceSettingsReceipt}.cs`, `VPNRouter.App/Services/WindowsServiceHelper.cs`, `VPNRouter.App/ViewModels/{MainWindowViewModel.Settings,ServiceViewModel}.cs`, `VPNRouter.Service/{Program,ServiceInstaller,VPNRouterService}.cs`, `packaging/windows/business.iss`.
- Business coverage includes `VPNRouter.Tests/{BusinessUpdateApplyTests,ServiceFlyoutOwnershipTests,MainServiceRegistrationTests,Fakes/FakeRegistration}.cs`, `VPNRouter.Tests/Windows/Test-MainService.ps1`.

The PowerShell maintenance, installer preparation, automatic repair, protected environment, publisher and relocated repair fixtures passed offline. Release template resource bytes, Go formatting, Roslyn syntax and Git whitespace checks passed. The installer alias fixture and five synthetic executions of the Business publication body also passed. Native Windows ACL/UAC behavior, xUnit, Go tests and build are worker checks. The fixtures used fake operating-system boundaries.

Live Apply checks imported rule sets with the selected sing-box before replacement and retains the enabled ad filter in DNS and route policy. Desktop Connect preserves an owned Unix session if the control API is unavailable. The Windows service acknowledges the saved revision after applying it, keeps its last accepted settings on failure, and uses strict validation for a previously rejected revision on restart. Business looks up both registered names and confirms the executable and PID before applying settings. The Business CI matrix builds the Windows service and runs the ownership and receipt cases.

`VPNRouter.Core/Services/UpdateBackup.cs` excludes private repair scratch from rollback payloads. `VPNRouter.Tests/UpdateBackupTests.cs` covers that boundary.

The independent recheck found four remaining #664 cases in service readiness and recovery, imported DNS ordering, validation of the actual runtime and relative files, and Unix failover ownership. Keep both PRs open for corrections and review. Linux CI also caught three imported-config cases reporting applied routing during preparation. Correct the status and retain the tests that reject premature success.

At `5610ecb6`, Linux test job `134841525012` reported premature success in the missing, unreadable and valid imported SRS scenarios. The staged configuration now reports preparation; the tests still require the original process and runtime file to survive failure. The Windows update integration workflow `44315814392` exercised the test installation, update and receipt successfully. The C# CodeQL analysis for this commit returned zero results.

The native Windows PowerShell repair fixture failed in hosted characterization. WINBRAT ran the identical committed fixture successfully under elevated PowerShell 5.1.17763.9479. Preflight recorded 8 logical CPUs at 25% load, 12,443 MiB free RAM of 16,383, 0 of 2,432 MiB pagefile used, 100 GiB free disk and no active build processes. Installed SDKs were 10.0.301, 10.0.302 and 10.0.400. This test requires PowerShell only. Run the fixture directly in the Windows update workflow and include child stderr in fixture failures.

The #664 corrections bind relative local rule-set files at import, use the live manager for SRS checks, place DNS interception before forwarding when the managed ad filter is active, and keep the session and lease on an unconfirmed failover stop. Service Apply verifies the responding control endpoint belongs to the replacement child, then updates health recovery with accepted settings before acknowledging the revision. Regression fixtures cover rule order, runtime selection, stop refusal, service readiness and recovery. Source syntax and whitespace checks passed. An offline native check accepted a readable fixture from a different working directory and rejected its missing file. Hosted SDK tests and independent review of these changes are pending.

The committed Windows repair fixture also passed directly in hosted Windows PowerShell, job `134852938117`. The Linux/Windows xUnit run supplies separate evidence for its test-host environment.

ARM64 Android 14 device acceptance at `5610ecb6` confirmed Off, Include and Exclude UID policies, persistence after relaunch, idle stats, active versus pending UI while connected and matching UI after reconnect. After disconnect both surfaces still showed Active even though Android had removed the VPN. Passive observations found the private command socket and no app-UID TCP listeners in the available IPv4/IPv6 tables during Include or Exclude; process FD attribution was restricted. No external egress address was measured.

The #661 source recheck accepted PE validation and historical asset discovery. It identified follow-ups on rollback relaunch, update preflight, shutdown ownership and per-setting migration precedence. Hosted characterization traced the PowerShell fixture failure to automatic loading of the Get-Acl module. Use the filesystem ACL APIs directly and retain the exact owner and access descriptor check.

The Windows repair helper reads the verified owner and DACL directly from the filesystem API for Desktop and Core PowerShell. The standalone installer pins the normalized helper digest. Android now saves its terminal service state before broadcasting tunnel-down, and both screens reread it even after duplicate events. The shared state predicate is public for the Android target. Device and hosted Windows checks will cover the corrected code.

Independent review confirmed the child-specific readiness probe and SRS runtime selection. Correct and verify consistent service acceptance/recovery, asynchronous crash notification, preservation of a pending firewall release, imported deny precedence, the accepted recovery copy and an indeterminate Linux child state before merge.

Windows #661 rollback relaunch follow-up checks the restored installation and launches only its verified GUI or App with fresh private scratch and a system-owned search path. Verify normal legacy/relocated launch and rejection with offline fixtures.

Linux capability-mode stop now releases ownership only after an explicit positive exit result. The engine fixture models an unavailable status with the same legacy HasExited fallback as production and checks that the original session, handle and lease survive. Startup reports a generic validation exception for malformed JSON. The Windows service recovery fixture checks the primary proxy and every VLESS outbound in the accepted profile, including Business portal routes.

Rollback relaunch verification passed locally in PowerShell with inert files and a recording launcher. Both legacy GUI and App fallback selected fresh protected scratch in a relocated fixture. Three refusals preserved the failure marker and recorded no launch. The existing repair-environment fixture and the Roslyn syntax/resource wiring checks passed. The added native Windows ACL fixture awaits a Windows test run.

The ARM64 Android 14 device confirmed Off/Include/Exclude and Next connection after foreground disconnect on both UI surfaces at the November 18 acceptance commits. Address stale swipe alarms and in-flight connects, read one policy snapshot for live start and restore provenance, and ignore obsolete status events. Verify the new transitions in CI and on the private device with a local-only profile.

Independent review found two remaining rollback failure paths. Verify the candidate inside the snapshot lock, keep the last live tree until launch succeeds, and preserve the failure marker if child creation fails. Windows CI also reported a synthetic SystemRoot in Test-RollbackRelaunch.ps1 and a localhost controller timeout. First-install Zapret flags and filter lists now retain per-setting precedence. Where an older version recorded only an empty directory, use the valid legacy setting.

The remaining Android corrections serialize service stop/revoke with swipe scheduling, distinguish explicit external restore, carry the original connect through QR selection and refresh, and check status-event ownership when updating the screen. Device acceptance of these cases remains pending.

The Windows updater now waits for package ownership checks and its lock before suspending the owned strategy. GUI restart and SCM shutdown keep their handle until exit is confirmed. Routing corrections cover cold-start monitoring, immutable accepted imports, DNS fall-through, localhost address families and retrying explicit Stop after an unknown exit. Verify the desktop and service regression fixtures on the submitted commit.

Linux and Windows CI found CS0117 in the desktop disconnect and quit handlers. Forward the existing localized TunnelStopUnconfirmed message from Core through the desktop Strings class and rerun compilation, tests and CodeQL. The missing Business screenshots followed the failed compilation.

Further independent review requires command-owned Android selection, tile consent and status/statistics delivery; preserve explicit game-filter Off through one-click installation; verify interrupted rollback, trampoline launch and publication locking; retire desktop tunnel ownership at every exit; fence queued startup failover, snapshot GUI settings and honor the controller host. Use deterministic first-party fixtures, exact-head CI and private Android acceptance.

Android selection now checks the originating command under the service preference lock at commit. The tile passes its original request into consent, and an explicit restore republishes the established session. Status and statistics retain command, state and TUN identity through rendering. File-backed selection and delayed-delivery fixtures cover these changes. Previous Windows CI recorded five passing rollback cases and three shutdown cases per edition. Two Linux fixture assumptions were corrected for the next CI run.

Windows job 134893463649 reported five passing WindowsRollbackRelaunchTests cases, three VPNRouterServiceShutdownTests cases, six UpdateCommandStopsOnlyAtVerifiedPublication cases and both restart-menu refusal cases on the preceding commit. The one-click game-filter default respects an explicit Off choice before and after first installation. A command fixture runs both initial choices with a local package.

Windows rollback uses distinct snapshot-copy and displaced-live directories. Compensation accepts only the directory moved by this operation. The relaunch helper waits for the trampoline handoff, and publication shares the rollback lock. Flat installations check their own marker and lock. Ten offline rollback cases, six publisher cases and the repair-environment fixture passed in each edition. The manifest publisher also passed contention checks before Prepare in nested and flat layouts. Native Windows ACL, historical trampoline, managed command and service fixtures run in hosted CI.

For #664, startup failover now checks its originating child and cancellation under the lifecycle gate before selecting and retiring a tunnel. Connect and Apply capture accepted settings independently of GUI edits. Generated delay and single-outbound warmup use the controller from the generated JSON. Desktop quit, restart, reset and update require confirmed retirement of the owned tunnel. Fixtures cover cancellation or rollback while queued, unknown Linux exit followed by retry, and IPv4-only or IPv6-only controller listeners.

Android follow-up review accepted command locking, explicit restore and event ownership. Correct the retained legacy row after an intervening Free Config selection, synchronize the Simple input after selecting a profile and keep each system consent result tied to its original command. Verify storage after reopen and queued consent with both result outcomes.

The Android follow-up removes the stale standalone duplicate while preserving its previously selected scalar until rejection. A Free Config choice refreshes the Simple input, and tapping Connect with an unchanged input uses the saved profile. A consent dialog keeps its original owner across activity recreation; queued Connect requests recheck system consent. Three file-backed selection cases and consent ownership fixtures cover the transition.

The game-filter picker now distinguishes an unset one-click default from explicit Off. Its regression sets the actual bound picker before running OneClick. The Linux exit fixture now seeds its sentinel after GUI construction, which previously normalized it before the operation under test. On the preceding Android commits both ARM64 compilation jobs passed, and Standard Linux and Windows each passed 18 subscription refresh cases and both tunnel-event cases. The Windows review accepted transactional compensation and publisher locking. Self-hosted rollback and targeted repair still need integration and independent review.

Follow up the routing review by binding the GUI submission before worker dispatch, releasing the single-instance TUN lease on a pre-launch configuration write failure, and using the effective controller for initial Unix readiness. On Android, investigate the warm reconnect failure observed in all three policy modes at Standard 8b788c56 and Business 2a3f5e05. Verify the product paths in CI and on the private offline device.

Desktop Connect captures the submitted settings before OS preparation. Apply, reconnect retries and TrueSplit restart use an independent snapshot. A write failure before child launch releases the TUN lease, and the manager accepts retirement. Added GUI snapshot and manager retirement regressions. The independent review accepted the Windows game-filter picker and legacy Android standalone-row eviction. The Android whitespace and consent-recreation cases remain in progress.

Unix startup now supplies the parsed controller and its credential from the final configuration to the first readiness request. A successful monitored Apply updates the manager and health client together. Six regression cases cover shorthand binds, local address families, imported credentials and an injected HTTP transport. The code and fixtures passed static checks. Hosted test execution and independent review are pending.

Simple Connect now compares the saved and displayed URI with the same whitespace normalization. A file-backed preference case retains the selected Free Config and the unrelated subscription across reopen. At system consent, the dialog result belongs to its original command. The resumed activity claims a queued command once and rechecks the current command ID. Regression fixtures cover recreation between result and resume, a later Connect, and Stop. Hosted tests and private device acceptance are pending.

After Stop, a status query could dispose the managed Java class wrapper used by the next UI command reservation. Status checks and reservations now share a process-lifetime JNI class reference. Reservation errors have a separate localized message. The Home policy summary uses the width of the card. The offline Android fixture checks Stop and Connect within one PID, a new network ID and identical UID ranges. Its parser checks used the saved Off, Include and Exclude observations. Verification of the packaged fix awaits the private ARM64 device run.

Follow up the exact-head reviews on three paths. Check that Simple Connect selects a newly entered manual URI with another subscription enabled, reports a failed command reservation before fetching an uncached feed, and honors Stop. Verify that controller transport injection preserves local socket selection. In desktop Simple mode capture the submitted routing choice before asynchronous subscription preparation.

Android manual and QR profile selection now commit the selected standalone row and its URI together under the command lock. A file-backed regression checks selection among three distinct profiles and the previous scalar-only format. An uncached subscription reports a command reservation failure. Controller requests through a general PolicyHttpClient use the dedicated local socket handler. A direct test transport receives a validated IP literal, and a loopback listener fixture checks that the general HTTP handler receives no request.

Follow up the Android source review on historical fallback names, shared profile import, and same-name servers in different sources. Exercise the saved preferences after reopen, including a rejected import and explicit source changes. Validate the selection used to generate the next tunnel configuration.

Manual selection now repairs the legacy fallback name under the preference lock. Profile import commits the connection payload and source together, preserving the previous selection on a parse failure. Subscription selection records the registration and row, including for the server picker. The auto-select pool gives the resolved row precedence when labels coincide. File-backed regression cases cover an old summary save, manual A to imported B, invalid import, and same-label rows from separate registrations. C# syntax and Android lifecycle source checks passed locally. Runtime results are pending exact-commit CI and private Android acceptance.

Desktop Simple Connect captures the routing settings before waiting for refresh or probes. The command passes the chosen row to the engine using its submitted subscription pool and preserves later editor changes. Nine delayed-command cases and one same-label, shared-UUID case cover selection through the production resolver. The Business matrix includes the command tests on Linux and Windows.

Follow up the Android review of saved selection and pending commands. Cover upgrade from a name-only choice, edits and removal of the selected registration, unnamed rows, refresh racing with import, and membership in the optional pool at generation. Check command ownership again before tunnel replacement and reconcile the foreground status when preparation is superseded. Exercise persistence with offline registrations and check the service transition independently.

The Windows rollback worker runs outside the installation. It verifies the initiating process and holds the installation lock through process exit, promotion and relaunch. Targeted repair takes the same lock and preserves the failure marker on a refused launch. The implementer ran seven offline PowerShell fixtures per edition and verified the embedded Pages helpers. The installed-App Windows test and independent source review are pending.

Private Android 14 acceptance on Standard 048ad1ad and Business fc1bda16 confirmed six warm reconnects in the original application processes, expected UID ranges in Off, Include and Exclude, and distinct endpoints for manual and feed selections with the same name. File-based ConfigShare import was not exercised on the device. Saving Custom JSON after a feed still left its old URL in the input and Connect started that feed. Correct and verify that transition with the subscription and lifecycle follow-ups.

Follow up the desktop Simple review. Select and restore the submitted row by source and identity across reordered or unnamed entries. Display the routing mode and selected endpoint of the accepted tunnel when the user edits the next connection during a probe. Cover the real command, status refresh and reopening saved settings.

Business Windows CI on cf896155 passed the installed-App rollback case. The repair fixture could not load Get-FileHash in its inherited PowerShell module environment. It now sets the same PSHOME Modules path as the installer before executing the production repair functions.

Recheck the default web repair and reinstall paths under a concurrent installation lock, account for repeated rollback failures, and distinguish a crashed doctor process from its documented configuration diagnostics in both repair layouts.

The desktop publishes the display source, route mode and server with the accepted startup or Apply settings. Reported auto-select tags resolve against that generation's configuration. A delayed Simple Connect regression checks the editor choice, status and active indicator independently. The service fixture checks that failed Apply keeps the previous display and successful Apply changes it. Hosted results are pending.

Android now reconciles the saved input and selected source across Custom Save, registration edits and legacy picker migration. Before generating the pool it checks that the selected registration still contains the row. The service rechecks the submitting command before retiring a tunnel and before publishing its replacement. File-backed cases exercise the generated outbound, including retyping an earlier Simple URI after selecting a different row in the manual list. Offline source and syntax checks precede hosted Android and desktop tests. The private ARM64 run will verify Custom Save, the manual-list case and warm reconnect on the new APKs.

The desktop display follow-up covers the visible Simple route mode, live group-member notifications, selection indicators after Apply and Stop, actual generated proxy groups, same-endpoint labels and status from a service-owned tunnel.

A desktop subscription pick now records its registration and row in the settings. Simple Connect uses that identity after refresh and keeps a later editor choice for the next command. Command tests cover provider reorder, YAML save and reopen with duplicate names and UUIDs, an unnamed probe winner and its required detour. Legacy settings with only a name keep their previous lookup behavior.

Follow up the Android review by testing refresh retirement between row resolution and dispatch, every generated member after a legacy URL edit, and raw empty-URL matching in the built-in Manual list.

The Connect reader now records the exact feed row resolved by its command. A refresh retiring that row invalidates the command in the cache write. New regression cases cover both auto-select settings, a later command, an established tunnel, the full legacy replacement group and the Manual picker with an empty URL. Local checks passed for C# syntax in four configurations and 33 Android source-order assertions. Hosted execution and device acceptance are pending.
