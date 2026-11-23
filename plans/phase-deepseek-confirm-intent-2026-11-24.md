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
