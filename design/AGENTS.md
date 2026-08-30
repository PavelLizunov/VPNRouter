# design/ — DSH Agent Instructions

Design handoff bundles, visual tokens, and UI layout reference specs for VPNRouter.

Before taking actions under `design/`, review [`docs/agent-contract.md`](../docs/agent-contract.md) (the canonical project contract) and root [`AGENTS.md`](../AGENTS.md).

## Workflow & Skills

- **Handoff Skill**: Use `.dsh/skills/merge-design-handoff` (or `merge-design-handoff` in the DSH skill catalog) when integrating design handoff packages into Avalonia UI.
- **Subagents & Orchestration**: Delegated UI implementation or review tasks use DSH `subagent`; use `workflow` only when the user explicitly requests it or large scripted orchestration.
- **Provenance**: Transcripts under `design/chats/` and historical design specs are preserved as source-material context; they do not override current code or the canonical contract.

## Token Mapping

Design tokens in `design/project/tokens.css` and handoff-local `design/project/handoff/tokens.css` map to Avalonia resources in `VPNRouter.App/Styles/Tokens.axaml`. Always reference semantic token variables (`--surface-*`, `--text-*`, `--accent-*`) rather than hardcoded colors or raw hex values.
