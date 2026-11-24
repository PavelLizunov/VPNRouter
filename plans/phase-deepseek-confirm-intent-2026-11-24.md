# Confirm desktop assistant actions

The desktop text assistant at 511099ed can change the tunnel or routing after
an informational request. The existing replay includes 98 requests requiring
abstention. The target is zero executor calls for those requests and an
explicit, locally described confirmation for each requested change.

Limit the model to one locally understood effect and bind its value, direction
and app identity to the original request. Verify proposal, settings, window
and tunnel identity again across the confirmation boundary. An unknown goal,
cancelled or outdated proposal cannot reach the executor. Keep local repair,
Undo, crash analysis and signed update handling independent.

Verify a Release solution build and focused plus full tests in hosted CI.
Exercise the historical cases, wrong toggles and route directions, cancellation
and stale confirmation. Inspect the diff independently and verify the panel on
WINBRAT before release. There is no public ViewModel API or data migration.
Revert this change as a unit if it regresses the panel; keep publication held.

## Implementation and verification

The text executor now advertises one semantic action. It accepts only a
matching operation, target, and toggle value. The panel shows its own
localized description and waits for Confirm. Cancelling the request,
detaching the panel, or changing the settings or resolved target prevents
execution. Free-form text outside the single-action forms can yield a locally formatted
example to enter as a separate request. This path cannot offer confirmation
or execute a control.

The fixture exactly matches the 98 unique no-action texts of the original
sealed replay. Panel tests cover that corpus, wrong direction and value,
decline, cancellation, a changed revision or default browser, confirmation
of a persisted DNS preference, and navigation. The fixture, XML wiring,
and whitespace checks passed locally. Hosted compilation and runtime checks
apply to the product commit. Windows acceptance and publication are separate
gates.

## Review follow-up

At 28dc9489 all nine PR checks passed. The Linux suite executed 4,295 tests
and the Windows suite passed 4,508 with 31 skips. The independent review of
this head found gaps in the decline outcome, early recognition of satisfied
requests, disclosure of route effects, and resolver and panel fixtures.
Verify these cases on the follow-up head. The production network page already
uses the same settings label as the confirmation via LblTabNetwork. The
command parser already has direct positive grammar tests.

The follow-up handles refusal in the panel, checks already-satisfied toggles
and routes locally, and describes profile and regional bypass changes from
the prepared settings. After confirmation it checks that the effective route
still matches the preview. The exact resolver has Windows and Unix cases.
The 98-case fixture checks both the parser and the ViewModel and pins the
ordered UTF-8 texts independently of checkout line endings. Generic UI
automation is now a test helper.

## Acceptance

All nine PR checks passed on `e9d3be7638edfb47beb6a59bd686f2b31ecdbdb1`.
Linux passed 4,321 tests, including the 98 original abstention cases. Windows
passed 4,534 tests with 31 skips. [CI run](https://github.com/PavelLizunov/VPNRouter/actions/runs/45293010039)

The installed Windows preview matched that commit and the verified package
checksum. On November 24 the desktop panel passed 54 checks on WINBRAT using
the registered test subscription and the authenticated step endpoint.
Questions and quotations left the configuration unchanged. Declining kept
the original bytes, confirming changed only the requested DNS preference,
and repeating the enabled preference produced a local no-op. Confirming the
opposite value restored the original bytes. The original installation was
restored and the test lease released. [Preview build](https://github.com/PavelLizunov/VPNRouter/actions/runs/45293092689)

An earlier harness attempt assumed that two different informational requests
would return different messages. The corrected check waits for completion.
The focused acceptance closes the text-action P1 for this source revision.
The combined Standard prerelease has separate integration and release gates.
