# Windows direct update and main/business branch consolidation

## Why and authority

The owner requests a fresh real Windows update from the old Business client to the delivered version, and only main and business branches in GitHub. This supersedes the previous no-merge handoff for the consolidation described here. It does not authorize merging unreviewed feature proposals or changing immutable release artifacts.

## What and invariants

- Execute the existing configured bridge2.50.5 directly to generic2.50.8 through the actual Windows update command. Keep the issued source ed2c570c and private installer unchanged; no rebuild.
- Preserve all101 remote branch tips and complete history in a verified private Git bundle before deleting refs. Record SHA256, fresh-tip comparison and recovery instructions. Preserve tags/releases and foreign worktrees, including the dirty root.
- Merge the accepted hygiene PR650 into business. Retarget the accepted Business PR646 to business, integrate the hygiene changes, pass exact-head checks and merge it. Keep main as the Standard line.
- Preserve the existing Pages website, signed APT repository, custom domain and HTTPS while migrating publication from gh-pages to GitHub Actions. Use a narrow main-based workflow/docs/test PR; carry that maintenance into business. Disable the obsolete publisher before deleting gh-pages.
- Close superseded or unreviewed task PRs with a reason and archive reference. Delete their remote heads only after comparing the current SHA with the verified archive. Do not remove uncommitted local work or silently include abandoned code in a product branch.

## How, risk and rollback

The protected archive is outside the public repository. A bundle verification must establish complete history without prerequisites. Restore an archived branch with git fetch <bundle> refs/heads/<name>:refs/heads/<recovery-name>, then explicitly push the chosen recovery ref. Update the archive before deleting a changed tip.

Branch deletion can lose discoverability; the inventory, PR disposition and verified bundle preserve recovery. Pages migration can interrupt installation/downloads; retain gh-pages until an Actions deployment succeeds and the public content is verified. Restore the legacy Pages source if migration fails. Existing immutable tags and signed APT bytes remain unchanged. No force-push to product branches.

Windows acceptance uses the official exclusive WINBRAT lease and canonical task-owned verifier plus the already reviewed extension. Stop immediately on identity/ownership mismatch. Preserve provisioning, account, settings, selected node, language, routing mode and Standard baseline. Remove only owned install/data/tasks/staging and release the lease after acceptance. No diagnostic upload or desktop screenshot.

## Verification and six gates

1. Build: existing immutable2.50.8 receipts remain applicable to that binary. No new product build on Harness/Omarchy. Applicable CI builds must pass at each merged source head.
2. Tests: real direct5-to8 update, complete payload hash comparison and retained profile; focused workflow contracts and exact-head PR suites for publication changes. No invented runtime claims for unrelated nodes/features.
3. Documentation: this outcome, sanitized Windows receipt, branch inventory/dispositions, archive verification and recovery path.
4. Review: independent native source/safety review for consolidation and publication; preserve the completed Business and integration ASTRA XHIGH reviews. No external repo-hygiene model.
5. Windows/UI: actual menu/check/download/apply/relaunch on WINBRAT, canonical verifier provenance, cleanup and lease release. Previous rollback/dataplane evidence remains separately scoped.
6. Integration: only main/business remote heads remain; main stays Standard; business contains accepted Business, hygiene and publication maintenance. Website/APT/domain/HTTPS remain operational, tags/releases unchanged and foreign root status retained.

## Observed preflight

PVE runtime admission:16logical CPUs, load1.97/1.73/1.75,30908MiB RAM total/24415MiB available, no swap,316GiB disk free. Official request vpnrouter-business-direct-update-20261106 admitted sequence34 after the previous request released. WINBRAT:8logical CPUs,1percent CPU,13953600KiB available RAM,124676890624bytes disk free,2432MiB pagefile/0used,0competing builds/apps/cores/TUN, no Business data. Windows PowerShell5.1.17763.9376. Runtime only, no SDK used.

## Outcome at final integration submission

Windows direct acceptance: PASS old configured bridge2.50.5 automatic bootstrap, actual update check, real download/apply/relaunch directly to2.50.8 and all278 installed payload hashes. Exact target source ed2c570cdd6f95e74ce2a081526f4c770314a441; no installer rebuild. Account/provisioning,16backing entries/six physical choices, selected choice, routing mode, Russian language and Standard config/service baseline retained. Subscription refresh legitimately changes cache bytes. This direct-update scenario makes no new dataplane/node/UIS/memory claim; the separately recorded prior rollback/two-reachable-node acceptance remains scoped to its evidence.

Owned Windows install, data, registry, tasks, core/TUN and entire staging root are removed; final counts0. Lease vpnrouter-business-direct-update-20261106 is RELEASED, sequence34. Tray UIA remained unavailable, so cleanup used only the identity/source/path/session/start-handle-verified idle App process; graceful tray exit/visual acceptance is not claimed. No report was uploaded. See business-direct-update-winbrat-2026-11-06.json, including executed canonical/script hashes. Canonical tracked tools are unchanged.

Initial inventory101 remote heads,14 open PRs and17 local worktrees. Complete-history archive VPNRouter-all-branches-before-cleanup.bundle is310883470bytes, SHA2567bc4c533563b8d60c5bf64d4f4ec46aa2423a5b9b30fc58b0a3022889358fd7b; git bundle verify passes without prerequisites. Protected delivery/cleanup evidence is retained outside the repository under Downloads/VPNRouter-BranchCleanup-2026-11-06. No tags/releases were removed or rewritten. The foreign root with7changes and foreign local worktrees/stashes remain untouched.

PR650 exact0c84d4ba passes11SUCCESS/two expectedSKIPPED and merges into business atd629520f. PR646 is retargeted to business, receives a union-only ledger conflict resolution and independent clean source review, passes12SUCCESS/three expectedSKIPPED atf9badcf9, and merges atb8edac72. All current Business findings and RH01-RH08 remain. Twelve other open task PRs are closed unmerged with explicit reasons and exact archived heads; PR652's four Romania files are already retained and its version7 is superseded by8. Unreviewed dependency/feature proposals are archived, not silently included. The first guarded atomic deletion removes96 exact archived remote tips; its five retained refs are the two product lines and the then-needed task/site refs.

Pages/APT preservation: PR653 exact7417f808 passes all7checks and merges into main atcf919e89. Actual migration42215497802 succeeds; all36public files/56800902bytes compare byte-for-byte with the original site, including the existing signed APT indexes/key/DEB and installer/help/Android files. Domain and HTTPS remain. The one-time workflow is disabled; only canonical release tags remain eligible for the Pages environment. The inactive deleted legacy workflow cannot be disabled, so an independently reviewed active creation-only rule for exactly gh-pages, with no bypass actors, is confirmed effective. This does not restrict normal task/product branches. See pages-actions-acceptance-2026-11-06.json and phase-pages-actions-2026-11-06.md.

Three independent native Pages source lenses find no confirmed P0/P1/P2;12actual admission cases and static YAML/bash/Python checks pass. An independent staged integration review verifies all14implementation files match accepted main, App/Core/Android are untouched, strict canonical verification survives and no private inputs enter the delta. The already completed Business/rollback/integration ASTRA XHIGH reviews remain snapshot-scoped. No external repo-hygiene model is used.

The final Business integration includes the exact Pages acceptance commit69196b29 from the narrow main documentation PR654, plus this sanitized Windows receipt/outcome. Merge654 first after its own green checks; merge this Business integration only after exact-head CI and the lower PR acceptance. Then archive any newer task tips, remove only the remaining temporary/service refs with SHA guards, and verify actual remote refs are exactly main/business, live Pages/HTTPS remains correct and the foreign root status is retained. These final PR gates/ref deletions are pending at this committed evidence snapshot; the GitHub/private execution receipts establish their later outcome without rebuilding the issued EXE.
