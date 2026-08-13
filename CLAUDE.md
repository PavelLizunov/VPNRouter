# VPNRouter - Claude bootstrap

Before taking any repository action, read
[`docs/agent-contract.md`](docs/agent-contract.md) completely. It is the single
canonical project contract for ownership, safety, Git, testing, releases and
WINBRAT verification. A conflicting rule in this bootstrap is a defect; the
canonical contract wins.

Claude-specific paths:

- Skills: `.claude/skills/<name>/SKILL.md`.
- Session handoff: `.claude_handoff.md` (gitignored, controlled by the project).
- Owner-private notes: `CLAUDE.local.md` (do not edit).
- `.claude/workflow.md`, settings, hooks, runtime cache and memory are
  harness-owned; do not edit them unless the user explicitly asks.

Read the zone `CLAUDE.md` named by the canonical contract before changing that
zone. Use the matching project skill whenever its trigger applies.
