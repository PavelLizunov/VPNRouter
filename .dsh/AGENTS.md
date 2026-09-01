# `.dsh/` - project DSH context

Read [`docs/agent-contract.md`](../docs/agent-contract.md) before changing project context.

- Native project skills live at `.dsh/skills/<name>/SKILL.md`; discovery is one directory deep.
- Every `SKILL.md` uses frontmatter keys `name`, `description`, and `whenToUse`.
- Keep skills repository-relative and use native DSH tools. Do not invent unavailable tool names.
- `docs/test-workers.md` owns worker aliases and resource behavior. A fixed identity/address may appear only where a fail-closed verification contract requires it.
- Apart from this file and `.dsh/skills/`, DSH runtime state, settings, caches, and session memory are harness-owned and must not be committed or edited without an explicit request.
