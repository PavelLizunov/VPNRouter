# plans/ — DSH Agent Instructions

Roadmaps, post-mortems, handoff documents, and architecture plans for VPNRouter. All planning `.md` files reside here under `./plans/`.

Before taking actions under `plans/`, review [`docs/agent-contract.md`](../docs/agent-contract.md) (the canonical project contract) and root [`AGENTS.md`](../AGENTS.md).

## Naming Convention

```
vpnrouter-vX.Y.Z-<short-topic>.md       ← version roadmap
vpnrouter-<feature>.md                   ← feature plan across versions
session-handoff-YYYY-MM-DD.md            ← historical session handoff
session-night-shift-YYYY-MM-DD.md        ← autonomous session post-mortem
release-notes-vX.Y.Z[-rN].md             ← draft release notes (.gitignored)
```

## Active Plans & Ledgers

| File | Status |
|---|---|
| `interaction-contracts/README.md` | adopted framework (FC + APP page interaction contracts) |
| `OPEN-DEFECTS.md` | release-gating defect ledger |
| `v3.0-refactor-roadmap.md` | long-running refactor roadmap and task ownership |
| `v3.0-execution-methodology.md` | active execution methodology for v3.0 |
| `macos-parity-leak-dns-firewall-update-qa-plan-2026-06-04.md` | macOS runtime parity and QA follow-ups |
| `code-signing-signpath-runbook-2026-07-10.md` | Windows code-signing enrollment |
| `vpnrouter-release-strategy.md` | rolling-rN policy reference |
| `cut-stable-checklist.md` | fixed-WINBRAT pre-cut live-update gate |

Historical plan documents and session handoffs are preserved for context and historical provenance.

## Workflow & Tooling Equivalents

- **Zone Contract**: `plans/AGENTS.md`.
- **Skills**: Use DSH skills (`.dsh/skills/` or DSH skill catalog: `bug-hunt`, `cut-stable`, `ship-rolling-candidate`, `phase-task-launcher`, etc.).
- **Subagents & Orchestration**: Use DSH `subagent` for bounded delegation. Use `workflow` only when the user explicitly requests workflow-style or large scripted orchestration.
- **Worker Configuration**: `docs/test-workers.md` is the single worker source of truth for test VMs and worker target topology. Do not hard-code machine credentials or IPs.
