# Linux backend lifecycle and hygiene

## Intent and contract

Base c1bafab1ec7523ce9faef08f1634470a6c6a9785, branch
`dsh/omarchy-night2-backend`, PR 622. Compare session lifetime options, trace EOF,
add the missing connected-session transport regression, audit Linux additions
against origin/main, and surface subscription fetch failures as safe errors.

Preserve stdio framing, bounded stop, process ownership and non-Linux behavior.
No service/socket migration, automatic reconnect or new persisted intent in this
batch. The owner limited this round to local reading/edits and CI: no local .NET
build, VPN operation, shell action or deployment. Independent acceptance belongs
to Claude lead. Record confirmed findings in OPEN-DEFECTS before implementation.
Risk: low for docs/renames, moderate for refresh failure semantics. Rollback:
revert the scoped task commits; no schema or installed-state migration.

## Changes

- The 49-line [session lifetime note](omarchy-session-lifetime.md) recommends a
  systemd user service with a Unix socket. Reconnect cannot close the traffic
  gap without a Linux kill-switch, and adequate intent semantics exceed 80 lines.
  README now states the shell-coupled lifetime; the P1 remains open.
- Zero-server refresh results now raise subscription_refresh_failed before Save.
  Post-fetch cancellation takes precedence. Detached candidates keep batch
  failures atomic, including an earlier successful fetch followed by a failure.
  Eight fake-HTTP wire/persistence cases cover failure and success controls.
- Connected-session EOF traverses ProtocolServer -> handler -> backend -> session
  and stops/disposes a fake owned engine exactly once. Existing Core tests cover
  Terminate before Kill and retained lease on failed exact stop. Kernel ip rules
  remain unverified; SIGKILL can bypass graceful route cleanup.
- Fixed two README links, removed the stale map/comment references and the unused
  IsUrgentOrReadOnlyMethod helper. Renamed Round4Checks/Round5Checks by behavior;
  explicit substitutions preserve all fixture/assertion bodies and registrations.
- Deleted phase-omarchy-resume-2026-10-07.md (75 lines), an obsolete step-history
  dump whose only active reference was the zone map. Recover with
  git show c1bafab1:plans/phase-omarchy-resume-2026-10-07.md. Other Omarchy plans
  retain distinct scope/receipts. Root README pair and Arch README remain accurate.

## Observed gates at code head 17752be7

1. PASS, scoped builds: test.yml and Arch package/install/upgrade succeed. Full
   solution/release/all-platform builds were not run.
2. PASS: CI 40179759509, Headless 41/0; Linux 3217 passed/77 skipped;
   Windows 3385 passed/20 skipped. Arch 40179761405 passes offline acceptance.
3. PASS: fact_check has zero broken links/unknown symbols in the four scoped
   documents; Headless dead_names has zero remaining candidates; scoped diff
   whitespace is clean. Census/consumers evidence is in the external D2 report.
4. BLOCKED for independent acceptance: direct source/diff review completed;
   Claude lead reviews after handoff. No nested reviewer or independent-pass claim.
5. N/A: no UI/live scenario authorized this round; real EOF/auto_route, crashes,
   egress/leaks and orphan rules were not tested. No installed/loaded-state claim.
6. PASS, bounded consumer checks: actual serialized protocol errors and complete
   Headless EOF disposal chain with fake HTTP/engine; rename equivalence checked.

## Delivery evidence and correction

Brief d41e0119 passed Android CI 40179218276. Product 04089901 passed EOF and
renamed suites but failed the new refresh success control: its Trojan fixture
was unsupported by ServerUriParser. Test-only 17752be7 uses a supported Shadowsocks
URI matching existing parser tests. No product rollback or weakened assertions.
Native old-code red/green was not run. Exact-head repository CI verifier at
17752be7 returned exit 0: eight green, no tolerated failures or pending/red checks.

Final documentation checkpoint SHA/CI, exact remote identity and artifact receipt
are recorded in the owner-requested external scratchpad/n3/d2.md report.
No release/tag/merge/deployment. Documentation prose gate: grounded claims,
source-backed paths and explicit acceptance limits; UI gate is not applicable.
