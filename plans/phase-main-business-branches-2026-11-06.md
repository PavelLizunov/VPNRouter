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

## Outcome

In progress. Initial inventory101 remote heads,14 open PRs,17 local worktrees. Complete-history bundle verification passes for101 heads;310883470bytes. One foreign root has7changes and remains untouched. gh-pages has37regular files/56800902bytes and currently hosts the installation site/APT; migration is required before removal. No branch deletion has occurred at brief creation.
