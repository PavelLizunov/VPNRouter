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

1. Build: PENDING exact-source solution/Business/generic publication CI.
2. Tests: PENDING full Linux/Windows, focused Business source/provisioning/package/helper/UI tests. Synthetic tests never use private credentials or real endpoints.
3. Docs: PASS design, manifest/package contract and pilot instructions updated; delivery outcome still pending.
4. Review: PASS SOURCE-ONLY. Three native reviewers used distinct correctness/concurrency, security and producer/test lenses with own-file exclusions. Stop-isolation, offer-race, helper-shadow and mixed-App/Core findings are corrected and covered; coordinator also serialized two global-data-dir test classes. No external model or runtime acceptance is implied.
5. UI/runtime: PENDING deterministic RU/EN340/420 captures and actual bridge-to-newer-package update on the owner-authorized VM, with preserved account/selection/mode and cleanup. No live desktop screenshots.
6. Characterization/integration: PENDING exact-head Standard public-surface checks, actual generic PE/resource assurance and archive receipt validation. Three expected skipped private/dispatch jobs on normal PR CI.

## Outcome

Implementation complete for initial CI; no updater-enabled installer or live feed claimed yet. Existing installer remains ae1276b0. Activity/icon fixes30176a85 have full green CI; live activity was confirmed fresh on four nodes across repeated collection cycles. Current direct-route outage cause remains unknown; audited management jumps restore telemetry.
