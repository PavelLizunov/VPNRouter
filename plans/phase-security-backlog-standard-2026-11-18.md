# Close the confirmed security gaps from the issue queue

Base main at e6d6df9fe4c928dcbaa0ab32cfec27d7cdfd2d17. Address the affected SDK-bundled VSTest version (#673),
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

Windows ownership repair covers GUI shutdown, service removal, silent upgrade, and explicit purge. Preserve each edition's service start mode and selected strategy through an interrupted upgrade. Verify path decisions, retry behavior, and shared-service conflicts with fakes. Windows build and installer execution belong to the integration worker. PowerShell fixtures passed for exact process and SCM paths, protected-root purge, standalone Prepare and fail-before-copy, and the protected Standard publisher. Roslyn parsed the Windows sources. Windows xUnit and the compiled installers await integration.

## Delivery and acceptance

The SDK block proceeds separately in PR #694. This worktree holds the runtime changes. Independent review found additional package, diagnostic, native shutdown, cache, and import boundaries. Test the runtime commit on hosted Linux, Windows and Android and use the private ARM64 Android 14 emulator for benign checks of app selection, reconnection and statistics. Do not attribute the exit-IP report to a particular cause without device evidence.

## SDK delivery block

Ship the SDK fix independently while the client trust-boundary fixes undergo review on the runtime branch. Both a review of the Microsoft advisory and the published SDK manifests confirmed affected VSTest 18.6.0 in SDK 10.0.303 and fixed 18.7.1-hotfix-26529-119 in 10.0.404. Root and Android SDK selection, signing, packaging provenance and the hosted test jobs now agree. The Linux and Windows jobs report the actual commit and bundled runner; the Android compile job checks workload compatibility. The Arch reporting unit test and synthetic SDK inventory parser passed locally. The private Mac has only SDK 8.0.125, and the Windows and Linux workers were powered down during the inventory. The exact-commit hosted tests passed with SDK 10.0.404 and bundled VSTest 18.7.1-hotfix-26529-119.

## Diagnostic privacy

Review of the twelve open C# cleartext-storage alerts identified eleven distinct logging sites in subscription parsing, server selection and failover. Record counts, status codes and fixed event messages in application and service logs. Keep the selected server and the reason for a rejected startup available to the explicit UI event. Regression coverage inspects structured log properties, exception details and rendered text. Independent source review accepted the changes in both editions. Run the regression suite and CodeQL against the delivery commit before closing the alerts. The latest published main analysis before this change was 2176639062 on e7b4a384 and recorded all twelve alerts.

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

The verifier now authenticates one CONNECT per physical connection after checking the private proxy's certificate. Local HTTP and HTTPS fixtures cover origin requests, a redirect and concurrent cancellation. The PR review also identified four remaining live-replacement paths and two runtime/CI coverage gaps tracked in the defect ledger.

Confirmed chain rejection clears duplicate registrations for the same URL and identifiable legacy copies in the saved revision. Transient refresh failure preserves the previous saved state. Android and Headless fixtures reopen storage and exercise both refresh orderings.

Android now persists the per-app selection accepted by VpnService.Builder for the active session. The main screen and picker use it to distinguish active policy from saved edits until reconnect. A fixture checks both languages. The Windows GUI takes ownership of the exact service image and keeps its winws handle when exit is uncertain. The updater checks the four required AMD64 images and imports supported legacy filter data on first installation into the protected location. Windows and ARM64 execution evidence remains pending.

The Android ARM64 compilation failed on an ambiguous RadioButton reference in both editions; the picker now names the Avalonia control explicitly. Independent review accepted CONNECT credential isolation and Android subscription persistence. Headless eviction now compares the fields stored in the legacy scalar, and a refused escalated Unix stop retains the original process handle and lease. The affected regression fixtures reopen a legacy-only feed and refuse replacement under both Unix launch modes.

## Windows #661 review integration

Silent Prepare and native migration check service, process and driver ownership before service changes. Explicit repair downloads the verified package and retries the retained strategy and startup mode. Automatic launchers and publishers use trusted Windows paths and protected repair staging. The standalone installer restores the original service state and repairs the selected application directory.

- Migration and installer preflight includes `VPNRouter.App/Program.cs`, `VPNRouter.Core/Services/ZapretLegacyServices.cs`, `packaging/windows/install.ps1`, `packaging/windows/installer/{purge-zapret-runtime.iss,stop-vpnrouter.ps1,zapret-maintenance.ps1}`.
- Automatic repair and publication includes `VPNRouter.GUI/{main.go,repair.go,repair-bootstrap.ps1}`, `VPNRouter.App/{Services/SelfRepair.cs,VPNRouter.App.csproj}`, `VPNRouter.Core/{Services/UpdateChecker.Windows.cs,VPNRouter.Core.csproj}`, `packaging/windows/installer/{publish-windows-update.ps1,repair-environment.ps1}`, `build.ps1`, `.github/workflows/publish-pages.yml`.
- Offline coverage includes `VPNRouter.GUI/repair_test.go`, `VPNRouter.Tests/{SelfRepairTests.cs,ReleaseToolingContractTests.cs,ZapretServiceMigrationTests.cs}`, `VPNRouter.Tests/Windows/{Test-AutomaticRepair,Test-InstallerPrepare,Test-InstallerRepair,Test-RepairEnvironment,Test-WindowsUpdatePublisher,Test-ZapretMaintenance}.ps1`.

The PowerShell maintenance, installer preparation, automatic repair, protected environment, publisher and relocated repair fixtures passed offline. Release template resource bytes, Go formatting, Roslyn syntax and Git whitespace checks passed. Native Windows ACL/UAC behavior, xUnit, Go tests and build are worker checks. The fixtures used fake operating-system boundaries.

Live Apply checks imported rule sets with the selected sing-box before replacement and retains the enabled ad filter in DNS and route policy. Desktop Connect preserves an owned Unix session if the control API is unavailable. The Windows service acknowledges the saved revision after applying it, keeps its last accepted settings on failure, and uses strict validation for a previously rejected revision on restart.

`VPNRouter.Core/Services/UpdateBackup.cs` excludes private repair scratch from rollback payloads. `VPNRouter.Tests/UpdateBackupTests.cs` covers that boundary.

The independent recheck found four remaining #664 cases in service readiness and recovery, imported DNS ordering, validation of the actual runtime and relative files, and Unix failover ownership. Keep both PRs open for corrections and review. Linux CI also caught three imported-config cases reporting applied routing during preparation. Correct the status and retain the tests that reject premature success.

At `3e96d410`, Linux test job `134841521027` reported premature success in the missing, unreadable and valid imported SRS scenarios. The staged configuration now reports preparation; the tests still require the original process and runtime file to survive failure. The Windows update integration workflow `44315785118` exercised the test installation, update and receipt successfully. The C# CodeQL analysis for this commit returned zero results.

The native Windows PowerShell repair fixture failed in hosted characterization. WINBRAT ran the identical committed fixture successfully under elevated PowerShell 5.1.17763.9479. Preflight recorded 8 logical CPUs at 25% load, 12,443 MiB free RAM of 16,383, 0 of 2,432 MiB pagefile used, 100 GiB free disk and no active build processes. Installed SDKs were 10.0.301, 10.0.302 and 10.0.400. This test requires PowerShell only. Run the fixture directly in the Windows update workflow and include child stderr in fixture failures.

The #664 corrections bind relative local rule-set files at import, use the live manager for SRS checks, place DNS interception before forwarding when the managed ad filter is active, and keep the session and lease on an unconfirmed failover stop. Service Apply verifies the responding control endpoint belongs to the replacement child, then updates health recovery with accepted settings before acknowledging the revision. Regression fixtures cover rule order, runtime selection, stop refusal, service readiness and recovery. Source syntax and whitespace checks passed. An offline native check accepted a readable fixture from a different working directory and rejected its missing file. Hosted SDK tests and independent review of these changes are pending.

The committed Windows repair fixture also passed directly in hosted Windows PowerShell, job `134852706914`. The Linux/Windows xUnit run supplies separate evidence for its test-host environment.

ARM64 Android 14 device acceptance at `3e96d410` confirmed Off, Include and Exclude UID policies, persistence after relaunch, idle stats, active versus pending UI while connected and matching UI after reconnect. After disconnect both surfaces still showed Active even though Android had removed the VPN. Passive observations found the private command socket and no app-UID TCP listeners in the available IPv4/IPv6 tables during Include or Exclude; process FD attribution was restricted. No external egress address was measured.

The #661 source recheck accepted PE validation and historical asset discovery. It identified follow-ups on rollback relaunch, update preflight, shutdown ownership and per-setting migration precedence. Hosted characterization traced the PowerShell fixture failure to automatic loading of the Get-Acl module. Use the filesystem ACL APIs directly and retain the exact owner and access descriptor check.

The Windows repair helper reads the verified owner and DACL directly from the filesystem API for Desktop and Core PowerShell. The standalone installer pins the normalized helper digest. Android now saves its terminal service state before broadcasting tunnel-down, and both screens reread it even after duplicate events. The shared state predicate is public for the Android target. Device and hosted Windows checks will cover the corrected code.
