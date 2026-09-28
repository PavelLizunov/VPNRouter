# Documentation instructions

Follow [the project contract](agent-contract.md) and root [AGENTS.md](../AGENTS.md).
This directory owns repository contracts, worker guidance and review prompts.

## Owners

- [agent-contract.md](agent-contract.md): project safety, Git, tests and release gates.
- [tag-retention-policy.md](tag-retention-policy.md): tag naming, retention and service exceptions.
- [test-workers.md](test-workers.md): worker roles, preflight and resource constraints.
- [REVIEW_AGENT_PROMPT.md](REVIEW_AGENT_PROMPT.md): bounded reviewer brief and evidence requirements.
- [execution-methodology.md](execution-methodology.md): task lifecycle and verification.
- [release-strategy.md](release-strategy.md) and [cut-stable-checklist.md](cut-stable-checklist.md):
  rolling-candidate policy and the fixed-WINBRAT live-update gate.
- [code-signing-signpath-runbook.md](code-signing-signpath-runbook.md): Windows signing enrollment.
- [interaction-contracts/README.md](interaction-contracts/README.md): Free Configs and Applications contracts.
- [android-development-methodology.md](android-development-methodology.md) and
  [android-keystore-backup.md](android-keystore-backup.md): Android workflow and signing-key backup.

## Editing

- Keep one owner for each rule. Link to global Harness guidance for model routing;
  project documents cannot grant tool permissions or override global safety rules.
- Verify source paths, test names and commands against the current branch.
  Mark historical observations as such rather than presenting them as readiness.
- Keep instructions in concise English with plain Markdown. Do not add decorative
  symbols, credentials, live subscription endpoints or raw logs.
- Preserve explicit owner authorization for merges, tags, releases and deployments.
  Documentation edits and green checks do not grant that authority.
