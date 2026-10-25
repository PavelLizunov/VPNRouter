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
2. Tests: PASS for scoped link/content checks and diff whitespace; brief-head PR CI passed all four checks at 77931e94254307cc9fb489fbcba7feadc6fb5ffa (run 40224166970 plus grep). Implementation-head CI must be checked on PR #627 after push. No local full suite.
3. Docs: PASS, root instruction leads to the 55-line source/genre/license guide; active fframes component instruction also links it.
4. Review: PASS, a2857a02 verified primary-source rules and three corrections before drafting; cc9a51e0 reviewed the entire tracked diff and untracked guide (SHA-256 b0f012342047fbe36ff3467737b7da67f0b380a3c445d7911d5a4298a6a09694), with no further material findings.
5. UI: N/A, no UI change, render or ship.
6. Characterization: N/A, no mechanical refactor or media change; verify protected paths remain unchanged.

## Outcome

- Status: documentation implemented and independently reviewed; final exact-head CI must be observed on PR #627 before handoff.
- Published scope: docs/fframes-music-sources.md; one root AGENTS.md route; resolved FFRAMES-MUSIC-DISCOVERY ledger entry; this brief. Eight sources, genre search terms, actual-track license checks, source-specific credits, modification notices and per-track provenance.
- Checks: Python standard-library checks passed for six local Markdown links, source/license/provenance coverage and exact file scope; git diff --check passed. No runtime, dependency, workflow, exporter or media changed.
- Review: a2857a02's Mixkit remix, CC notices/modifications and credit-placement corrections were incorporated; cc9a51e0 found no further material issues in the full guide/diff. Retain source/track-specific checks for each future selection.
- Local integration: copied the exact reviewed guide to /var/lib/dsh/Project/Fframes and linked it from both root and component AGENTS.md. Kept that checkout on dsh/fframes-eight-styles at a369043715868b426a47429a2de8b641d96a60ee with an unchanged index. These documentation additions intentionally remain unstaged there for the owning branch; no unrelated commits are included in PR #627.
- Preservation: active interchange.mp3 SHA-256 remains a6a2562c3600c52446bd29dda5c1cb4bb96e4cfc6b57f5e26ca6b2f481852e29. Existing music provenance and both exporters retained their original SHA-256 values. Root and component routes resolved to the reviewed guide.
- Git: brief 77931e94254307cc9fb489fbcba7feadc6fb5ffa pushed to origin/dsh/fframes-music-sources; https://github.com/PavelLizunov/VPNRouter/pull/627. PowerShell is unavailable; use exact-head gh pr checks per the repository contract. No merge or release authorized.
- Prose delivery gate: PASS for no fabricated claims, mechanism-first wording and concrete source-to-action flow. Not a guarantee of future agent compliance or claim-free music.
- Limitations: no individual track audition, download, render or upload-claim test is claimed. Runtime/UI/characterization gates are not applicable.
- Cleanup: isolated worktree and active documentation copies intentionally retained; no caches or existing artifacts removed.
