# Make subscription refresh visible in Standard

- Branch: `dsh/simple-subscription-refresh-turnrelay-20261127`.
- Base: `d2a3ed197623a00570fcfafce3893326ce6e3995`.
- Risk: Low. The change affects desktop subscription controls and picker rows.
- Rollback: Revert the UI and regression changes together.

## Why

In r5 the Simple home shows the subscription link editor, while refreshing
the current server list requires opening the server checks panel. The same
picker in Simple and Advanced also shows TurnRelay hops with the unsupported
server label, despite those hops being available to linked exits.

## What and how

Place refresh, progress and failure feedback on Simple home. Reuse the
existing refresh command and in-flight guard. Filter TurnRelay hops out of
the shared picker groups and search while retaining the complete subscription
pool for resolving detours. Leave the existing exit and auto-selection policy.

## Verification

1. Build and run the applicable Standard suites in hosted CI.
2. Assert that refresh is visible with checks collapsed and executes the
   registered feed refresh. Exercise TurnRelay group and search rows.
3. Record exact-source outcomes and the original UI finding.
4. Review the shared picker, narrow layout and subscription state ownership.
5. Inspect isolated CI renders at the supported narrow widths.
6. Check ViewModel characterization and confirm a pinned exit still produces
   the expected TurnRelay hop in the generated config.

## Outcome

Implementation and CI pending.
