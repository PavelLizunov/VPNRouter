# DeepSeek application harness

## Goal

Use the existing Avalonia automation driver as the model's application
interface. Discover supported interactions from the live window so that
connection, navigation and settings use the same mechanism. The model
selects a current control and the app executes its normal interaction.

## Scope and invariants

- Share local control traversal and execution with `AppAutomationDriver`.
  Keep the opt-in developer listener and its wire contract compatible.
- The user starts a bounded operation in the assistant. The model receives the
  goal, connection state and available control metadata. The local session
  holds control references and values. Model requests carry no automatic
  screenshots, raw field values, logs or subscription credentials.
- Use a versioned planning contract through the existing vpnctl subscription
  gateway. Accept one action at a time. Each reply can act only on a control
  from that request. Re-check window, enabled state and visibility before
  executing. Take a fresh observation before the next action.
- Discover controls explicitly marked for assistant use. Built-in navigation,
  connection and settings actions use reusable interaction types. Verify
  requested process names against running apps and registered Windows paths.
  Retain normal command guards, pending changes and operating system prompts. Native
  dialogs and programs outside VPNRouter are outside the window surface.
- Preserve existing published clients and names. Keep rollback by reverting
  the client change and serving the previous planning endpoint.

## Verification

1. Build an exact Windows candidate and compile the gateway in CI.
2. Test discovery and execution across unrelated controls with a fake model.
   Cover stale controls, disabled controls, unknown IDs and payload limits.
3. Update the privacy disclosure for the new metadata.
4. Review the model boundary and normal command behavior independently.
5. Verify the representative connection and application routing commands in
   a disposable Windows fixture.
6. Check the developer automation and previous assistant protocol for
   compatibility.

## Outcome

The assistant discovers named actions from the live window. It runs the
same controls used for connection, tab navigation and selected app and DNS
settings. For exclusive app routing, the model supplies an executable name.
The client resolves it locally and uses the existing route verifier and Undo.
The privacy notice is a single sentence. The complete data flow is in
`PRIVACY.md`.

Review led to positive control marking and local verification of executable
names. The gateway validates decoded text before returning it, including
escaped credential rejection. The model gets up to six steps per request.

The desktop candidate is `b8b0e9e3`. Its Windows package and screen capture
passed hosted run `45120648117`. All six jobs in run `45120583469` passed,
including Linux and Windows tests. The tests exercised the Simple Connect
button through the window automation and validated an Edge process argument
from the same planner contract.
The gateway is `d6128ad3`. Candidate build `45120298423` and all 19 contract
tests in run `45120484348` passed, with coverage of both protocol versions.

The running subscription service accepted the original route at the
authentication boundary and returned 404 for the step route. That separates
the hosted verification from activation and the live DeepSeek routing
scenario. GitHub's connector declined both draft PR requests with 403.

## Follow-up: missing gateway route and crash diagnosis

The installed gateway responds to POST /plan with 401 without credentials
and to POST /step with 404. A direct synthetic request to DeepSeek Flash
with an archived OpenCode Go key returned 200. Give the desktop a distinct
response for the missing gateway route.

After restart the assistant can analyze the latest managed crash report.
Use the existing report and log tail to build a bounded set of diagnostic
codes locally. The crash analysis request is read-only and carries no raw
report text. Normal configuration still uses the application driver.
Rollback by reverting this follow-up in the client and gateway separately.

Verify the release build and existing tests at exact hosted revisions,
crash parsing and wire validation with synthetic fixtures, model-boundary
review, privacy text, and the desktop panel separately. No mechanical
split or Android change is in scope.
