# VPNRouter - DSH agent entry point

Before any repository action, read [`docs/agent-contract.md`](docs/agent-contract.md) completely. It is the canonical contract for ownership, safety, Git, tests, releases, and remote verification; conflicting local guidance is a defect and the canonical contract wins.

- Project skills: `.dsh/skills/<name>/SKILL.md`.
- Local repository overlay: `AGENTS.local.md`.
- Worker topology and resource rules: `docs/test-workers.md`.
- Plans and durable task notes: `plans/`.

Before changing a zone, read the nearest `AGENTS.md` listed by the canonical contract. Treat DSH runtime settings, caches, and session state as harness-owned unless the user explicitly asks to change them.
