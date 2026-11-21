# Phase - DeepSeek setup and repair agent

- Branch: `dsh/subscription-ai-assistant-20261121`
- Base: `f08258c431f2e21591e13ff174f023803b728c76`
- Risk: MEDIUM. The agent applies live routing settings and uses a metered upstream API.
- Rollback: Restore the routed-app settings and live session captured before execution. Roll back the feature by disabling its vpnctl route and reverting the client change.

## Intent

A VPNRouter subscriber gives the app a goal in everyday language. The agent reads relevant local state, applies the changes through VPNRouter's own operations, and checks the result. One user request authorizes the reversible steps needed to reach that goal. The user can stop the run and undo its changes.

## First working scenario

In Standard desktop, the user enters **Route Chrome through the VPN and leave the other apps direct** and submits once. VPNRouter resolves the installed app, sets split mode with Chrome in the include list, applies or starts the VPN when the goal requires it, and checks the effective routing and connection. Use the same app-selection mapping, routing fields and lifecycle as the existing controls. A second run with the same goal leaves the effective state unchanged.

Use the same execution path for future repair operations. The next target is **Fix my connection**. Start with repairs that the existing problem detector and lifecycle can check. Add operations for the rest of the app as their inputs, verification and undo behavior become available.

## Execution

1. Collect current connection state, applicable checks and candidate application IDs. Resolve names locally from the user's request. Supply the model with the relevant subset of supported operations and compact state.
2. Use DeepSeek to return a validated operation with typed arguments or a clarification for an ambiguous target. Expand one logical operation into local setting changes and a single coordinated Apply. Keep a revision of the settings and a snapshot of the fields the operation owns.
3. Dispatch through the existing settings and tunnel lifecycle. Reuse app routing selection, `ApplyPendingChangesInternalAsync` and `VpnEngine.ApplyAsync` where they satisfy the operation. Verify committed routing and current runtime readiness. Confirm the old session retired before relying on a replacement.
4. If validation, Apply or a required postcondition fails, restore the previous persisted settings and live state in the same work flow. If a concurrent edit wins, retain it and report the conflict. On cancellation, stop issuing actions and complete recovery of any partial change. Keep Undo available after a successful change.
5. Show the outcome and verification, for example **Chrome is set to use VPN; the tunnel is connected**. Distinguish applied policy and tunnel readiness from traffic actually observed. Ask only when there is an ambiguous target or a system authorization the app requires.

## API and data

Use a separate `POST /api/v1/assistant/vpnrouter/plan` route. Reuse vpnctl's deployed NINA credential lookup, provider client and rate-limiting pattern. Preserve the NINA route and payload. In the initial first-party rollout, trust `https://ninitux.com` and build the request URL independently from the saved subscription URL. Use `device` for `/api/v1/app/config/<32 lowercase hex digits>` and `subscription` for `/sub/<token>` or `/api/v1/sub/<token>`. Send the credential once as HTTPS Bearer authentication with redirects disabled. vpnctl resolves a current enabled account, applies an account-level quota across both credential forms and uses its server-side model key.

The request carries the user's goal, UI locale, supported operation IDs and relevant typed state. Show the scope of the shared data in the action UI and update `PRIVACY.md`. Keep subscription details, local paths, app inventories and raw diagnostics local. Translate any opaque target ID back to a local selection. Accept only known operation IDs with validated arguments, eligible targets and a matching settings revision. A bounded reply supplies an operation or a specific clarification. Return `Cache-Control: no-store`. Treat a service outage as an agent-run failure and preserve the existing VPN state.

## Verification

1. Build the modified desktop and vpnctl components in CI or on prepared workers.
2. Exercise entitlement, quotas, unknown operation, ambiguous target, obsolete settings revision, repeated request, apply failure, cancellation and rollback. Verify that a malformed model response cannot change settings. Check both restored configuration and restored session.
3. Document the actual agent operation, end state, undo behavior and data sent. Preserve the original subscription and NINA contracts.
4. Independently review the model-to-operation validation and recovery from partial changes.
5. In the approved desktop environment, start with a different routing mode and submit the natural-language request once. Observe Chrome traffic through the VPN and a control app's traffic direct. Inject an Apply or verification failure and prove the previous settings and session return. Record configured-only status when traffic evidence is unavailable.
6. Test normal manual Apply, subscription refresh and Connect/Stop with the route enabled and disabled. Confirm the agent uses the same routing semantics as the existing controls.

## Outcome

Revised the product goal to execution, verification and undo. Confirmed that VPNRouter has a shared Apply path for its desktop and service modes. The current Fix Problems action provides a local snapshot pattern and retries with the old MTU; the agent needs to verify and restore the whole change it owns. Product implementation and verification are pending.
