# Phase - fframes music discovery

- Owner: lead; direct request: make music sources discoverable to the video agent.
- Branch: dsh/fframes-music-sources
- Base: c3ebc46ee7a336bc689a893a00d2db8c9bf4a300 (current origin/main).
- Risk: LOW; documentation and agent routing only, no audio or renderer changes.
- Rollback: revert this task's documentation commits.

## Why

Music research exists only in chat. The current fframes checkout documents one CC0 track but has no reusable source-selection guide.

## What

Add docs/fframes-music-sources.md and a root AGENTS.md entry, and record FFRAMES-MUSIC-DISCOVERY in the defect ledger. Cover genre-based discovery, commercial/product use, attribution, Content ID and per-track evidence.

Preserve all media, existing music credits, renderer code, export scripts and unrelated work. Do not download music, generate audio, render, merge or release. Source terms can change; the agent must verify the actual track and publication context.

## How

Use an isolated worktree from main because the renderer is still in PR #626. Commit/push the brief first and wait for PR checks. Then write the guide using the official sources already researched, check links and claims, commit/push and observe exact-head CI. Copy the completed task documentation into the active fframes checkout without changing its branch or index so agents there can use it immediately; report that local integration separately from the published branch.

## Verification

1. Build: N/A, no executable or dependency changes; no local build.
2. Tests: PENDING, scoped Markdown links/content checks, diff whitespace and exact-head PR CI; no local full suite.
3. Docs: PENDING, root instruction must lead to the complete source/genre/license guide.
4. Review: PENDING, independent bounded documentation review and source-verified dispositions.
5. UI: N/A, no UI change, render or ship.
6. Characterization: N/A, no mechanical refactor or media change; verify protected paths remain unchanged.

## Outcome

- Status: IN PROGRESS; brief saved before implementation.
- Checks/Git: pending.
- Limitations: no individual track audition, download or upload-claim test is claimed.
