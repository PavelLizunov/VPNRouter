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
