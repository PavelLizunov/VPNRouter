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
