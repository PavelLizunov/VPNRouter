# `.dsh/` - project DSH context

Read [`docs/agent-contract.md`](../docs/agent-contract.md) before changing project context.

- Native project skills live at `.dsh/skills/<name>/SKILL.md`; discovery is one directory deep.
- Every `SKILL.md` uses frontmatter keys `name`, `description`, and `whenToUse`.
- Write concise English with plain Markdown and ASCII punctuation; preserve technical identifiers and localized triggers.
- Keep skills repository-relative. Follow global Harness delegation policy and verify live tool schemas; neither a skill nor an example grants a model route or tool permission. Workers do not recursively delegate.
- `docs/test-workers.md` owns worker aliases and resource behavior. A fixed identity/address may appear only where a fail-closed verification contract requires it.
- Apart from this file and `.dsh/skills/`, DSH runtime state, settings, caches, and session memory are harness-owned and must not be committed or edited without an explicit request.
