# Documentation instructions

Follow [the project contract](agent-contract.md) and root [AGENTS.md](../AGENTS.md).
This directory owns repository contracts, worker guidance and review prompts.

## Owners

- [agent-contract.md](agent-contract.md): project safety, Git, tests and release gates.
- [test-workers.md](test-workers.md): worker roles, preflight and resource constraints.
- [REVIEW_AGENT_PROMPT.md](REVIEW_AGENT_PROMPT.md): bounded reviewer brief and evidence requirements.

## Editing

- Keep one owner for each rule. Link to global Harness guidance for model routing;
  project documents cannot grant tool permissions or override global safety rules.
- Verify source paths, test names and commands against the current branch.
  Mark historical observations as such rather than presenting them as readiness.
- Keep instructions in concise English with plain Markdown. Do not add decorative
  symbols, credentials, live subscription endpoints or raw logs.
- Preserve explicit owner authorization for merges, tags, releases and deployments.
  Documentation edits and green checks do not grant that authority.
