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

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] **Gate 2 — Tests green**: full suite passes; new wizard tests included.
- [ ] **Gate 3 — Docs**: this brief Outcome filled; README updated with the new troubleshooting entry point.
- [ ] **Gate 4 — Self-review**: simplify/ponytail review plus read-only Qwen review; security review only if scope expands into firewall mutation.
- [ ] **Gate 5 — Remote brat UI verify**: WINBRAT end-to-end wizard flow and screenshots under `artifacts/brat-verify/setup-wizard/`.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split; intentional public VM additions documented by the characterization test.

## Outcome (filled after verification)

Pending implementation and all six gates.
