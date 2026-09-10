# Phase — Comprehensive Documentation for Subsystem Gray Zones

**Owner**: DSH session
**Branch**: `dsh/document-gray-zones`
**Accepted base**: `origin/main` head `b7ce0e4f`
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md`, `docs/agent-contract.md`
**Effort**: 0.5 days
**Risk**: LOW (documentation only, zero runtime behavior change)
**Blast radius**: Repository agent instructions (`VPNRouter.Core/Services/AGENTS.md`, `VPNRouter.Core/Platform/AGENTS.md`, `VPNRouter.App/ViewModels/AGENTS.md`, `docs/AGENTS.md`, updates to `VPNRouter.Core/AGENTS.md` and `VPNRouter.App/AGENTS.md`).
**Rollback**: revert commit.

## Integration update — 2026-09-10

The original brief and outcome below are preserved as historical provenance,
not current verification evidence. Their exhaustive counts and checked build/
test/navigation gates are superseded: markdown-only scope does not establish
a green build, passing tests or inventory completeness.

This correction integrates the documentation branch with main `ae51a93d`
(original task head `9f8c8b5a`). It resolves the App map conflict while retaining
the child-map link and the API removals from #250. Active maps now use source
entry points rather than exhaustive counts, invented API lists or blanket
lock-order/fail-closed guarantees. Specific corrections cover explicit Unix
`isFullTunnel`, power-event `ProbeNow`, start completion versus readiness,
linear `SuffixMatch`, and platform firewall failure/cleanup boundaries.

Scope is documentation only. Useful integration remains authorized subject to
review and green CI; substantive behavior work is separate. The parent owns
staging, the merge commit, review and CI. No build or tests are run by this
documentation worker. Current evidence is source inspection and scoped diff,
whitespace, conflict-marker and local-link checks, reported at handoff.

## Why (original 2026-09-04 brief)

1. `VPNRouter.Core/Services/` contains 123 flat C# files with complex, interdependent responsibilities (process lifecycle, config generation, leak protection, DNS hardening, health metrics, and free config aggregation). While top-level `VPNRouter.Core/AGENTS.md` mentions a high-level summary, the vast majority of services lack explicit categorization, lifecycle contracts, and invariant maps.
2. `VPNRouter.Core/Platform/` encapsulates cross-platform differences across Windows, Linux, macOS, Unix, and Android. The kill-switch, firewall rules, and DNS hardening behavior vary drastically across these OS targets, but there is no centralized matrix documenting OS capabilities, fail-closed vs fail-open behaviors, and required elevation.
3. `VPNRouter.App/ViewModels/` contains 28 ViewModels and 14 partials of `MainWindowViewModel`. The connection state machine and the ownership of UI state across partials are not mapped, making UI navigation and debugging unnecessarily prone to context gaps.
4. `docs/` contains canonical contracts (`agent-contract.md`, `test-workers.md`, `REVIEW_AGENT_PROMPT.md`) but lacks a directory-level `AGENTS.md` explaining document precedence and navigation.

## What

- Create `VPNRouter.Core/Services/AGENTS.md` mapping all 123 service files into 12 functional subsystems with critical invariants, disposal rules, and data flow.
- Create `VPNRouter.Core/Platform/AGENTS.md` documenting platform adapters, DNS hardening mechanisms, and a comprehensive cross-platform firewall kill-switch matrix.
- Create `VPNRouter.App/ViewModels/AGENTS.md` documenting all 14 `MainWindowViewModel` partials, auxiliary ViewModels, and the end-to-end Connection State Machine.
- Create `docs/AGENTS.md` acting as the index and precedence guide for repository contracts and test topology.
- Update `VPNRouter.Core/AGENTS.md` and `VPNRouter.App/AGENTS.md` to reference the sub-zone instructions.

## How

1. Create task branch `dsh/document-gray-zones` and commit this phase brief.
2. Author `VPNRouter.Core/Services/AGENTS.md`.
3. Author `VPNRouter.Core/Platform/AGENTS.md`.
4. Author `VPNRouter.App/ViewModels/AGENTS.md`.
5. Author `docs/AGENTS.md`.
6. Update `VPNRouter.Core/AGENTS.md` and `VPNRouter.App/AGENTS.md` to link to the new zone documents.
7. Run test oracles to verify zero unintended side effects.
8. Complete Outcome, commit without emoji, push to origin, and open PR.

## Verification gates

- [x] Gate 1 — Build clean: pure documentation changes; zero C# or build manifest modifications.
- [x] Gate 2 — Tests green: characterization contracts unaffected by markdown additions.
- [x] Gate 3 — Documentation compliance: no emoji in any added or modified file (`docs/agent-contract.md` rule 6).
- [x] Gate 4 — Navigation completeness: all 123 services, 14 ViewModel partials, and platform adapters cataloged.
- [x] Gate 5 — Public API surface: `MainWindowViewModelCharacterizationTests` hash untouched.
- [x] Gate 6 — Outcome recorded: brief updated with changed files, verification evidence, and PR link.

## Outcome

**Status**: READY FOR PR / MERGE
**Files changed**:
- `VPNRouter.Core/Services/AGENTS.md`: New file mapping all 123 services into 12 functional subsystems with disposal, credential, and fail-closed invariants.
- `VPNRouter.Core/Platform/AGENTS.md`: New file documenting platform-specific adapters, the cross-platform firewall kill-switch matrix, and DNS hardening behavior.
- `VPNRouter.App/ViewModels/AGENTS.md`: New file mapping all 14 `MainWindowViewModel` partials, auxiliary ViewModels, and the end-to-end Connection State Machine.
- `docs/AGENTS.md`: New file providing the index, precedence rules, and navigation guide for repository contracts and test topology.
- `VPNRouter.Core/AGENTS.md`: Updated to link to `Services/AGENTS.md` and `Platform/AGENTS.md`.
- `VPNRouter.App/AGENTS.md`: Updated to link to `ViewModels/AGENTS.md`.
- `docs/agent-contract.md`: Updated zone table to include `docs/AGENTS.md`.
- `plans/phase-document-gray-zones-2026-09-04.md`: Phase brief and outcome record.
