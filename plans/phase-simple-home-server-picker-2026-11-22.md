# Simple desktop home and server picker

- Branch `dsh/simple-home-server-picker-20261122`
- Base `cc361c50a6bab949ea9e92ca054346c836cdbae6` with the verified manual-selection change at `566812acb98493094c88e330a0305c328e892dfd` from PR #725
- Risk is medium because the desktop picker submits connection preferences
- Rollback by reverting this phase's implementation commits

## Why

On Windows 2.51.0-r1 a real 16-server subscription fills the home screen when expanded. Choosing a server, running checks and editing the subscription URL share one disclosure. The home routing hint lists fixed example applications as if it reported current rules.

## What

Keep connection action, the current server and the routing choice at the top of Simple mode. Make the connection status and recovery action easy to reach at a narrow window size. Put secondary workflow entries below the primary controls. Preserve the detailed pages for managing subscriptions, running checks and editing network settings.

Open a bounded server picker on request. Keep the current server identifiable, support case-insensitive search across the full subscription and make all entries accessible. Searching or navigating the results must leave the committed server and auto-select setting intact. Explicit activation uses the manual selection behavior from PR #725. A separate action reveals the config editor so choosing a server does not expose the saved subscription URL.

Replace the static routing examples with wording based on the route mode and a link to the existing application rules. Keep the applied mode distinct from pending settings. This is a shared desktop UX change with Windows verification first.

## How

1. Read the current Simple page, connection-status projections and manual server picker.
2. Simplify the layout using existing Avalonia controls and design tokens. Keep English and Russian labels in the shared localization table.
3. Exercise the real page at default and narrow widths with a disconnected fixture and a long subscription. Verify filtering, zero results, active selection, refresh and explicit activation. Use only synthetic subscription data.

## Verification

1. Build through the hosted Release jobs.
2. Run selection and Simple-mode regressions in the hosted desktop suites.
3. Update this outcome and the defect ledger.
4. Obtain independent scoped review of the picker state and UI keyboard behavior.
5. Verify desktop headless rendering and retain screenshots with synthetic values. Live release acceptance is separate.
6. Keep the existing public ViewModel characterization surface or record intentional changes with tests.

## Outcome

The current Windows layout was observed with the subscription connected. The planning head `27ea2f96` passed eight GitHub checks on PR #728.

The Simple page places the compact status and connect action above the server and routing controls. The list opens on demand, searches all subscription results by name or host, and caps the viewport at 172 logical pixels. The current selection remains separate from the list highlight. The exact row is saved only on activation. Subscription editing and checks remain behind a second disclosure.

Static XML namescope checks, YAML parsing and `git diff --check` passed. Hosted regressions exercise both auto-select states, arrow navigation, duplicate labels, hidden link input and persistence. The Windows suite also captures the 360 px Russian and 520 px English layouts using synthetic data. Exact implementation CI and screenshot inspection are pending.

The Linux runs at `47098b98` and `5eaa71c2` failed the four hidden-link assertions. The checks queried effective visibility on the expander's content, which can be outside the rooted visual tree when collapsed. Replaced the expander with a toggle and an inline panel whose visibility follows it. The assertions now check both visual-tree attachment and effective visibility. The merge with `7bb2b767` preserved both defect-ledger additions. Follow-up checks are pending.

All nine checks passed at `4231f120`. The Linux suite reported 3,885 tests. The Windows suite passed 4,012 with 31 skips. Inspected all four synthetic 360 px and 520 px captures in artifact `simple-desktop-windows` from run 45007163572. The connection action and collapsed server row fit the narrow layout. The filtered list shows different endpoints for duplicate names. Matched the details toggle to the page's existing disclosure style and shortened the disconnected hint.

Independent review found stale live translations and mode drift when the app-rules shortcut follows an Advanced Servers visit. The code now refreshes labels on the dispatcher and preserves connection mode when saving the view. Regressions cover both findings. Final follow-up CI is pending.
