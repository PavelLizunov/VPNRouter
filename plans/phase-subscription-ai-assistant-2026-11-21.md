# Phase - DeepSeek assistant for VPN subscribers

- Branch: `dsh/subscription-ai-assistant-20261121`
- Base: `f08258c431f2e21591e13ff174f023803b728c76`
- Risk: MEDIUM. The feature uses subscriber credentials and a metered upstream API.
- Rollback: Disable the new vpnctl route and revert the client feature. The VPN connection and subscription contracts stay compatible.

## Why

A VPNRouter subscriber should be able to ask the app to explain a connection problem in plain language, using the subscription already saved in the app. NINA's diagnosis flow provides the reference.

## Initial scope

- Add **Explain with AI** to VPNRouter Standard desktop diagnostics. Display the results of local checks immediately. On request, show the DeepSeek explanation alongside those results.
- With one enabled, eligible vpnctl source, use that source. With multiple eligible sources, use the active source or offer a choice by its display name. Continue to support ordinary diagnostics for every source.
- In vpnctl, reuse NINA's first-party credential lookup, server-configured model client and rate-limiting pattern. Define a separate VPNRouter endpoint and a typed diagnostic report. Preserve NINA's deployed request and response schema.
- Validate the subscription against the server's current record at each call. Key the per-user limit by the resolved account across token and device identities. Make limits and rollout eligibility server-controlled. Confirm how the allowed plans map to that record before enabling subscriber access.
- Keep the model key on the server. Send the VPN credential only to the fixed vpnctl HTTPS origin in the authorization header. The model receives a small allowlisted report containing platform, current connection state and results of named checks. Display what will be sent before the request.
- Reach the control service using the app's normal network policy, including when the tunnel is down and the service is reachable. Treat entitlement, rate limit, outage and cancellation as assistant states independent of VPN state.
- Decide the production usage allowance and gateway configuration before rollout. Android can reuse the same API. A later phase can let the assistant propose a typed settings change, preview it and apply it on explicit user action through existing validation and rollback.

## Initial API contract

Use `POST /api/v1/assistant/vpnrouter/diagnose` on the official first-party vpnctl HTTPS origin. Keep the deployed NINA endpoint stable. vpnctld continues to obtain its public origin from operator configuration. The first VPNRouter rollout trusts the same `https://ninitux.com` origin as NINA.

Use `X-Vpnctl-Identity: device` for an `/api/v1/app/config/<32 lowercase hex digits>` URL and `subscription` for `/sub/<token>` or `/api/v1/sub/<token>`. Accept documented format parameters on the saved URL and extract the credential from the path only. Match the origin, path shape and credential alphabet. Build the request URL independently. Send the credential once as HTTPS Bearer authentication with redirects disabled. An eligible-looking URL only makes the feature available for an attempt. vpnctl determines access from the current user record even when the app has a cached VPN configuration.

The version 1 report contains platform, UI locale, connection state and check outcomes. Use explicit checked, failed and unknown outcome values. Map existing typed observations for configuration and core readiness. Include network check outcomes only when measured for that report. `HealthCheck.Result.Message` and the exported report remain local. Render the explanation as plain text. Include a description of the transmitted fields in the UI and update `PRIVACY.md`.

Return `schema` and `explanation` with `Cache-Control: no-store`. Keep NINA's limits of 2 KiB for the incoming report and 4 KiB of explanation as initial bounds. The client sends one request per click and cancels obsolete requests. Map unauthorized or revoked access, rate limiting with `Retry-After`, and upstream failure to distinct assistant states. Apply per-account and source limits before inference and configure a separate rollout budget. The six-per-minute NINA limit is a reference for testing the mechanism.

## Implementation order

1. Specify the VPNRouter diagnostic report and eligibility contract in vpnctl. Add a fixture model, then share the existing gateway and authorization code where their contracts match.
2. Add the typed VPNRouter client and expose the action on desktop. Keep network calls cancellable when the report or subscription changes.
3. Exercise the full click-to-explanation flow in staging against the configured DeepSeek model.

## Verification

1. Build vpnctl and the Release desktop solution in CI or on prepared workers.
2. Test recognized token and device identities, disabled or revoked identities, unsupported feeds, shared quotas and provider errors. Assert that only report fields reach the model and that redirects cannot carry credentials away from vpnctl.
3. Document the feature, response states and rollout controls. Record the final revision and outcomes here.
4. Review the client-to-vpnctl trust boundary and model quotas independently. Preserve deployed NINA fixtures.
5. Verify desktop diagnostics, entitlement errors, cancellation and local diagnosis during a model outage in the approved UI environment.
6. Verify standard subscription refresh and Connect/Stop with the assistant route enabled and disabled.

## Outcome

Planning. Confirmed that vpnctl main includes NINA's deployed diagnostic endpoint and recognizes both credential forms. Reviewed the subscription resolver, HTTP client, diagnostics and privacy contract at the desktop base. NINA's draft action schema is a separate worktree change. Product implementation and verification are pending.
