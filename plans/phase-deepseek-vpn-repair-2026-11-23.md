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

Desktop repair is implemented on the dedicated branch. The repair control is
outside the model action inventory. A user-triggered run checks the saved
configuration and the accepted runtime before committing a change. It preserves
manual selections, traffic routing policy and the previous settings if a
candidate fails. The single-outbound follow-up `65f455f9` also honors the
explicit row marker when the legacy auto-select checkbox is enabled.

The Windows preview at `cec1937c` on WINBRAT passed four interactive cases.
An invalid MTU of 9000 was corrected to 1420 in the live TUN and saved YAML
after a successful proxy request. Disabled TUN automatic routing and an empty
selected-app list produced specific diagnoses with the settings and stopped
state preserved. A deliberately unreachable subscription with auto-select off
left the selection unchanged and restored direct routing without a core or
TUN. The original installation and config hash were restored and lease 98
was released. Automated tests verify alternate-server eligibility. A live
switch, Android, and recovery from unrecognized startup failures remain
unverified.

Exact hosted follow-up CI at `65f455f9` passed all six jobs in run
`45173196488`. Linux recorded 3,931 passed tests, including 62 repair-specific
cases. The Windows 520 x 680 headless capture of `38af31e8` from run
`45172338584` showed the repair control and the visible Disconnect button.

The separate WINBRAT replay used 132 unusual prompts with synthetic
application state, including one local-only validation case. All 33 supported
first decisions matched and 72 of 98 expected abstentions matched. The 26
unintended action proposals included 19 projected VPN or routing settings
changes. The experimental stricter prompt repeated the same inputs and
matched 32 of 33 supported decisions and 89 of 98 abstentions. Nine proposals
still changed simulated VPN or routing settings. No model reply was executed
against the app. The stricter prompt remains an experiment, and the open
text-action P1 remains a release blocker. No rollout or merge was performed.


## Independent review follow-up

- Keep exact trial selection in memory without changing the saved subscription
  row ID or automatic selection policy. Exclude pinned alternate rows.
- Propagate cancellation to the Windows path-MTU child process and keep the
  existing per-ping deadline. Preserve the no-argument diagnostics entry point.
- Retry a different alternate only after a clean rejection for a changed server
  or failed proxy check. Exceptions during cleanup or commit stop the repair.
- Verify proxy checks fail closed without a controller. Cover row identity and
  cancellation with focused tests and rerun hosted CI. Revert the follow-up
  commit to roll back the changes.

Exact revision `09969ed1` passed all six jobs in run `45176953517`. Linux
passed 3,935 tests and Windows passed 4,148. The new tests verify that a
single-server trial produces the saved identity, leaves the automatic pool
available for the next regular start, skips pinned alternates, and cancels
the MTU sequence during an active ping. The Windows process-runner contract
also passed against a real local ping process. A tunnel without a controller
already failed the proxy check at the reviewed revision. The new test
pins that behavior.

The first follow-up at `b50a572a` failed the headless settings serialization
contract. The private runtime field introduced at `09969ed1` avoids generated
serializer properties, and the same contract passed. The previous WINBRAT
scenarios still describe the earlier preview. Live switching to an alternate
server remains unverified on WINBRAT.
