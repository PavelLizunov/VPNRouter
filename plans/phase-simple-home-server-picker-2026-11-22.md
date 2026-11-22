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

## Category picker follow-up

Continue PR #728 from verified head `9efdc0a3`. The approved design groups
subscription servers by their existing protocol use case. Keep the Simple home
and its routing controls. Use one open group, global search, separate auto and
disclosure actions, and compact rows with the current measurement. No location
is inferred from the provider or endpoint.

Category selection persists independently of the observed active server.
Startup and recovery choose only from that group. An explicit row selection
pins its subscription ID and row key. Missing choices require a new selection.
Keep the full subscription cache available for required route dependencies.
Legacy settings retain their selection behavior.

Risk is medium because selection affects config generation and recovery.
Rollback is a revert of this follow-up. Verify the persisted policy, generated
outbounds, scoped failover and narrow keyboard-operated picker. Use hosted
builds, desktop tests and synthetic Windows captures. Review startup, refresh,
detour and failover before push. Keep the public ViewModel surface stable.

## Outcome

The current Windows layout was observed with the subscription connected. The planning head `27ea2f96` passed eight GitHub checks on PR #728.

The Simple page places the compact status and connect action above the server and routing controls. The list opens on demand, searches all subscription results by name or host, and caps the viewport at 172 logical pixels. The current selection remains separate from the list highlight. The exact row is saved only on activation. Subscription editing and checks remain behind a second disclosure.

Static XML namescope checks, YAML parsing and `git diff --check` passed. Hosted regressions exercise both auto-select states, arrow navigation, duplicate labels, hidden link input and persistence. The Windows suite also captures the 360 px Russian and 520 px English layouts using synthetic data. Exact implementation CI and screenshot inspection are pending.

The Linux runs at `47098b98` and `5eaa71c2` failed the four hidden-link assertions. The checks queried effective visibility on the expander's content, which can be outside the rooted visual tree when collapsed. Replaced the expander with a toggle and an inline panel whose visibility follows it. The assertions now check both visual-tree attachment and effective visibility. The merge with `7bb2b767` preserved both defect-ledger additions. Follow-up checks are pending.

All nine checks passed at `4231f120`. The Linux suite reported 3,885 tests. The Windows suite passed 4,012 with 31 skips. Inspected all four synthetic 360 px and 520 px captures in artifact `simple-desktop-windows` from run 45007163572. The connection action and collapsed server row fit the narrow layout. The filtered list shows different endpoints for duplicate names. Matched the details toggle to the page's existing disclosure style and shortened the disconnected hint.

Independent review found stale live translations and mode drift when the app-rules shortcut follows an Advanced Servers visit. The code now refreshes labels on the dispatcher and preserves connection mode when saving the view. Regressions cover both findings. Final follow-up CI is pending.

The grouped picker and scoped runtime preference are staged for the #728
follow-up. Read-only runtime audit identified unscoped exact-only routes,
obsolete preferences after explicit source changes and unsafe enum parsing.
Category auto now selects an eligible route on connect or recovery. Advanced
periodic auto selection remains a separate setting. Pinned chain IDs can
refresh when the existing route identity and feed still match. Added regressions
for unknown YAML categories, protocol eligibility, explicit feed changes and
scoped recovery. The current staged delta passed whitespace and XAML syntax
checks. Hosted builds, tests and Windows capture review remain pending.

Hosted Linux run 45014407551 at `6275549b` reported seven failures from
the settings round trip. The generated YAML serializer wrote the getter-only
`IsCategory` property and the reader could not set it. Computed category
access now uses methods, leaving only the three wire properties visible to
the generator. The macOS acceptance rerun passed on the same head, job
137184217889. Exact follow-up hosted results are pending.

All ten hosted checks passed at `f994a779`. Linux passed 3,809 tests
with 86 skips. Windows passed 4,022 tests with 31 skips. Examined the four
synthetic Windows images in artifact 14751244900, including the open
picker at 360 and 520 logical pixels.

The consolidated review found that the disconnected summary could show
the wrong server after a pin disappeared or two server names matched.
The summary now uses the exact selected row and displays a short
unavailable label when that row is missing. Recovery reports an
unavailable category separately from an exhausted category, and
unsupported rows show a capability warning and cannot be pinned in Simple.
Regressions exercise these cases and keyboard activation in an expanded group.
Cold feature-probe latency on macOS or Linux remains unmeasured and is tracked
in the ledger. The review follow-up passed XAML parsing and whitespace checks.
Hosted confirmation is pending for the follow-up.

## Country flags and owner Windows preview

Scope is the grouped and searched Simple subscription rows. Reuse the
Business ISO flag images with their source and license. Read their explicit
country labels and leading regional indicators from provider names. Unknown
country labels show the neutral globe. Keep row identity, routing and saved
settings unchanged. Verify flag resources and narrow Windows UI in hosted
checks. Build an exact-source preview and install it on the owner's local
Windows VM on request. Preserve the installation and settings for rollback.
This preview does not publish a release or tag.

The owner preview configuration has 15 cached subscription rows. Its labels
and subscription metadata supply no country codes. Twelve rows have literal
global addresses and the other three contain hostnames. Add a bundled
country-only fallback for literal addresses, with explicit provider labels
taking precedence. The pinned DB-IP Lite November 2026 CSV matches the
publisher's digest after decompression, and its offline ranges cover all
12 literal addresses. The picker does not resolve hostnames for flags.
All ten hosted checks passed for the first flag-resource revision
`b926cefd`, and a Windows preview package was built. Use the follow-up
package for the owner preview so the actual subscription can show flags.

## Subscription link, search and checks

The owner supplied a narrow Simple-mode design and ten interaction criteria.
Keep the link editor in the connection card, independent of the server list.
Keep the first-run link and Connect flow. Give existing configurations a
separate draft, explicit save, cancellation and field-local feedback.

Validate an explicitly submitted URL or server URI through the existing
subscription fetch/refresh and server parser. Prepare a remote result before
publishing it, reject stale submissions, and preserve accepted runtime
ownership. A saved link takes effect on the next connection or explicit
Apply. Keep DNS, transports, failover policy, package and release out of scope.

Put search behind a focused disclosure and filter the displayed rows by
existing name/location data. Closing it restores the group view. A recognized
link can be transferred to the draft without applying it. Put diagnostics on
a separate button. Preserve category protocols, server flags, ping, and picker
selection. Use the existing branch for this follow-up; rollback is its revert.

Verification targets: exact-head Release build and full suites, focused
draft/validation/stale-response and picker keyboard regressions, brief outcome,
independent review of save ownership, headless Windows captures at narrow
widths in both themes, and the existing ViewModel surface characterization.
Live candidate deployment is outside this task.

The view separates the link editor, search disclosure and diagnostics.
The new save path prepares a detached candidate via the existing parser and
subscription cache updater, persists it, then updates the next selection.
The editor never calls the Connect or Apply paths. The review checked these
ownership boundaries before implementation. Added headless coverage for the
first run, Escape, query transfer, input preservation, and narrow themed
layouts. The fetch seam covers rejection and stale completion. Local AXAML
parse, named-control/event checks and whitespace checks passed. Exact-head
hosted build, tests and captures are pending.

All ten checks passed at `67987fae`. Linux passed 3,831 tests with 86 skips,
and Windows passed 4,044 tests with 31 skips. The Windows artifact
`14758173417` includes headless Avalonia captures at 320, 360, 450 and
520 px. The 320 px and 450 px captures fit the link input, long draft, and
actions in light and dark themes. Visual inspection found low contrast on
Save link. The follow-up uses the existing solid action brushes. Live
installation and service control were outside the brief.

Independent review confirmed the detached save and stale-response guards.
The follow-up preserves other registered feeds and keeps the previous runtime
visible. The editor shows progress without removing keyboard focus from Save.
Newly saved direct links round-trip in the private settings and are offered
only when the stored row still matches. Legacy manual rows have no original
link to recover. The checks button explicitly starts the ping check, as in
the supplied preview, and reveals the existing deep-check and refresh actions.
Country search uses stored name and country data, with the existing bilingual
aliases and the platform's country labels. City names are matched when present
in a server name. No geolocation lookup is added. Added regressions for feed
preservation, exact-row direct-link recovery, and focus after an explicit save.

## Advanced subscription category filter

Use the same protocol categories, labels, icons and protocol summaries in the
Advanced Subscriptions list. Provide an All categories entry and keep the
existing row template, diagnostics and virtualized scrolling. The filter is
view state. It must not pick a server, write settings, restrict auto-selection
or reconnect, even if a refresh removes the shown category. Row activation
still uses Advanced manual selection and its service-managed Apply behavior.

Use the existing task branch, with a revert as rollback. Verification gates:
exact-head Release build, existing full suites and a focused filter/selection
regression, plan outcome, static review of selection boundaries, narrow
Windows headless capture, and unchanged desktop public surface.

The Advanced Subscriptions tab now displays the shared category names and
protocol summaries in a filter above its virtualized server list. Server
rows retain diagnostics and use the existing country flags when available.
Category changes update only view state. Explicit row activation still
uses the Advanced selection path. The headless regression covers filtering,
an unavailable category after refresh, the untouched choice and settings,
and a 360 px capture. Hosted verification is pending.

At `16b2d78c` the Linux filter and manual selection tests passed. An older
icon test counted the new filter icon as a server-row icon. The follow-up
checks the visible fallback discs inside the server list and the visible
country flag on the filtered row separately.

All ten checks passed at `08d314c9`, with 4,046 Windows tests passing and
31 skipped. The Windows capture showed an existing 360 px layout issue:
the intent picker crowded the auto-select label onto one letter per line.
Stack the controls and assert the checkbox has space to lay out its label.

Both hosted macOS attempts at `b4c21893` reached PF reacquisition after
confirming the reference was absent from the kernel. The fixture compared
host record bytes, but [XNU derives the token from the allocation
address](https://github.com/apple-oss-distributions/xnu/blob/xnu-11417.140.69/bsd/net/pf_ioctl.c#L318-L359).
The acceptance check now requires a live reference owned by a new pfctl
PID, even if the token value repeats. Privileged runtime code is unchanged.
Extended the Advanced keyboard case to verify that arrows preview a row and
Enter requests Apply through the existing service-managed path.

## Shared subscription selection and disclosure

Continue from `3ccbddb4` on this task branch. The updated design brief asks
for one accessible disclosure across the Simple card header and one
category/server picker in both modes. Extract the existing native picker and
link editor. Keep diagnostics, subscription management and connection intent
settings. The previous Advanced web-test checkbox controls a global sing-box
pool, so remove that alternate subscription control. Existing settings without
an explicit subscription preference retain their runtime behavior.

Use the same preference and explicit commands for category auto and exact
pins on either page. Keep search/disclosure local to each view. Exercise
mode switching, a connected service-managed choice, refresh and ping with
unchanged selection, same-category probes, exact pins, and stale results.
Risk is settings or reconnect side effects while selecting or switching
views. A revert on this task branch restores the previous implementation.

Gates: exact-head Release build, existing suites plus focused regressions,
this outcome, independent runtime review, native Windows headless renders
at narrow widths in both modes, and unchanged public VM surface. Live
candidate deployment and release are outside this brief.

Both views now use the same tree, link editor and subscription choice
commands. The Simple header is one toggle. Advanced retains row retests,
health verdicts, bulk diagnostics, intent, and registered-feed management.
The cold-start path uses the same scoped probe from both views. The full
subscription cache, exact manual pin and existing recovery limits remain
in use. Added regressions for disclosure clicks, category/manual round trips,
filter and ping isolation, service-managed Apply, and switching views
during a probe. Windows captures exercise both choices and the Simple
header at 360 px light and 450 px dark. XML/handler checks and
`git diff --check` passed. Hosted verification and independent review
are pending.

The Linux build/test, macOS system job and Windows characterization job at
`1efd17f7` stopped at the same missing Avalonia primitives import. Restore
the import in the Simple view and its companion icon test before retesting.

At `ee9155fc`, the Release build and macOS system checks passed. Linux ran
3,920 tests with seven visibility failures. The tests checked effective
visibility on not-yet-attached content inside the shared controls. Verify
both attachment to the window and visibility. The follow-up also preserves
a scoped choice across Advanced non-server saves and an unavailable pin
after refreshing its original feed. Regressions cover both persistence
paths. Hosted retest remains pending.

All 11 checks passed on `fc9ac716`. The Linux TRX records 3,836 passed,
86 skipped and no failures. The Windows artifact includes the 320 px first
run and both modes at 360 px light and 450 px dark. The 450 px scenario
exercises a service-managed pending selection while a different server is
active. Visual inspection revealed that the running server label showed
the pending manual mode and the disclosure could render halfway through
its rotation. Separate the running caption from the selected policy and
remove the animation on the header chevron. The updated fixture asserts
the running caption stays accurate after switching views.

## DNSTT category and picker scrolling

The installed Windows preview at `b85fc444` grouped DNSTT as Everyday and
displayed nested scrollbars over the expanded picker. Classify DNSTT as
Emergency by protocol or profile. Keep exact pins and the manual-only
selection rule. Use the host viewport for the full category tree and its
search results. Test actual wheel input and visibility of the last row
at 360 px in both desktop modes. Scope is the shared picker and category
classifier. Roll back by reverting this block on the task branch.

Gates are the exact-head Release build, category and wheel regressions
with the existing suite, Windows headless captures, a scoped source
review, this outcome, and unchanged selection behavior.

The classifier and protocol summary now recognize DNSTT independently of
VLESS transport. The expanded server list and search results use the host
viewport. Removed the nested group scrollbar and list height caps. XML and
diff checks passed. Hosted wheel tests and before/after inspection are
pending.

Linux CI at `c8d03da8` passed grouping and verified that wheel input
moved the host viewport in both modes. The new regressions then tried
to read the last virtualized row before it was realized. Bring that item
into the host viewport through the list's item index before checking
its position and the saved selection.
