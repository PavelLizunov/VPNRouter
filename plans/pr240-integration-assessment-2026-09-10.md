# PR #240 integration assessment

Read-only security-review/ponytail assessment of ae352fdbd0adbb1d4fe436e10a5b8b4cd747960c against accepted ae51a93d91773a48408c9fd699d8c496596bd907; common base b7ce0e4f. Immutable objects only, no new builds, remote CI queries or live verification.

## Current integration snapshot (frozen documentation handoff)

The assessment above compared historical immutable objects; the merge now in progress is original #240 head `ae352fdbd0adbb1d4fe436e10a5b8b4cd747960c` plus exact main source `673cedac4ae6ee8d5b533950c878b2e4d1a5102e` (`MERGE_HEAD`). Do not substitute the earlier `ae51a93d` comparison target for this integration source.

Observed merge conflicts: `plans/OPEN-DEFECTS.md` and `VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs`. This documentation handoff removes ledger conflict markers only; Git index resolution/staging and the characterization-test conflict remain parent-owned and pending acceptance. Integrated exact-head CI/tests and fresh correctness/security review are PENDING. No new builds, tests, remote checks or product review were performed by this documentation task.

The ledger preserves main's entries, order and statuses. Its single set of twelve product records stays unchecked pending integrated evidence and parent acceptance; original #240 scoped implementation resolutions remain historical evidence in the overnight section note and linked original reports. NIGHT-DOC retains accepted #235 resolution, NIGHT-MEASURE remains research-only/open, and the two original P1 follow-ups are added once, still open. External release-gate entries are unchanged; no gate closure or waiver is authorized. The shared original morning report is not edited.

Documentation-only verification PASS: byte comparison against `673cedac` confirms every existing ledger entry, checkbox and order unchanged, plus an identical external prefix and resolved-history suffix. All 16 NIGHT record IDs occur exactly once; the twelve product entries remain unchecked and ledger conflict markers are absent. The morning report bytes equal original #237 `7678e6ef`, original #240 `ae352fdb` and main `673cedac` (SHA256 `3c145d391345a15b903cfe16585f15040c309aabd5d6708ea9571ded55eb6256`). Scoped `git diff --check` passed. These checks verify documentation preservation only, not integrated product acceptance.

## Dependency and scope

Independent of #233/#235/#237 ancestry. Contains the same historical morning report as #237. Scope: 18 Core, 6 App, 34 tests, 5 plans; no release tooling, Android, version or parser changes. Do not merge historical combined verification branch 1fe721a9 as a replacement for current main.

## Integration order and retention

Finish #235 and preserve #237 audit, then integrate #240 independently of #233. Read-only merge simulation found conflicts in MainWindowViewModelCharacterizationTests.cs and OPEN-DEFECTS.md. Three auto-merged product overlaps need semantic review: MainWindowViewModel.cs, ConfigGenerator.Dns.cs, CustomConfigInjector.cs. Preserve main OpenUrl guard, current 8.8.8.8 resolver defaults, wgturn removal, parser/query secrecy fixes, URL validation and release repairs. Preserve current Windows public-surface hash unless actual exact merged snapshot demonstrates a justified change; retain private handler exclusion/assertion deliberately. Union ledger by IDs, never replace accepted statuses wholesale.

## Corrective review history — superseded by amendment

Final amendment: independent reviewer WITHDREW the confirmed-reachable-P1 claim below. polkit122 execv and exact build-pinned sing-box foreground signal loop contradict normal wrapper detachment; a Linux sudo fixture also waited for its child. Initial source reasoning proved only a conditional predicate and incorrectly treated an old comment as reachability evidence. No product fix or helper added, no P1 waiver. Bounded source review permits exact-head CI progression; native/full acceptance remains pending. Full evidence and failed macOS fixture limit: `phase-unix-launch-provenance-design-2026-09-10.md`. The following paragraph preserves the initial, now withdrawn assessment:

Bounded source review confirmed NIGHT-INTEGRATION-UNIX: given the supported exited-elevation-wrapper/live-child state, StartupPipeline accepts Unix API/warmup success but new OnConnected/CaptureReadinessGuard rejects null/current wrapper PID or exited handle; coordinator can then time out and stop the healthy child. Native incidence remains untested. Owner approved separate repair planning with owned-runtime identity and unchanged session/generation/warmup safeguards, no foreign API/port adoption. Existing helpers are being assessed before choosing implementation; no correction or current CI acceptance claimed.

The characterization conflict is resolved in the worktree using accepted main Windows pin and the exact non-public OnEngineConnected(int) assertion/exclusion. Source isolation review found no safety blocker for expanded Windows CI selectors on disposable runners: fake process/firewall seams and temporary files, plus bounded loopback UDP (not exclusively mock networking). No external live VPN tests claimed. Ledger gained the new pending integration finding after the earlier frozen preservation checks above; original entries remain retained.

## Existing unresolved findings

NIGHT-FOLLOWUP-01 remains source-confirmed: StartWithJsonCore/RestartCore catches can release TUN ownership after Started callback throws while live handle remains. NIGHT-FOLLOWUP-02 remains source-confirmed: SafeMode synthetic FullTunnel profile does not necessarily change settings.App.RoutingMode consumed by config/firewall generation. Both are existing P1 records in original #240 ledger, not newly introduced vulnerabilities or remotely reproduced exploits. Keep open on ledger integration; neither fix nor waiver is authorized by ordinary cleanup. SafeMode routing and broader ownership/adoption policy require separate substantive decisions.

## Evidence limits and acceptance

Original product/tests last changed at 489a529b; later ae352fdb changes docs only. Historical report cites CI33974410438 and grep33974410445, Ubuntu3277 passed/75 skipped, 14 witness cases plus two controls. These are inspected historical receipts, not current execution proof. Full solution, native nft/pf/TUN/process termination, live websocket and UI gates remain unverified. New exact integrated CI and focused Windows NIGHT/characterization plus accepted regression checks required before merge. No release, deployment, Android migration, FakeIP expansion or P1 waiver authority inferred.
