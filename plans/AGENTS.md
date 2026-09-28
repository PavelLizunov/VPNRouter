# Planning instructions

Follow [the project contract](../docs/agent-contract.md) and root [AGENTS.md](../AGENTS.md).
Keep task briefs, outcomes, roadmaps and durable project notes under `plans/`.

## Task records

- Use `phase-<slug>-YYYY-MM-DD.md` for a task brief; update the same record through delivery.
- Record intent, scope, invariants, verification, unknowns and observed outcomes.
- Use `vpnrouter-vX.Y.Z-<topic>.md` for release roadmaps and
  `session-handoff-YYYY-MM-DD.md` for handoffs. Release-note drafts remain gitignored.
- Preserve historical records. A dated plan, map or test receipt is evidence for
  its named snapshot, not a current contract, open-defect status or release approval.

## References

- [OPEN-DEFECTS.md](OPEN-DEFECTS.md): release-gating ledger; record verified findings and their disposition.
- [v3.0-refactor-roadmap.md](v3.0-refactor-roadmap.md): refactor roadmap and task ownership.
- [v3.0-execution-methodology.md](v3.0-execution-methodology.md): task lifecycle and verification.
- [interaction-contracts/README.md](interaction-contracts/README.md): Free Configs and Applications interaction contracts.
- [vpnrouter-release-strategy.md](vpnrouter-release-strategy.md): rolling-candidate policy.
- [cut-stable-checklist.md](cut-stable-checklist.md): fixed-WINBRAT live-update gate.
- [macos-parity-leak-dns-firewall-update-qa-plan-2026-06-04.md](macos-parity-leak-dns-firewall-update-qa-plan-2026-06-04.md): macOS QA follow-ups; recheck current status.
- [code-signing-signpath-runbook-2026-07-10.md](code-signing-signpath-runbook-2026-07-10.md): signing runbook; recheck prerequisites.

## Boundaries

Use matching project skills and the global Harness delegation policy. Do not
hardcode model routes, worker credentials or volatile host addresses in plans.
Worker roles and preflight belong to [docs/test-workers.md](../docs/test-workers.md).
