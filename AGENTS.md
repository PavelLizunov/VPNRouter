# VPNRouter agent instructions

## Scope

VPNRouter is a process-based split-tunnel VPN router for Windows, macOS, Linux and Android. Keep project rules here, detailed procedures in skills, and task evidence in `plans/`. Global Harness guidance and live tool permissions still apply.

## Start here

- Read [docs/agent-contract.md](docs/agent-contract.md) completely before repository work. It owns project safety, Git, tests and release gates.
- Read the nearest zone `AGENTS.md` before changing files. The contract indexes zones; follow nested instructions too.
- Use zone maps to find source entry points. Verify paths, symbols and behavior at the current revision; historical plans and maps from other branches are not current contracts.

## Task routing

- Project skills: `.dsh/skills/<name>/SKILL.md`. Load matching skills from the session catalog; do not assume a tool or model route exists because a document names it.
- Task briefs and outcomes: [plans/AGENTS.md](plans/AGENTS.md). Record intent, scope and verification before edits; use `phase-task-launcher` for changes over 30 lines.
- Review evidence and open findings: [docs/REVIEW_AGENT_PROMPT.md](docs/REVIEW_AGENT_PROMPT.md) and [plans/OPEN-DEFECTS.md](plans/OPEN-DEFECTS.md).
- Worker roles and preflight: [docs/test-workers.md](docs/test-workers.md). Load `homelab` before remote work.
- Builds and SDKs: never install an SDK or toolchain, and never run a build, package or full test run, on the development workstation or `harness-test`, not even a quick compile check. Heavy work runs on a trusted worker alias or in GitHub CI at an exact SHA, after the read-only load preflight in test-workers.md (CPU load, free RAM and swap, disk, running jobs, SDK) with the numbers recorded. A missing SDK is a blocker to report. An owner remark such as "you can do the build" does not name a machine; ask which worker.

## Instruction maintenance

Write active agent guidance in concise English with plain Markdown and ASCII punctuation. Preserve technical identifiers and localized skill triggers. Link to owning rules instead of copying them. Do not edit Harness settings, caches or session state as project content.
