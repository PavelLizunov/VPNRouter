# Phase — Comprehensive Documentation for Subsystem Gray Zones

**Owner**: DSH session
**Branch**: `dsh/document-gray-zones`
**Accepted base**: `origin/main` head `b7ce0e4f`
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md`, `docs/agent-contract.md`
**Effort**: 0.5 days
**Risk**: LOW (documentation only, zero runtime behavior change)
**Blast radius**: Repository agent instructions (`VPNRouter.Core/Services/AGENTS.md`, `VPNRouter.Core/Platform/AGENTS.md`, `VPNRouter.App/ViewModels/AGENTS.md`, `docs/AGENTS.md`, updates to `VPNRouter.Core/AGENTS.md` and `VPNRouter.App/AGENTS.md`).
**Rollback**: revert commit.

## Why

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

- [ ] Gate 1 — Build clean: `dotnet build VPNRouter.sln -c Release` passes with zero errors.
- [ ] Gate 2 — Tests green: canonical Core, ViewModel, and CLI test oracles pass.
- [ ] Gate 3 — Documentation compliance: no emoji in any added or modified file (`docs/agent-contract.md` rule 6).
- [ ] Gate 4 — Navigation completeness: all 123 services, 14 ViewModel partials, and platform adapters are cataloged.
- [ ] Gate 5 — Public API surface: `MainWindowViewModelCharacterizationTests` hash unchanged.
- [ ] Gate 6 — Outcome recorded: brief updated with changed files, verification evidence, and PR link.

## Outcome

(Pending execution)
