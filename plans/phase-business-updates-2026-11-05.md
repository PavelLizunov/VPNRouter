# Business in-app updates

## Why and scope

The owner requests the existing Standard check/download/install experience in Business. Continue PR646 on dsh/business-pilot after full30176a85 CI (11SUCCESS,2expectedSKIPPED). Reuse UpdateNotificationViewModel and IDesktopInstaller; add a trusted Business manifest source and edition-specific payload admission/apply. No public configured installer, merge, tag or release is authorized.

## Design and invariants

- A valid embedded private bundle remains authoritative and is atomically persisted under protected AppPaths.DataDir/provisioning/business.json. Generic Business binaries load it without carrying credentials. Invalid input must not overwrite it; settings/cache cannot supply trusted service/update addresses.
- business.updates_url is optional and HTTPS-only. Manifest is bounded16KiB/depth6, schema1, Business/win-x64, canonical newer stable version, positive size<=512MiB and SHA256. Asset is same HTTPS authority without query/fragment/userinfo. No Standard fallback.
- Shared ZIP is app-only, with a bounded schema1 receipt and required runtime/geo. Inspect real Core edition/version and App resource metadata without executing assemblies; reject Standard/private/config-bearing payloads, unsafe paths, links and oversized archives.
- Existing checksum downloader is retained. Pin the successfully verified file manifest to that exact staged attempt; later checks must not invalidate it. Helper rechecks the immutable manifest and copied bytes, waits for own parent identity, aborts on timeout/failure, replaces only app files and relaunches Business App.
- Update preparation stops only the owned engine and blocks connection transitions during installation. Never reach global orphan cleanup, coinstalled Standard service or global sing-box taskkill from this update path.
- Keep selector/mode/language/cache/employee identity, Standard source/install behavior and MainWindowViewModel public API. Add the existing check command in the menu and a compact conditional notice/progress. Localize Business messages.

## Risk, sequence and rollback

Risk HIGH: authenticated executable replacement and provisioning survival. First deliver an update-enabled private bridge retaining the issued rollback installer; then activate only validated credential-free versioned ZIP/manifest. A previous working private installer restores the app. Preserve current provisioning and config rather than replacing the data directory. No builds/SDK installs on Harness/Omarchy; exact-source Windows CI owns packaging.

## Verification gates

1. Build: PASS source5e6b4643 solution/compile checks; generic publication remains pending because its preceding Business test step failed.
2. Tests: FAIL at5e6b4643 Business gate:377passed,2failed,6skipped of385; two failures are isolated Windows helper scenarios. Source compiles and Standard test-update succeeds. At6a863499:379passed,1failed,6skipped; inherited-module and source-change checks now pass, copy failure reaches the intended COPY phase, and only successful replacement remains red. The helper now passes NullString.Value to File.Replace and distinguishes REPLACE/MOVE stages; full corrected-head CI pending.
3. Docs: PASS design, manifest/package contract and pilot instructions updated; delivery outcome still pending.
4. Review: PASS SOURCE-ONLY. Three native reviewers used distinct correctness/concurrency, security and producer/test lenses with own-file exclusions. Stop-isolation, offer-race, helper-shadow and mixed-App/Core findings are corrected and covered; coordinator also serialized two global-data-dir test classes. No external model or runtime acceptance is implied.
5. UI/runtime: PENDING deterministic RU/EN340/420 captures and actual bridge-to-newer-package update on the owner-authorized VM, with preserved account/selection/mode and cleanup. No live desktop screenshots.
6. Characterization/integration: PENDING exact-head Standard public-surface checks, actual generic PE/resource assurance and archive receipt validation. Three expected skipped private/dispatch jobs on normal PR CI.

## Outcome

Initial implementation5e6b4643 compiles but fails two WinPS helper checks; correction awaiting CI. No updater-enabled installer or live feed claimed yet. Existing installer remains ae1276b0. Activity/icon fixes30176a85 have full green CI; live activity was confirmed fresh on four nodes across repeated collection cycles. Current direct-route outage cause remains unknown; audited management jumps restore telemetry.

## WinPS correction evidence

The CI parent is pwsh7, which launches dotnet and then Windows PowerShell5 through Process.Start. Microsoft documents incompatible PSModulePath inheritance for this intermediate-process chain: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_psmodulepath?view=powershell-7.3 . The helper now pins its own PSHOME/Modules before command discovery. This is a confirmed compatibility susceptibility, not yet proof of the truncated original failure stage. New sanitized stage/type/HResult output and full safe fixture-log assertions identify any remaining error. The copy-failure assertion now requires reaching COPY rather than accepting any earlier error. No SDK, local build or live app operation was performed for diagnosis. Owner VM SSH is currently closed; Omarchy sudo requires authentication despite the earlier authorization, so live update acceptance remains pending access.

## Existing-file replacement correction

At6a863499, the full sanitized failure is FILE_COPY_FAILED / COPY / MethodInvocationException /80131501. The same helper passes when creating an absent file (including the inherited-module case), but fails with an existing destination. File.Replace(...,$null) receives an empty string from PowerShell; its optional backup path requires a true null, supplied by [NullString]::Value. Microsoft API reference: https://learn.microsoft.com/en-us/dotnet/api/system.management.automation.language.nullstring . The existing two-file success scenario covers both create and replace paths and stays unchanged. Exact-head runtime closure is pending CI; no live installed app has been changed.

## Actual shared ZIP gate

Explicit shared build42107305209 atc1962419 compiles the Business binaries but rejects its produced ZIP at ValidateArchive's path/alias/link check. No artifact was uploaded and no feed was activated. The producer now uses framework ZipFile.CreateFromDirectory with the app base directory retained; consumer guards stay strict and report only fixed archive_separator/path/alias/link categories. This corrects the producer boundary without accepting noncanonical paths. The precise rejected original entry was not captured; a complete corrected ZIP run is required.

Hosting preflight is read-only: business-updates.ninitux.com resolves to the existing ingress through both authoritative DNS servers, has no existing site block or source root, Caddy is active in CT210 and free disk is5878194176bytes. A site snippet is prepared outside Git; nothing has been deployed. An unsupported installer-extraction utility was downloaded/extracted only into task-owned private staging and removed; no SDK/toolchain was installed. The owner was asked asynchronously to start the existing VM because its SSH proxy accepts TCP but closes without a guest SSH banner; sudo is not currently unattended.

## Confirmed package-path correction

The independent producer review identified the exact forbidden entry app/appsettings.json in dispatch42109479221 inventory. A sidecar-verified publicv2.50.5 reference ZIP independently reproduces it after identical entry-point stripping; the137-byte file matches tracked Service Logging settings. build.ps1 removes appsettings.*.json but leaves the bare appsettings.json. The Business shared producer now deletes this exact unused Service file before archiving; consumer JSON policy remains unchanged. This supersedes the earlier unconfirmed archive-primitive explanation. The owner started the VM; SSH now verifies WIN-IH9VDMDHUHM / WindowsPowerShell5.1.26100.6584. No new installer has yet been applied.
