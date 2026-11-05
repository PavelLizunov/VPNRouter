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

1. Build: PASS exactd2a5781f ordinary CI (12SUCCESS/3expectedSKIPPED) and explicit shared-package dispatch42112056656. The actual ZIP passes producer-to-consumer PE/resource/path validation before upload.
2. Tests: PASS exactd2a5781f applicable Linux/Windows/Business, characterization and Standard auto-update gates. Earlier helper failures are corrected: own WinPS module lookup, true null for File.Replace, source/manifest tamper refusal and isolated data-directory tests pass. Earlier failure counts below remain historical evidence.
3. Docs: PASS design, manifest/package contract and pilot instructions updated; delivery outcome still pending.
4. Review: PASS SOURCE-ONLY. Three native reviewers used distinct correctness/concurrency, security and producer/test lenses with own-file exclusions. Stop-isolation, offer-race, helper-shadow and mixed-App/Core findings are corrected and covered; coordinator also serialized two global-data-dir test classes. No external model or runtime acceptance is implied.
5. UI/runtime: PASS inspected deterministic RU/EN340/420 captures atd2a5781f (same product implementation before the version-only successor). PARTIAL live bridge acceptance below; actual in-app2.50.5-to2.50.6 update, dataplane and task cleanup remain pending. No live desktop screenshots.
6. Characterization/integration: PASS atd2a5781f, including actual generic publication and complete archive validation. Three expected skipped private/dispatch jobs on normal PR CI. The version-only successor requires its own exact-head gate.

## Outcome

Corrected implementationb247c037/2.50.6 passes all applicable CI, shared ZIP and private installer gates. The validated generic feed is active and the private final installer is delivered to Omarchy Downloads. The owner guest currently has the update-enabledd2a5781f/2.50.5 bridge; live in-app update acceptance remains pending because the VM stopped. Activity/icon fixes30176a85 have full green CI; live activity was confirmed fresh on four nodes across repeated collection cycles. Current direct-route outage cause remains unknown; audited management jumps restore telemetry.

## WinPS correction evidence

The CI parent is pwsh7, which launches dotnet and then Windows PowerShell5 through Process.Start. Microsoft documents incompatible PSModulePath inheritance for this intermediate-process chain: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_psmodulepath?view=powershell-7.3 . The helper now pins its own PSHOME/Modules before command discovery. This is a confirmed compatibility susceptibility, not yet proof of the truncated original failure stage. New sanitized stage/type/HResult output and full safe fixture-log assertions identify any remaining error. The copy-failure assertion now requires reaching COPY rather than accepting any earlier error. No SDK, local build or live app operation was performed for diagnosis. Owner VM SSH is currently closed; Omarchy sudo requires authentication despite the earlier authorization, so live update acceptance remains pending access.

## Existing-file replacement correction

At6a863499, the full sanitized failure is FILE_COPY_FAILED / COPY / MethodInvocationException /80131501. The same helper passes when creating an absent file (including the inherited-module case), but fails with an existing destination. File.Replace(...,$null) receives an empty string from PowerShell; its optional backup path requires a true null, supplied by [NullString]::Value. Microsoft API reference: https://learn.microsoft.com/en-us/dotnet/api/system.management.automation.language.nullstring . The existing two-file success scenario covers both create and replace paths and stays unchanged. Exact-head runtime closure is pending CI; no live installed app has been changed.

## Actual shared ZIP gate

Explicit shared build42107305209 atc1962419 compiles the Business binaries but rejects its produced ZIP at ValidateArchive's path/alias/link check. No artifact was uploaded and no feed was activated. The producer now uses framework ZipFile.CreateFromDirectory with the app base directory retained; consumer guards stay strict and report only fixed archive_separator/path/alias/link categories. This corrects the producer boundary without accepting noncanonical paths. The precise rejected original entry was not captured; a complete corrected ZIP run is required.

Hosting preflight is read-only: business-updates.ninitux.com resolves to the existing ingress through both authoritative DNS servers, has no existing site block or source root, Caddy is active in CT210 and free disk is5878194176bytes. A site snippet is prepared outside Git; nothing has been deployed. An unsupported installer-extraction utility was downloaded/extracted only into task-owned private staging and removed; no SDK/toolchain was installed. The owner was asked asynchronously to start the existing VM because its SSH proxy accepts TCP but closes without a guest SSH banner; sudo is not currently unattended.

## Confirmed package-path correction

The independent producer review identified the exact forbidden entry app/appsettings.json in dispatch42109479221 inventory. A sidecar-verified publicv2.50.5 reference ZIP independently reproduces it after identical entry-point stripping; the137-byte file matches tracked Service Logging settings. build.ps1 removes appsettings.*.json but leaves the bare appsettings.json. The Business shared producer now deletes this exact unused Service file before archiving; consumer JSON policy remains unchanged. This supersedes the earlier unconfirmed archive-primitive explanation. The owner started the VM; SSH now verifies WIN-IH9VDMDHUHM / WindowsPowerShell5.1.26100.6584. No new installer has yet been applied.

## Delivery preparation and newer-version proof

Shared dispatch42112056656 succeeds at exactd2a5781f. Downloaded ZIP:73472471bytes, SHA256ed2f680ddd9248fa6c46ab3dd06c6c6ae7deb584585954834a5a8a1809c0d413,278entries, Business/win-x64/2.50.5/external-provisioning receipt. Source/sidecar checks match; bare appsettings.json is absent.

The owner restarted the guest. Read-only preflight confirms its pinned identity, admin token, one app in interactive session1, no core process,25percent CPU,10681256KiB available RAM and41686781952bytes free disk. The installed App resource was read as bytes without executing the assembly. Its private bundle hash matches the prior delivery receipt; all13entries/fourphysicalhosts and existing service fields are unchanged. Only the trusted update-feed address is added to the protected bridge input. Exact-value/encoding comparison of19private values against1496tracked files finds no matching paths; this is a scoped current-tree check, not a complete Git-history secret audit.

Private bridge dispatch42113768880 is pinned tod2a5781f/2.50.5. AppVersion advances to2.50.6 in the next isolated block so that a real strictly-newer generic update can be exercised. This is a task-branch package version, with no tag, GitHub release or merge. The bridge installer and subsequent generic ZIP must each pass their own packaging gate before use; final live update/provisioning acceptance remains pending.

## Verified 2.50.6 artifacts and publication

Exactb247c037 ordinary CI is green:12SUCCESS/3expectedSKIPPED. Linux3621passed/81skipped; Windows3788/26; Business380/6; Standard auto-update integration succeeds. Generic dispatch42114121659 and private dispatch42114127149 both succeed at this source. Private first-run validation and native split/full config checks pass; these are not live dataplane tests. Private CI preflight:4CPUs,8percent load,13983348KiB available RAM,27.69GiB free disk, zero competing builds.

- Generic ZIP:73472519bytes, SHA256457a8fd2a9b6523b654438094ef5ed886899a88f6cb415aa3c1e358b57a731f0;278files; App DLL SHA256a082a7f3cd6cfda7d0bd7128c0c491a87d20546fd5ad5cd3763abe46ac218930. CI validates actual PE edition/version/resource identity and full archive admission. Exact private-value/encoding scan of33values finds no credential/provisioning matches; a common public TLS SNI also occurs in existing App/Core defaults and is explicitly classified separately.
- Private installer:VPNRouterBusiness-Setup-v2.50.6.exe,63045060bytes, SHA256aa179ea8101d3a88868dbf22feaa9cf433fcf4b7626dc43d29138e044f507ded. Copied with source/hash/guide to the owner's Omarchy Downloads/VPNRouterBusiness-Recovery-2026-11-05; remote hash rechecked, directory0700/files0600. Never publish this configured installer.
- Trusted feed:https://business-updates.ninitux.com/windows-x64/latest.json. Versioned credential-free ZIPs are served by the existing ingress Caddy from /srv/vpnrouter-business-updates, with the dedicated conf.d/vpnrouter-business-updates.caddy site. No DNS records changed; unrelated Caddy configuration hashes remain unchanged. Whole configuration validation/reload succeeds; trusted HTTPS JSON and no-store manifest behavior are observed. The2.50.5 asset was independently downloaded over HTTPS and its full hash matched;2.50.6 manifest matches the validated archive. Actual client download remains pending.
- Both ephemeral GitHub pilot secrets are removed and verified absent after artifact retrieval. Protected local build inputs and task staging remain only for the unfinished acceptance/recovery; their final cleanup is pending.

## Partial live bridge acceptance and current blocker

The owner-started guest was verified through pinned SSH and an isolated RDP session using existing credentials supplied through stdin, with no host root prompt or desktop capture. Graceful tray exit, installer exit0, exactd2a5781f installed source and interactive relaunch pass. Protected provisioning has the expected15414131c25eabd29302abbe88979e212db3e3a0025420b789952f3802ccbe3a hash/ACL. Corporate UUID set, selection/mode/Russian language and all three existing report archives remain unchanged.

The provider now returns15entries/fivephysical VLESS hosts; the previous13entries/fourhosts are all retained. A trusted subscription GET independently matches those counts. The task-only verifier's fixed13/4 assertion was therefore invalid; it now permits validated growth while requiring every prior entry, account, source, selection/mode/language and report bytes. Cache refresh is recorded, not treated as corruption.

The initial task-only focus check trusted an immediate AppActivate return. It now verifies the actual foreground PID with a bounded activation helper. A later ready check remains incomplete. RDP then ended with RPC-initiated disconnect; guest SSH closed, host RDP listener disappeared and read-only process inspection confirms zero QEMU processes. The reason the VM stopped is not established. Host sudo -n still requires authentication despite the owner's existing authorization. A secure T3 sudo credential request is pending to restart the exact VM and enable its narrow unattended management path. No root popup, unrelated VM action or claim of successful self-update is made.

Prepared private scripts cover real menu check/download/apply/relaunch, all installed ZIP file hashes, provisioning retention, current rows and bounded two-node split/full/split acceptance. They must still execute successfully on the recovered guest. UIS, long-memory, every-rule/DNS/UDP and office acceptance remain outside this proof.
