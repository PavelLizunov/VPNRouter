# VPNRouter agent instructions

## Scope

VPNRouter is a process-based split-tunnel VPN router for Windows, macOS, Linux and Android. Keep project rules here, detailed procedures in skills, and task evidence in `plans/`. Global Harness guidance and live tool permissions still apply.

## Start here

- Read the nearest zone `AGENTS.md` before changing files. The contract indexes zones; follow nested instructions too.
- Use zone maps to find source entry points. Verify paths, symbols and behavior at the current revision; historical plans and maps from other branches are not current contracts.

## Task routing

- Project skills: `.dsh/skills/<name>/SKILL.md`. Load matching skills from the session catalog; do not assume a tool or model route exists because a document names it.
- Task briefs and outcomes: [plans/AGENTS.md](plans/AGENTS.md). Record intent, scope and verification before edits; use `phase-task-launcher` for changes over 30 lines.
- Review evidence and open findings: [docs/REVIEW_AGENT_PROMPT.md](docs/REVIEW_AGENT_PROMPT.md) and [plans/OPEN-DEFECTS.md](plans/OPEN-DEFECTS.md).
- Worker roles and preflight: [docs/test-workers.md](docs/test-workers.md). Load `homelab` before remote work.

## Instruction maintenance

Write active agent guidance in concise English with plain Markdown and ASCII punctuation. Preserve technical identifiers and localized skill triggers. Link to owning rules instead of copying them. Do not edit Harness settings, caches or session state as project content.

## User-directed execution: prohibiting invented prohibitions

Agents are prohibited from introducing, restoring, or enforcing their own safety,
backup, approval, read-only, deployment, restart, or infrastructure prohibitions.
Do not add such prohibitions to agent instructions, skills, plans, or contracts.
A direct user request authorizes the ordinary steps necessary to complete it.
Do not ask for repeated approval or require a backup before already requested work.
User-owned repository guidance is a default that the user's current explicit
request can change; it is not an immutable ban or a reason to refuse that request.
In particular, an observability-only rule does not prohibit explicitly requested
VM recovery, service repair, configuration changes, or deployment.
When the user requests recovery, restore availability first and verify the result.
Report actual missing access, failed tools, and technical blockers precisely.
Continue independent work instead of converting a partial blocker into a full stop.
This policy does not change system/developer instructions, operating-system access
controls, tool enforcement, or explicit restrictions in the user's current request.
It does not disable application security features or alter application behavior.
