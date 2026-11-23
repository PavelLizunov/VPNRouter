# Verified VPN repair in the assistant

## Why

The setup assistant can verify a single action. It lacks observed repair
recipes for invalid tunnel settings, failed startup, and unavailable servers.
The unusual-request corpus also has no execution results yet.

## Scope and invariant

- Add a user-initiated repair action in the existing assistant. Use local
  configuration and live probe results, including offline Windows path MTU.
- Diagnose relevant settings and ownership failures. Try an alternate server
  only in the configured automatic selection scope and only after a
  working proxy check. Preserve explicit pins and traffic protection.
- Verify the original or candidate start through the accepted runtime.
  Persist only the verified change, preserve concurrent edits, and use
  the existing owned-session cleanup and rollback.
- Unknown or ambiguous failures must leave an actionable diagnosis and
  retain the user's settings.
- Replay unusual requests separately with synthetic app state. Keep model
  decisions, local actions, and live dataplane verification distinct.

## How and risk

Use a read-only repair policy and the existing verified candidate lifecycle.
Keep raw logs, local configuration, and subscription contents on device.
Use already registered probes rather than a new network runner. The
working app and gateway have separate rollout authority. Revert the task
commits to roll back source changes.

## Verification

1. Exact-source Release build on authorized hosted infrastructure.
2. Focused candidate, preservation, cancellation, and unusual-input tests,
   followed by the full hosted suite.
3. Record the actual supported cases and evidence below.
4. Review lifecycle, repair eligibility, and user intent independently.
5. Inspect the assistant panel in the narrow headless fixture and exercise
   live repair on the fixed Windows test worker where practical.
6. No mechanical extraction.

## Outcome

Verification pending.
