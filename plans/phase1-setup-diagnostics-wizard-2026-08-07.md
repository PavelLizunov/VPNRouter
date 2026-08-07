# Phase 1 — Setup and diagnostics wizard

**Owner**: Codex task `019fc457-6058-7a21-a9e6-738b22054870`
**Branch**: `codex/setup-diagnostics-wizard-2026-08-07`
**Roadmap ref**: out-of-roadmap QoL feature; reuses existing diagnostics and MTU contracts
**Effort**: 1–2 days
**Risk**: MEDIUM — user-facing settings writes and a new modal UI, but no new tunnel or firewall engine
**Blast radius**: desktop Avalonia UI + existing settings persistence · about 8–12 files / 400–700 changed lines · Windows/macOS/Linux
**Rollback**: `git revert <implementation-commit>` / branch delete

## Why

VPNRouter currently exposes Health Check, MTU diagnostics, Safe Mode, factory
reset, and diagnostics export as separate troubleshooting actions. A
nontechnical user cannot tell which action is temporary, which action destroys
settings, or whether a suspicious MTU is the reason a connection fails. The
wizard should guide the user through the existing checks, offer a narrow MTU
repair, and explain Safe Mode without introducing speculative auto-MTU or a
second diagnostic engine.

## What

- Add a four-step modal desktop wizard opened from the troubleshooting menu:
  routing summary, read-only health check, safe network repair, result.
- Render `HealthCheck.RunAll()` results inside the app instead of opening a text
  report.
- Offer an explicit MTU reset to `TunSettings.DefaultMtu` (`1420`) and include
  it in “restore safe network settings”. Preserve a snapshot for one-click undo.
- Persist only routing mode and MTU through the existing `SaveSettings()` path.
- Report kill-switch availability honestly from the selected profile/platform;
  do not simulate a tunnel crash or invent a new global firewall setting.
- Keep “Restart in Safe Mode” as a separate emergency action and clarify its
  copy as a temporary launch that does not repair stored settings.
- Reuse the existing redacted diagnostics export command on the result step.
- Desktop scope only. Android has no user-editable MTU field and keeps its
  existing one-shot Safe Mode/Health Check surfaces.

```diff
- Separate Health Check / Safe Mode / Reset config / MTU controls
+ One guided modal that diagnoses, applies only selected safe repairs,
+ verifies the result, and can undo its own changes
```

## How

1. Add localized wizard strings and an `OpenSetupWizardCommand` menu entry.
2. Add a dedicated wizard ViewModel that wraps existing health-check results
   and keeps the pre-change routing/MTU snapshot.
3. Add a responsive Avalonia window using semantic design tokens and manual
   step panels (no `TabControl`).
4. Add the smallest MainWindowViewModel bridge needed to save or restore MTU
   and routing through the existing settings path.
5. Add pure ViewModel tests, headless render/binding coverage, and localized
   source-contract checks where appropriate.
6. Run full build/tests, read-only Qwen review, simplify review, Markdown
   checks, and remote WINBRAT end-to-end UI verification.

### Tests written

- `SetupWizardViewModelTests.ResetMtu_UsesCanonicalDefaultAndCanUndo` — pins
  `1420`, persistence callback, and snapshot rollback.
- `SetupWizardViewModelTests.RunHealthCheck_MapsOkWarnErrWithoutWriting` — pins
  the read-only diagnostic mapping.
- `SetupWizardViewModelTests.RestoreSafeSettings_ResetsMtuAndKeepsUserRoutingChoice` —
  prevents a repair from silently forcing Full Tunnel.
- Headless wizard render test — verifies all four panels, wrapped copy, and
  narrow-window-safe controls can be constructed.

### Verification approach

Run focused wizard/headless tests first, then the Release solution build and
full test suite. Since this is a UI feature, deploy the branch artifact only to
the fixed WINBRAT VM and exercise: open wizard → run checks → reset MTU → undo →
restore safe settings → diagnostics action → close. No VPNRouter launch or UI
automation is allowed on the local development machine.

## Verification gate

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release --no-restore` → 0 warnings / 0 errors.
- [x] **Gate 2 — Tests green for changed scope**: wizard/headless/characterization tests 8/8; isolated broad suite 2695 passed, 4 known unrelated local baselines failed, 2 skipped, and no wizard test failed.
- [x] **Gate 3 — Docs**: Outcome filled; README EN/RU document the troubleshooting entry point.
- [x] **Gate 4 — Self-review**: Ponytail full plus three read-only exact-model Qwen passes; security review N/A because the feature does not mutate firewall, authentication, TLS, or process execution.
- [x] **Gate 5 — Remote brat UI verify**: final branch artifact verified only on WINBRAT with screenshots under `artifacts/brat-verify/setup-wizard/`; logs clean.
- [x] **Gate 6 — Characterization diff**: intentional `MainWindowViewModel` public additions updated the Windows characterization hash to `d44c861459eb262e7f344483e2088fef169594bca8bacf9d731fc2b5831fe9c2`.

## Outcome

**Status: PASS for draft review; not released.**

Implemented the four-step desktop wizard behind the troubleshooting menu. It
reuses the existing `HealthCheck`, settings persistence, routing setter and
redacted diagnostics exporter. The only persistent repair values are MTU and
routing mode; closing before an explicit repair writes nothing. `Reset MTU
only` always uses the canonical `TunSettings.DefaultMtu` (`1420`) and preserves
the currently applied routing mode. `Restore safe settings` applies the chosen
routing mode with MTU 1420, then reruns checks. The opening MTU/routing snapshot
supports one-click undo.

Safe Mode remains a separate temporary emergency start. The wizard does not
invent auto-MTU, simulate failures, change firewall rules, reset unrelated
settings, or add a second diagnostics engine. Desktop only; no Android scope.

### Evidence

- Release solution build: 0 warnings / 0 errors.
- Focused tests after the final XAML review: 8 passed / 0 failed.
- Broad suite with isolated ProgramData: 2695 passed / 4 pre-existing
  environment/baseline failures / 2 skipped. The failures are the already
  documented non-admin `Global` TUN semaphore pair and two Windows visual
  baselines; no wizard failure.
- Qwen worker: exact `qwen3.8-max-preview`, noninteractive `-p`, safe/plan mode,
  chat recording off, zero tool calls. Confirmed the architecture and found
  the repaired step-3 overflow, invisible save-error feedback, missing tests,
  and final 360px wrapping gaps. Codex independently validated every finding;
  rejected the proposed per-stage `InvokeThen` timeout because the helper's
  public contract deliberately bounds the entire atomic action.
- Final Windows branch package SHA256:
  `057a014e844eab40dcdcdb9f7ba8ffa900a6e8b3ce9021c2ccf18a4e499f6645`.
- WINBRAT identity: `WINBRAT` at `100.115.182.0`. Verified open → checks →
  repair → explicit MTU reset → repeated checks → diagnostics export → close.
  Health result was warnings 1 / errors 0; the warning was the existing
  fixed-target IPv4 DF ping caveat. The last 60-minute remote log scan had no
  `[ERR]`, `Exception`, or `FATAL` entries.
- Final layout evidence:
  `artifacts/brat-verify/setup-wizard/final-step1.png`,
  `artifacts/brat-verify/setup-wizard/final-step3.png`, and
  `artifacts/brat-verify/setup-wizard/step4-mtu-reset.png`.
- Routing apply/undo has direct ViewModel coverage. Avalonia radio-button UIA
  was not reliable enough to mutate that choice semantically on WINBRAT, so
  the verifier now fails closed and the remaining tooling limitation is
  measurement-gated in `plans/OPEN-DEFECTS.md`.

### Review footprint

No dependency or abstraction was added. The feature is one ViewModel, one
modal view/code-behind pair, one localization partial and one focused test
file, plus narrow integration edits. The implementation stays within the
brief's intended size and reuses existing services instead of creating a new
wizard framework.

Draft PR: [#116](https://github.com/PavelLizunov/VPNRouter/pull/116).
