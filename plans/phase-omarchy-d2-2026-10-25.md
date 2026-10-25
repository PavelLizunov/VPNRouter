# Linux backend lifecycle and hygiene

## Intent and scope

Base c1bafab1ec7523ce9faef08f1634470a6c6a9785, branch
`dsh/omarchy-night2-backend`, PR 622. Compare shell-independent session
ownership options in a <=60-line design note. Trace EOF through ProtocolServer,
RouterSession and the owned Core stop path; add the missing connected-session
transport regression. Audit only Linux additions against origin/main using the
repo-hygiene scripts, verify documentation consumers, and replace round-named
checks with behavior names. Surface subscription refresh failures as safe
protocol code/message errors while preserving cached servers and atomic mutations.

## Contract and approach

No service/socket migration or automatic reconnect in this batch. Preserve
stdio framing, bounded shutdown, exact process ownership and existing platform
behavior. No network/VPN operation, shell action, deployment or local .NET build.
The lead supplies final independent review. Read code before accepting scanner
candidates; keep durable references and historical evidence that still has value.
Record each confirmed finding in OPEN-DEFECTS before implementation.

Risk: low for documentation/test renames; moderate for refresh error behavior.
Rollback: revert the task commits. No schema or persisted connection intent added.
Verification: local read-only hygiene scripts/diff checks, exact-head test.yml and
arch-headless.yml in CI; distinguish fake-engine tests from kernel route evidence.

## Gates and outcome

1. Build: pending CI scoped builds; full solution build not scheduled.
2. Tests: pending CI Headless, Linux/Windows Core and Arch offline checks.
3. Docs: pending design note, scoped fact checks and outcome.
4. Review: direct review pending; independent Claude lead acceptance pending.
5. UI/live: N/A for this local-reading/CI-only round; kernel ip rules unverified.
6. Consumer characterization: pending EOF and structured-error boundary checks;
   renames must preserve all test bodies and runner registration.

## Implementation checkpoint

- Added 49-line session-lifetime comparison; recommend independent user service
  and socket. No runtime intent persistence; README now exposes shell coupling.
- Refresh rejects zero usable servers with a fixed safe error before saving;
  post-fetch cancellation takes precedence. Eight hermetic wire/persistence
  cases cover HTTP, transport, empty, invalid, cancellation, partial batch,
  success and no targets. The existing detached candidate makes failure atomic.
- Added connected-session EOF coverage through the real adapter chain to a fake
  engine. Existing Core tests assert Terminate before Kill and retained ownership
  on failed exact stop; no fake assertion claims to inspect host ip rules.
- Hygiene scripts: two broken README links corrected; one unused admission helper
  removed after caller/registration inspection. Renamed two test classes/files;
  explicit substitution comparison proves all fixture/assertion bodies preserved.
- Removed only phase-omarchy-resume-2026-10-07.md: obsolete step history with one
  active map reference, now replaced. Recover with git show c1bafab1:plans/phase-omarchy-resume-2026-10-07.md.
  Other Omarchy plans retain distinct scope/verification receipts. Root README
  pair and Arch README were read; no changed claims there require edits.

Local checks so far: fact_check on Headless AGENTS/README, Arch README and design
note: zero broken paths/links, zero unknown symbols; Headless dead_names: zero
remaining candidates; git diff --check: clean. No local .NET execution.
Prepared brief d41e0119 passed Android compile CI 40179218276; repository verifier
returned exit 0 before implementation. Product CI is still pending.
