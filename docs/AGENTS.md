# docs/ Directory Instructions

This document governs the `./docs/` directory, which houses the canonical project contracts, test worker topology, and adversarial review prompts.

## Document Index & Precedence

| File | Purpose & Precedence |
|---|---|
| `docs/agent-contract.md` | **Canonical Project Contract**. The supreme authority for ownership, working model, safety, Git rules, test oracles, and release gates. All local `AGENTS.md` files and instructions must yield to this file. |
| `docs/test-workers.md` | **Worker Topology & Resource Rules**. Single source of truth for remote test VMs (such as `windows-brat`), Tailscale worker hostnames, capability tags, and execution constraints. Volatile credentials or IPs must never be copied elsewhere. |
| `docs/REVIEW_AGENT_PROMPT.md` | **Adversarial Review Prompt**. Standardized instructions and review lenses (correctness, tests, security, regressions) used when conducting multi-agent reviews or bug-hunts before release cuts. |

## Critical Invariants for Documentation Under `docs/`

1. **No Direct Push to Main**: The canonical remote is `origin`. Never push directly to `main` (`docs/agent-contract.md` rule 1).
2. **No Emoji Rule**: Do not add emoji to code, configuration, or documentation (`docs/agent-contract.md` rule 6).
3. **No Secrets**: Never commit tokens, UUIDs, subscription endpoints, or credentials into repository files (`docs/agent-contract.md` rule 5).
4. **Authority Separation**: A green test or verification check is evidence, not release authority. Candidate cuts, stable cuts, and deployments require explicit owner approval.
