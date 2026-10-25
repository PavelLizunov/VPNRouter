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
