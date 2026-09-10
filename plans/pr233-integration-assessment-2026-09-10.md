# PR #233 integration assessment

This document began as a read-only assessment of e5594f14 against fa7685c5. The discovery sections below preserve that earlier checkpoint, not current toolchain or CI state. Current integrated product snapshot is c81c514e3cc2e103261155a1e7eb3f25e927b2c9 against accepted main f4b75f18b4ed6c2ced48e76735ff2ee8f9b58d21. See plans/pr233-platform-verification-progress-2026-09-10.md for current observed CI and native desktop packaging receipts. No runtime deployment performed.

Final bounded source refresh at c81c514e: PASS, no new confirmed merge-blocking source defect. Nine previously reviewed core/test/workflow/build blobs unchanged. Subsequent code change only adjusts Windows PowerShell extraction guard9->10 for added native verification block, retaining parsing of all blocks; actual Windows CI passed. Review does not independently establish remote build results or Android success. Android unsigned verification remains separately owned and pending; final merge acceptance not yet recorded.

## Historical integration preparation checkpoint

Owner clarified: complete remaining merge before the broader combined-main/anti-slop/performance verification. Ordinary #233 review and green exact-head gates still apply. Merge inputs: original e5594f14be1da949138d9f0594e8509e28e9260a and accepted main f4b75f18b4ed6c2ced48e76735ff2ee8f9b58d21. No use of old combined verification branch.

Owner authorized removing only stale registration for absent /tmp/vpnrouter-vpnctl-completion. Exact metadata moved to .git/cleanup-registration-backups/2026-09-10/vpnrouter-vpnctl-completion; checks confirmed same branch SHA and byte-identical seven other registrations. No branch, commit, worktree directory or user data deleted.

Merge conflicts: CustomConfigInjector FindRemoteDnsTag and OPEN-DEFECTS ledger. Injector working resolution retains both FakeIP exclusion from #233 and endpoint-aware WireGuard classification from #240. Local-dns test assertion adjusted to accepted8.8.8.8; explicit tunnel1.1.1.1 assertion retained. Ledger additive reconciliation in progress. Final source review, commit, exact-head CI and nonpublishing package gates pending. No runtime identity/deployment policy changes.

## Packaging verification routing constraint

Integrated release255 safeguards require exact release tag refs for Linux/macOS/Android workflow_dispatch even with upload_to_release=false (Linux build-linux.yml:47-49 independently read). Branch dispatch is rejected by design. Do not create/repoint a tag or weaken these guards to obtain pre-merge evidence. Required nonpublishing package evidence needs an independently scoped verification-only lane or preflighted exact-SHA workers with installed toolchains. Reviewer is checking the Windows route and smallest safe lane; no dispatch or privilege change performed.

Ledger working resolution verified by its worker: removing only additive migration note and six original VPNCTL records reconstructs exact accepted main ledger. VPNCTL04 stays deferred, five historical resolutions UNRELEASED, all existing NIGHT/external states preserved; current integration acceptance pending.

## Worker alternative assessment — 2026-09-10

Owner requested evaluating alternatives before adding a verification workflow. Read-only SSH preflight confirmed documented Linux/macOS identities; approximately53GiB/41GiB disk available. Neither exposes dotnet or Go on PATH; Linux also lacks Java on PATH. Standard dotnet locations checked explicitly: no Linux executable found; macOS /opt/homebrew/bin/dotnet reports only SDK8.0.125. Required SDK10.0.301/latestPatch not found in checked locations. Xcode26.6 is present. These are bounded discovery results, not proof no custom installation exists anywhere. No SDK installation, source checkout, build, cleanup or infrastructure change performed.

Existing Windows update workflow supports nonpublishing branch verification (omit optional version_label); Linux/macOS release workflows require tags, Android additionally uses production signing secrets. Thus currently established routes cannot provide all fresh package gates without either an approved isolated verification lane, approved toolchain provisioning, or an owner-supplied suitable worker/toolchain path. No tag/guard bypass or acceptance waiver inferred. Await route decision; merged source review alone is not package acceptance.

## Confirmed source observations

AntiCensorshipDnsTests.Generate_DnsServers_EmitTypedFormat114 expects local-dns 1.1.1.1, while accepted main emits 8.8.8.8. Preserve main default and update only the local assertion on integration. The fixture explicitly configures tunnel VpnDns=1.1.1.1, so its vpn-dns assertion must remain unchanged. This is source-predicted test failure, not an executed red test.

Bundled core migration is not proof of installed-runtime replacement: existing StartupPipeline.DeploySingBoxBinary copies only when missing or byte length differs; hot reload skips deployment. Existing TryColocateCronet also uses size comparison and tolerates missing bundle/copy errors. SingBoxFeatures preferentially probes bundle capabilities. Do not claim verified runtime migration from package builds alone. Changing deployment/adoption policy requires separately scoped approval; none performed here.

## Bounds

Preserve accepted release safeguards and DNS defaults, legacy Android/API23 deferral, parser and URL fixes. No release, deployment, P1 waiver or expanded FakeIP conversion authority inferred. Bounded reviewer report complete: desktop package target is official 1.14.0-vpnctl.3, with separately pinned legacy Cronet payload; Android stays tooling-libbox-singbox-1.13.10/API23. FakeIP conversion is deliberately limited to unambiguous legacy forms, rejecting ambiguous policies rather than guessing. Existing generated HTTPS/SVCB rejection is part of original migration, not new cleanup behavior or proven censorship mitigation. Recompute integration after #240; preserve release255 safeguards in overlapping build/workflow files. Exact merged tests plus nonpublishing Windows decoder/Cronet/update, Linux/macOS builds and legacy Android APK evidence are needed; ordinary Ubuntu tests alone cannot establish native decoder compatibility. Historical combined1fe721a9 results are not current acceptance. Runtime identity policy, ECH changes, broader FakeIP conversion and Android API24 remain separately scoped choices.
