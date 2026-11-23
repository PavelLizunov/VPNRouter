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
- Retain normal command guards, pending changes and operating system prompts.
  Cover application controls through reusable interaction types. Native
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
