# Business version rollback and mandatory Windows acceptance

## Why and scope

The owner requires real Windows verification of Business updates and the same explicit older-version rollback available in Standard desktop. Continue the owned dsh/business-pilot branch and PR646. Preserve PR652 and its verified2.50.7 Romania changes without merging or modifying its checkout. Incorporate that exact presentation/test delta in the next2.50.8 implementation so the follow-up cannot regress the issued UI.

## Design and invariants

Reuse IUpdateSource.ListOlderAsync, UpdateNotificationViewModel's existing history/confirmation/download/apply commands and the Business-only installer. Add an optional bounded releases array to the existing trusted latest manifest, with complete per-version edition/architecture/size/SHA validation. Deployed5/6/7 clients retain the existing top-level latest fields and ignore the compatible history field. Normal checks offer only newer stable versions; only an explicitly selected, admitted older-version object may enter rollback. Keep original offer/path pinned across overlapping checks/history/downloads. No Standard fallback, executable server responses or configured public installer.

Expose compact localized version history and explicit confirmation in the Business menu. Reuse the private downgrade config backup before apply, fail without applying/exiting if preparation/backup fails, retain protected provisioning/account/cache/selection/mode/language and unrelated processes/services. No broad engine cleanup. Older approved packages remain immutable; history availability is maintained on the server.

## Sequence and rollback

First commit this brief. Then implement source/catalog, shared VM/UI bindings, downgrade backup and meaningful consumer/concurrency tests in one verified block, retaining the2.50.7 Romania delta and advancing the package version to2.50.8. Build only at exact source in CI. Review correctness/security/coverage separately, plus the owner-requested final GPT ASTRA XHIGH review. Publish only verified generic packages/catalog. Retain existing private installers for recovery; no tag, GitHub release, PR merge or main push.

## Mandatory Windows acceptance

The current explicit Windows-test request uses the canonical WINBRAT worker for this new scope, following the owning pool lease and tools/brat-verify.ps1. This is a new task lease, not reuse of the released November4 lease. Omarchy's guest remains inaccessible without OS authentication; do not ask the owner to read version/settings or trigger host root prompts. Exercise actual private bridge install, existing-version-to2.50.7 update, new2.50.8 update, explicit older-version rollback, full process relaunch and exact installed-file hashes, protected profile retention and bounded split/full lifecycle/dataplane checks. No live desktop screenshots; use headless340/420 RU/EN renders. Clean task install/data/probes/staging and release the lease.

## Risk and gates

Risk HIGH: executable downgrade and configuration compatibility. Preserve checksum/PE/resource/path guards, require user confirmation and a private config backup; failure must not relaunch a partial replacement. Existing per-file replacement remains nontransactional and is documented.

1. Build: PENDING exact-source solution and Business package CI; no builds/SDKs on Harness.
2. Tests: PENDING source/catalog bounds, version admission, explicit confirmation, forged-object rejection, true pending-download/check overlap, backup failures and command-to-prepare ordering, plus full Linux/Windows suites.
3. Documentation: PASS initial brief; final observed outcome pending.
4. Independent review: PASS three distinct native source lenses with own implementation excluded; no confirmed new P0/P1/P2. Final ASTRA XHIGH remains pending. Composed fault-injection coverage limits are recorded in the ledger.
5. Windows/UI: IN PROGRESS after canonical worker admission; deterministic RU/EN340/420 and real update/rollback/relaunch are mandatory before completion.
6. Characterization/integration: PENDING Standard update behavior and MainWindowViewModel public API retention; new feature must not alter routing/service ownership.

## Initial preflight and confirmed gap

PVE1: load3.25/2.63/2.06,30908MiB total/21747MiB available RAM, no swap,316GiB free disk. WINBRAT100 is stopped; Linux101 is active under another owner's vpnctl-full-node-traffic-20261106 lease. Queue Windows with stable request vpnrouter-business-rollback-win-20261106 and3600second admission wait; never stop or borrow another job. Current Business source has no ListOlderAsync implementation or history UI, and its apply branch returns before Standard's downgrade backup. No actual rollback is claimed.

## Implementation snapshot and worker admission

The queue admitted this task after the other owner released Linux101. WINBRAT identity is verified:8logical CPUs,0percent load,13815588KiB free RAM,124655112192bytes free disk, pagefile2432MiB with0used, no competing build jobs, Business install/data, core or TUN. Windows PowerShell5.1.17763.9376 and one interactive Explorer session are present. This is runtime-only acceptance; no SDK or build is used there.

The implementation reuses desktop history commands and Business staging/apply, adds a maximum12-entry optional catalog inside the existing16KiB/depth6 document, and pins a downloaded offer/path atomically. The menu history includes explicit confirmation/cancel and uses shared localized text. Downgrades create the existing private config backup before the Windows helper can start. New source, backup and real command-to-MainWindow preparation tests include true pending-download barriers, rejected/forged rows, failure/no-exit paths and340/420RU/EN popup renders. Version2.50.8 retains the exact Romania presentation/tests from952e23ba. Source reviews, executable tests and live acceptance are separate gates; no Windows rollback pass is claimed by this implementation record.

The private bridge5 install and automatic bootstrap pass on WINBRAT with16backing entries/six physical choices, Russian UI, protected provisioning and preserved Standard configuration/service state. The task-only update7 scenario currently stops at its expected-offer assertion; diagnose the client/check result before classifying this as a product or verifier defect. No download/apply pass is claimed yet.
