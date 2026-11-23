# DeepSeek desktop acceptance follow-up

## Scope

The published Windows candidate at `d50cfb2f` connected in a cold run on
WINBRAT at 520 x 680. The Simple page grew after connection and the
Disconnect button was below the scroll viewport. The same candidate rolled
back the assistant's Chrome-only route on repeated attempts. The client
reported a generic apply failure. An intermittent HTTPS canary failure was
also observed. The evidence does not establish which engine check rejected
the candidate.

Keep the Simple connection action in the viewport while warnings, the
assistant and configuration scroll. Trace assistant verification through
the accepted candidate and its proxy health check. Keep the commit and Undo
bound to the verified session, preserve earlier settings on failure and
keep actual HTTPS and UDP verification enabled. No retag or release change.

## Verification

1. Build the Release solution in CI.
2. Exercise both reachable connection actions in a bounded Avalonia window
   with the assistant expanded and verify that scrolling leaves them in place.
   Exercise the assistant verification boundary with a deterministic fixture.
3. Record the exact revisions, failed release gates and new evidence in the
   outcome and defect ledger.
4. Review runtime and regression findings independently.
5. Verify the viewport at 520 x 680 on WINBRAT and exercise Chrome-only Apply,
   independent direct traffic, Undo, Connect/Disconnect and proxy health.
6. Compare the generated route and session identity across apply and rollback.

The rollback is a revert of the follow-up commit. The existing published tag
remains immutable.

## Outcome

The connection controls move to a fixed footer outside the Simple page's
ScrollViewer. The viewport fixture covers disconnected and connected
states with the assistant expanded. The screenshot fixture uses a
520 x 680 window and a synthetic subscription.

Candidate verification now probes the controller from the accepted
configuration. The previous path reconstructed an IPv4 controller from
the configured port and could use stale credentials or the wrong address
family. DeepSeek verification attempts HTTPS first and retains the
bounded fallback. The operation checks the accepted session, process,
configuration and exclusive route before it persists the settings.
Rejections identify the verification stage in the log without exposing
addresses, subscription data or proxy response text.

This corrects source-level differences in verification. The cause of the
published WINBRAT Apply failure still requires a repeat run. CI and
live verification are pending.
