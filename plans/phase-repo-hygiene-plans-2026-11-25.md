# Phase H-2 - Repository Hygiene: Archive Historical Plans and Tidy Open Defect Ledger

- Owner: Workstream 2B implementation sub-agent
- Branch: dsh/repo-hygiene-plans-archive-20261125
- Base: 4e8aa796554892e6e92427c7c6217d81449cdd3e
- Roadmap: Repository hygiene H-2 (following H-1 precedent #327)
- Risk: LOW (documentation and ledger organization under `plans/` only; no product code, project, workflow or script edits)
- Blast radius: `plans/` only (no `.cs`, `.csproj`, `.axaml` or other non-`plans/` files modified)
- Rollback: Revert the PR or restore any archived file with `git checkout 4e8aa796 -- plans/<file>`

## Why

Since H-1 (`6491be4c`, 2026-09-28), `plans/` accumulated 213 files, most of them
completed or superseded historical briefs and release receipts from late
September through mid-November 2026. In addition, `plans/OPEN-DEFECTS.md`
carried 312 already-resolved `- [x]` entries mixed inside `## Open`, stretching
the open section to 593 lines and obscuring the 90 actual open `- [ ]` items.

## What

1. Archive 169 superseded historical files from `plans/` via `git rm`. Every
   removed file remains permanently readable at archive snapshot `4e8aa796`
   (`git show 4e8aa796:plans/<file>`).
2. Retain 44 existing files in `plans/` plus this brief (45 files total):
   - Core files (2): `plans/AGENTS.md`, `plans/OPEN-DEFECTS.md`.
   - Current release cycle `2026-11-20` through `2026-11-25` (19 existing + 1 this brief):
     `phase-android-private-readiness-2026-11-21.md`,
     `phase-android-tun-review-2026-11-23.md`,
     `phase-deepseek-app-harness-2026-11-23.md`,
     `phase-deepseek-app-targets-2026-11-23.md`,
     `phase-deepseek-confirm-intent-2026-11-24.md`,
     `phase-deepseek-desktop-acceptance-2026-11-23.md`,
     `phase-deepseek-harness-reliability-2026-11-23.md`,
     `phase-deepseek-vpn-repair-2026-11-23.md`,
     `phase-f01-macos-durable-2026-11-21.md`,
     `phase-f01-macos-privilege-migration-2026-11-20.md`,
     `phase-memory-leaks-2026-11-25.md`,
     `phase-omarchy-dnstt-hop-2.51-2026-11-20.md`,
     `phase-repo-hygiene-plans-2026-11-25.md`,
     `phase-simple-subscription-controls-2026-11-20.md`,
     `phase-standard-prerelease-r3-2026-11-24.md`,
     `phase-standard-rollback-assistance-2026-11-24.md`,
     `phase-subscription-ai-assistant-2026-11-21.md`,
     `phase-tgproxy-dependency-refresh-2026-11-24.md`,
     `phase-vmess-quick-probe-tls-sni-2026-11-22.md`,
     `phase-vmess-standard-subscription-2026-11-22.md`.
   - Additional files cited by open (`- [ ]`) entries in `plans/OPEN-DEFECTS.md` (3):
     `phase-d10-r16-owner-logs-2026-10-02.md`,
     `phase-headless-upgrade-recovery-2026-10-26.md`,
     `phase-omarchy-n4-lifetime-2026-10-26.md`.
   - Additional files cited by tests, workflows, `AGENTS.md` or active `README.md` files (6):
     `omarchy-session-lifetime.md` (`VPNRouter.Headless/README.md`),
     `phase-omarchy-integration-2026-10-22.md` (`VPNRouter.Headless/README.md`),
     `phase-stable-2.50.5-2026-10-20.md` (`VPNRouter.Core/Recovery/README.md`, `CURRENT_STATE.md`),
     `phase-m1-ui-mcp-2026-10-02.md` (`.github/workflows/build-ui-mcp.yml`, `VPNRouter.Tools/AGENTS.md`),
     `phase-h68-android-test-hook-2026-09-30.md` (`VPNRouter.Tests/AndroidTestHookGuardTests.cs`),
     `phase-p1-post-ship-local-2026-10-01.md` (`VPNRouter.Tests/PostShipLocalContractTests.cs`).
   - Direct relative Markdown link target from kept `plans/omarchy-session-lifetime.md` (1):
     `phase-omarchy-session-service-2026-10-25.md`.
   - Additional files cited by `CURRENT_STATE.md` (12, of which 3 are also cited in
     open-section intro notes in `plans/OPEN-DEFECTS.md`):
     `phase-android-abi-awg-keyboard-about-2026-10-06.md`,
     `phase-core-1.14.2-vpnctl2-2026-10-05.md`,
     `phase-geo-ruleset-selfheal-2026-10-11.md`,
     `phase-home-problems-fix-2026-10-17.md`,
     `phase-r29-release-2.50.0-r29-2026-10-04.md`,
     `phase-release-2.50.5-r3-2026-10-19.md`,
     `phase-stable-2.50.0-2026-10-04.md`,
     `phase-stable-2.50.1-2026-10-06.md`,
     `phase-stable-2.50.2-2026-10-06.md`,
     `phase-stable-2.50.3-2026-10-11.md`,
     `phase-stable-2.50.4-2026-10-19.md`,
     `phase-tray-icon-guard-2026-10-18.md`.
   - Proposal cited by `packaging/windows/vpnrouter.iss` (1):
     `phase-installer-proposal-2026-09-30.md`.
3. Update `plans/AGENTS.md` History section to retain the H-1 (`6491be4c`) note
   intact and add the H-2 (`4e8aa796`, 2026-11-25) snapshot reference.
4. Tidy `plans/OPEN-DEFECTS.md` by moving all 312 `- [x]` resolved entries
   (along with their 7 bound continuation and integration notes) verbatim from
   `## Open` into `## Resolved (history)` without deleting any history or
   changing the `tools/check-open-p0.ps1` gated open P0/P1 set.

### Invariants

- Zero edits outside `plans/`.
- Zero broken references from any file outside `plans/`, any kept `plans/`
  Markdown link, or any `- [ ]` entry in `plans/OPEN-DEFECTS.md`.
- `AgentContextContractTests` invariants preserved (`plans/AGENTS.md` non-empty,
  no forbidden legacy agent tokens).
- `tools/check-open-p0.ps1` and `ReleaseDefectGateTests` invariants preserved:
  exactly one `## Open` heading, identical open P0/P1 gated entry set (1 open
  P1: NaiveProxy Chromium CVE-2026-76029).
- Zero history deleted from `plans/OPEN-DEFECTS.md`.

## How

1. Audit all 213 tracked files in `plans/` against every reference outside
   `plans/` (`VPNRouter.Tests/`, `tools/`, `.github/`, `docs/`, `README*.md`,
   `AGENTS.md`, `CURRENT_STATE.md`, `packaging/`, `.dsh/`), every `- [ ]` entry
   in `plans/OPEN-DEFECTS.md`, and relative Markdown links in kept plans.
2. Verify `tools/check-open-p0.ps1` and `VPNRouter.Tests/` consumers of
   `OPEN-DEFECTS.md` (`AgentContextContractTests`, `ReleaseDefectGateTests`);
   confirm zero tests assert `- [x]` entries inside `## Open`.
3. Update `plans/AGENTS.md`, move resolved `- [x]` entries verbatim from
   `## Open` to `## Resolved (history)` in `plans/OPEN-DEFECTS.md`, and `git rm`
   the 169 archived historical plans on branch
   `dsh/repo-hygiene-plans-archive-20261125`.
4. Run static verification scripts checking counts, reference integrity, ledger
   line-multiset preservation, `check-open-p0.ps1` parity and forbidden-token
   compliance.
5. Push branch and open PR targeting `main` so GitHub Actions CI runs the full
   automated contract and test suites.

## Verification

1. Build: N/A locally (documentation-only under `plans/`; no local `dotnet`
   build permitted on this workstation; verified via GitHub Actions CI on PR).
2. Tests: N/A locally (no local `dotnet test` on this workstation; CI runs
   `AgentContextContractTests`, `ReleaseDefectGateTests` and full suite).
   Static verification PASS:
   - `plans/` count: 213 before -> 45 after (44 kept + 1 this brief, 169 archived).
   - Zero dangling references outside `plans/` to any archived file.
   - Zero dangling relative Markdown links from kept `plans/` files to any archived file.
   - Zero dangling references from any `- [ ]` entry in `plans/OPEN-DEFECTS.md`
     to any archived file.
   - `plans/OPEN-DEFECTS.md`: 0 dropped non-blank lines, all 94 `- [ ]` lines
     and all `- [x]` lines preserved verbatim; `## Open` shrunk from 593 lines
     (90 open + 312 resolved) to 178 lines (90 open, 0 resolved).
   - `tools/check-open-p0.ps1` parity: 1 `## Open` heading, identical 1 gated P1.
   - `plans/AGENTS.md`: non-empty, ASCII punctuation, zero forbidden legacy tokens.
3. Docs: PASS (`plans/AGENTS.md` updated with H-2 snapshot `4e8aa796`; this
   brief documents exact counts and verification).
4. Review: N/A (trivial documentation/archive hygiene under `plans/`; PR
   reviewed by coordinator and GitHub Actions CI).
5. UI: N/A (no UI, Avalonia or Android changes).
6. Characterization: N/A (no code splits or product source changes).

## Outcome

- Status: PASS (local static checks complete; PR opened for GitHub Actions CI).
- Changed files and delta: `plans/` only: 169 archived (`git rm`), 2 modified
  (`plans/AGENTS.md`, `plans/OPEN-DEFECTS.md`), 1 added
  (`plans/phase-repo-hygiene-plans-2026-11-25.md`); 213 -> 45 tracked files in
  `plans/`.
- Checks: Static Python audit verified zero dangling references, zero dropped
  lines in `plans/OPEN-DEFECTS.md`, `tools/check-open-p0.ps1` parity (1 open P1
  unchanged), LF line endings and `AgentContextContractTests` token rules.
- Review: Pending PR CI and coordinator review.
- Git: Branch `dsh/repo-hygiene-plans-archive-20261125` pushed to `origin` with
  PR targeting `main`.
- Follow-ups: Existing historical `##` sections appended below
  `## Resolved (history)` (lines 638-712 of pre-H-2 `OPEN-DEFECTS.md`,
  including `OMARCHY-SHELL-SESSION-LIFETIME`) were left in place so H-2 does
  not alter `tools/check-open-p0.ps1` gating semantics.
- Cleanup: No temporary files created in the repository.
