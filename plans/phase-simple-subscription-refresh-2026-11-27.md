# Make subscription refresh visible in Standard

- Branch: `dsh/simple-subscription-refresh-turnrelay-20261127`.
- Base: `d2a3ed197623a00570fcfafce3893326ce6e3995`.
- Risk: Low. The change affects desktop subscription controls and picker rows.
- Rollback: Revert the UI and regression changes together.

## Why

In r5 the Simple home shows the subscription link editor, while refreshing
the current server list requires opening the server checks panel. The same
picker in Simple and Advanced also shows TurnRelay hops with the unsupported
server label, despite those hops being available to linked exits.

## What and how

Place refresh, progress, outcome feedback and blocked-reason hints on Simple home
and Advanced SubscribePage. Reuse the existing refresh command and in-flight guard.
Filter TurnRelay hops out of the shared picker groups and search while retaining
the complete subscription pool for resolving detours, flagging missing upstream
hops and TurnRelay-only feeds clearly. Fix P0/P1/P3 Neon UX/UI audit findings
(`F-01`..`F-07`, `F-10`):

- `F-01`: Prevent accidental cancellation of in-flight assistant/diagnosis
  requests on clicks/Tab/ScrollBar; preserve Escape and outside-click decline
  during confirmation only.
- `F-02`: Fix dark/light selection contrast on `ToggleButton.row:checked` and
  `ListBox.pick ListBoxItem:selected`; add semantic `WarningActionSolid` tokens
  for >= 5.0:1 WCAG AA contrast on amber/warning buttons.
- `F-03` / `F-06`: Clear action role hierarchy (`outline` Refresh, `ghost` Change
  link / Test servers / App rules), honest link-save notices, service-managed
  `Apply now` button, responsive `WrapPanel` banners, no auto-ping on expand.
- `F-04`: TurnRelay badge + tooltip, accurate selectable server counts, clickable
  manual-only categories (expand on click), missing-hop / TurnRelay-only states.
- `F-05`: Three-section Assistant card (Local repair / Command / Diagnostics),
  indeterminate progress bar, distinct connection vs crash copy, focus return.
- `F-07` / `F-10`: `ShowMode="Standard"` 248px scrollable `⋮` menu with `App`
  section (Check updates, Version history, About), persistent update check status
  with installed version, Light/Dark `LogoSource` sync via `ActualThemeVariantChanged`.

## Verification

1. Build and run the applicable Standard suites in hosted CI.
2. Assert that refresh is visible with checks collapsed, shows blocked reason and
   partial-failure feedback, and executes the registered feed refresh. Exercise
   TurnRelay exit, missing-hop, and hop-only picker rows.
3. Assert Assistant focus/click guards during in-flight and confirmation phases.
4. Check ViewModel characterization (all new `MainWindowViewModel` members kept
   `internal`) and confirm a pinned exit still produces the expected TurnRelay hop.
5. Inspect isolated CI renders across narrow widths and Light/Dark themes.

## Outcome

Applied Standard UX/UI redesign (`F-01`..`F-07`, `F-10`) across App/Core/Tests
without altering `MainWindowViewModel`'s public surface. XAML parse, diff check,
and static audits passed locally; hosted CI build, tests, and synthetic renders
running on PR #747.
