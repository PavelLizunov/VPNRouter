# User interaction boundaries and edge-case verification framework

**Date:** 2026-06-02  
**Status:** framework ADOPTED (lean) 2026-06-02. Phase 1 = contract docs in
`plans/interaction-contracts/` (FC + APP written; other Tier-A lazy). The 9
Public Configs product decisions are resolved below. Phase 2 = a small set of
FC-gating code fixes (B1/B4/G5), scoped + queued for a `v2.40.0-r1` candidate
AFTER `v2.39.0` cuts — they change the binary, so they must not land during the
v2.39-r7 soak. Phase 3 (machine-readable manifest + coverage tooling) is NOT
adopted now (premature for a solo project; the doc itself flags it optional).  
**Scope:** desktop and Android product behavior, feature contracts, edge-case
selection, state-transition tests, UI action gating and release verification

## Purpose

VPNRouter already has a large regression suite and a careful release process.
The next useful step is not to enumerate every imaginable click sequence. That
approach grows without limit and still misses the important bugs.

The useful step is to define the supported interaction space:

- which states the application can be in;
- which actions a user can perform in each state;
- which inputs and external failures must be handled;
- which combinations are intentionally forbidden;
- what must remain true after success, failure, cancellation and restart.

This document proposes a reusable framework for describing that space and
turning it into focused verification scenarios.

Public Configs is used as the first filled example because it combines network
fetching, cache state, long-running operations, cancellation, platform parity,
saved data and VPN connection changes. The same format should later be applied
to Applications, Servers, Subscriptions, Network rules, DPI bypass, Telegram
proxy, Emergency channel, updater and service management.

## Current baseline

The Public Configs audit in
`plans/public-configs-pipeline-audit-and-hardening-plan-2026-06-02.md` identified
several high-priority risks. The current Git history shows that the main P0
items have already received implementation work:

| Commit | Closed direction |
|---|---|
| `12992df` | client fetches `pool.json.gz` instead of raw 27 MB JSON |
| `593c319` | desktop `ExcludeRu` also applies to cached Verified entries |
| `767b6cc` | Android apply fallback no longer wipes the Servers list |
| `1b61922` | Saved recheck no longer treats residual Verified as fresh success |
| `eff7eff` | Android Connect requires deep verification and target counts Verified entries |
| `8c83203` | adversarial follow-ups: DNS leak, redaction and CTA checks |

The test suite has also grown substantially:

- approximately `1,395` xUnit/Avalonia test attributes;
- `172` dedicated `*Tests.cs` files;
- contract seams such as `IHttpClient`, streaming HTTP, filesystem,
  `IProcessRunner`, settings store, sing-box API and update source;
- headless UI screenshots and Windows visual-diff pre-ship gate.

This framework should be layered onto those assets. It is not a replacement
for the existing regression suite.

## Core idea

## Define an interaction envelope, not an infinite scenario list

A user does not interact with an arbitrary program state. The user interacts
with a bounded product surface:

```text
platform capability
  + durable settings
  + runtime state
  + current feature operation
  + external environment
  + input class
  + lifecycle interruption
  -> visible actions and allowed transitions
```

Call this the **User Interaction Envelope**.

For each feature, we document only:

1. state dimensions that the feature actually reads or mutates;
2. actions visible to the user;
3. allowed transitions;
4. forbidden transitions and their guards;
5. invariants that must survive every path;
6. representative boundary values;
7. fault-injection points;
8. platform-specific differences.

This reduces the pool of bugs because the product stops having implicit states.
An undocumented state is either removed, made explicit, or treated as invalid
input with a defined recovery path.

## Four classes of interaction

Every user-facing action should be classified into one of four classes.

| Class | Meaning | Required behavior |
|---|---|---|
| Supported | Normal product flow inside documented capability limits | Complete successfully or report an actionable external failure |
| Handled invalid | Reachable from user input, stale storage, migration or external data but not valid for execution | Reject safely, preserve prior good state, explain the issue |
| Forbidden concurrent | Action is valid in isolation but must not run during another transition | Hide, disable, coalesce or reject consistently |
| Unsupported | Outside the product contract or OS capability | Explain limitation; do not silently simulate success |

Examples:

| Situation | Class |
|---|---|
| Apply a deep-verified public config while VPN is stopped | Supported |
| Paste malformed VLESS URI | Handled invalid |
| Click Connect twice while VPN is starting | Forbidden concurrent |
| Enable Windows Firewall lockdown on macOS before that capability exists | Unsupported |

The distinction matters. "Unsupported" must not become "undefined". The app
still needs a safe response.

## Global state dimensions

The complete cross-product is intentionally not tested. Each feature chooses
only the dimensions it depends on.

### P - Platform and capability

| Code | State |
|---|---|
| `P.win-admin` | Windows desktop with required elevation |
| `P.win-user` | Windows desktop without elevation |
| `P.mac` | macOS desktop |
| `P.linux` | Linux desktop |
| `P.android-permitted` | Android with VPN permission granted |
| `P.android-denied` | Android without VPN permission |
| `P.cap-present` | Optional helper/binary/service capability present |
| `P.cap-missing` | Optional helper/binary/service capability absent |

Capabilities should be modeled separately from OS where possible. For example,
`wgturn-cli present` and `wgturn-cli absent` are useful states on the same OS.

### V - Main VPN lifecycle

Use one explicit lifecycle model:

```text
V.stopped
  -> V.starting
  -> V.running
  -> V.stopping
  -> V.stopped

V.starting -> V.failed
V.running  -> V.recovering -> V.running
V.running  -> V.failed
V.failed   -> V.stopped
```

Additional ownership dimension:

| Code | State |
|---|---|
| `V.owner-app` | current UI process owns the engine |
| `V.owner-service` | Windows Service owns the running VPN |
| `V.owner-none` | no active owner |

The UI must not equate `IsConnected` with engine ownership. Service-managed and
reconciled runtime states need explicit handling.

### C - Configuration mode

| Code | State |
|---|---|
| `C.generated` | manual/generated server list |
| `C.subscribe` | subscription-derived server list |
| `C.custom` | user-provided sing-box JSON |
| `C.invalid` | corrupt, legacy or unsupported persisted value |

Adjacent routing mode:

| Code | State |
|---|---|
| `R.include` | selected apps are routed through VPN |
| `R.exclude` | selected apps bypass VPN |
| `R.full` | full tunnel |
| `R.invalid` | corrupt persisted mode |

### O - Feature operation lifecycle

Every long-running operation should reuse the same vocabulary:

```text
O.idle
  -> O.starting
  -> O.running.phase-N
  -> O.completing
  -> O.idle

O.running.phase-N -> O.cancelling -> O.idle
O.running.phase-N -> O.failed     -> O.idle
```

Examples:

- subscription refresh;
- public-config search;
- Saved recheck;
- server deep verify;
- DPI strategy probe;
- helper download/update;
- VPN start;
- diagnostics export;
- app update.

Do not represent all of these with unrelated `bool IsBusy` flags if they need
different UI or cancellation semantics. Prefer a typed operation state with an
optional phase.

### D - Durable data state

| Code | State |
|---|---|
| `D.empty` | first launch or intentionally cleared |
| `D.one` | one valid record |
| `D.many` | representative populated state |
| `D.max` | configured or practical upper bound |
| `D.stale` | valid but older than freshness threshold |
| `D.partial` | some valid and some invalid records |
| `D.corrupt` | unreadable or structurally invalid storage |
| `D.legacy` | migration input from an older supported schema |
| `D.duplicate` | colliding or repeated records |

### E - External environment

| Code | State |
|---|---|
| `E.online-fast` | normal network |
| `E.online-slow` | response makes progress but is slow |
| `E.offline` | immediate network failure |
| `E.timeout` | external operation exceeds budget |
| `E.partial` | truncated body, partial write or partial source outage |
| `E.malformed` | syntactically valid transport, invalid payload |
| `E.permission-denied` | filesystem, VPN or OS action denied |
| `E.disk-full` | durable write fails |
| `E.process-missing` | required helper absent |
| `E.process-crash` | child process exits during operation |
| `E.conflict` | another VPN, service or process conflicts |

### I - Input class

| Code | State |
|---|---|
| `I.valid-min` | smallest valid input |
| `I.valid-normal` | common input |
| `I.valid-max` | largest supported input |
| `I.blank` | empty or whitespace |
| `I.boundary-minus` | immediately below accepted range |
| `I.boundary-plus` | immediately above accepted range |
| `I.malformed` | invalid format |
| `I.duplicate` | already present |
| `I.case-variant` | same semantic value with casing difference |
| `I.unicode` | non-ASCII display content |
| `I.legacy` | accepted old format |
| `I.adversarial` | oversized, deeply nested or amplification payload |

### L - Lifecycle interruption

| Code | State |
|---|---|
| `L.none` | operation runs normally |
| `L.double-click` | same action invoked twice rapidly |
| `L.navigate-away` | user changes tab during operation |
| `L.close-window` | desktop window closes or hides |
| `L.background` | Android app backgrounds |
| `L.process-kill` | app terminates unexpectedly |
| `L.relaunch` | app starts with prior interrupted state |
| `L.update-mid-state` | app is upgraded with existing durable data |
| `L.clock-shift` | wall clock changes across freshness logic |

## Scenario notation

Use a compact notation in plans, test names and release notes:

```text
<FEATURE>.<ACTION>
  [P=<platform>; V=<vpn>; C=<config>; O=<operation>;
   D=<data>; E=<environment>; I=<input>; L=<lifecycle>]
```

Example:

```text
FC.SEARCH.START
  [P=android-permitted; V=stopped; O=idle;
   D=stale; E=online-slow; L=background]
```

Example:

```text
APP.REMOVE.CATEGORY
  [P=win-admin; V=running; C=generated; R=exclude;
   D=many; L=none]
```

The notation is not intended to encode every implementation detail. Its job is
to make the tested boundary explicit and comparable between platforms.

## Global invariants

Every feature contract should reference the applicable global invariants.

### G1 - No silent traffic leak

- A configuration transition must not silently change intended route polarity.
- Missing proxy outbounds, invalid route rules and invalid DNS routing must
  fail closed or stop the start path.
- App/service ownership changes must not silently report VPN protection when
  traffic is direct.

### G2 - Preserve user-owned durable data

- A convenience action must not wipe unrelated user settings.
- Parse, fetch, apply and migration failures preserve the previous valid state.
- Destructive actions require deliberate user intent.

### G3 - UI state must tell the truth

- Enabled action means the operation is accepted.
- Disabled action is visibly and semantically disabled.
- Status labels distinguish running, cancelled, timed out, failed and stale.
- A displayed `Verified` state has one product meaning.

### G4 - Single-flight operations

- Repeated click does not start a second conflicting operation.
- Operations are either rejected, coalesced or queued by explicit policy.
- Cancellation belongs to the currently active operation only.

### G5 - Bounded work

- Network bodies, decoded bodies, files, lists, retries and process waits have
  explicit limits.
- Slow progress and total timeout are separate concerns.
- Cancellation has a measurable upper-bound latency.

### G6 - Atomic adoption

For any fetched, imported, migrated or generated replacement:

```text
read candidate
  -> validate candidate
  -> prepare replacement
  -> persist atomically
  -> switch active state
```

Do not erase the prior good state before the candidate is known to be usable.

### G7 - Safe restart

- Relaunch after interruption converges to a valid state.
- Orphan temp files and child processes are cleaned deliberately.
- The app never assumes that its prior shutdown completed.

### G8 - Capability honesty

- Platform-specific unsupported behavior is hidden, disabled or explained.
- A no-op stub must not look like a working safety feature.

### G9 - Secret hygiene

- Logs, diagnostics exports, temp files and error messages must not expose
  credentials.
- Redaction fails closed.

### G10 - Observability

- Every long-running action has an operation ID or enough structured context
  to reconstruct what happened.
- Logs identify phase, cancellation, fallback and recovery without secrets.

## Action contract template

Each user-visible function should get a compact contract sheet.

```markdown
## `<FEATURE>.<ACTION>` - `<short title>`

### User intent
What the user thinks this action does.

### Entry points
Desktop button, Android button, tray action, shell verb, startup hook, service.

### Supported envelope
- platform capabilities:
- VPN states:
- config modes:
- feature operation states:
- durable data states:

### Inputs and limits
- accepted formats:
- min/max:
- normalization:
- duplicate policy:

### Allowed transition
`before -> in progress phases -> success state`

### Forbidden transitions
What is disabled, rejected, coalesced or deferred.

### Durable effects
Which settings/cache files may change. Which must never change.

### Runtime effects
Processes, firewall, DNS, TUN, external URLs, Android service.

### Invariants
Applicable global invariants plus feature-specific invariants.

### Failure contract
Expected result for timeout, cancellation, malformed data, write failure,
permission failure, process crash and relaunch.

### Verification table
Scenario IDs mapped to unit, integration, UIA/ADB and release checks.

### Observability
Expected non-secret log events and user-facing status.
```

## UI action boundary template

For each button or interactive control, define its availability explicitly.

| Field | Meaning |
|---|---|
| Visible when | Capability and product-mode conditions |
| Enabled when | Exact state predicate |
| Rejected when | VM/Core guard predicate |
| During execution | Spinner, label, disabled neighbors, cancellation control |
| On success | New state, persisted effect, status |
| On failure | Restored state, status, logs |
| On cancel | Restored state, partial results policy |
| On relaunch | Recovery policy |

Each action should have three aligned guard layers:

```text
UI enabled predicate
  -> ViewModel command guard
  -> Core safety validator
```

The Core guard is mandatory for safety-sensitive behavior. UI gating improves
clarity but is not a security or correctness boundary.

## Edge-case derivation method

## Step 1 - Describe ordinary intent first

Before writing tests, state the normal action in one sentence.

Example:

```text
The user selects a deep-verified public config and asks VPNRouter to adopt it
as the active generated VLESS server and connect.
```

If that sentence is ambiguous, implementation and tests will also be
ambiguous.

## Step 2 - List only touched state dimensions

Do not cross every global dimension into every test.

For public-config apply, the relevant dimensions are:

- platform;
- VPN stopped/running/starting;
- selected config validity and verification state;
- existing Servers list;
- warning acknowledgement;
- persistence write result;
- VPN start result;
- repeated click and relaunch.

Theme, language and DPI are useful UI checks but do not belong in every Core
apply test.

## Step 3 - Partition input equivalence classes

For every free-form input, test semantic classes rather than arbitrary strings.

Example for server URI:

| Class | Representative |
|---|---|
| Valid common | ordinary VLESS Reality URI |
| Valid minimal | smallest accepted URI |
| Valid optional-rich | URI with all supported optional fields |
| Blank | whitespace |
| Malformed | broken URI escape or missing UUID |
| Unsupported | valid URI for unsupported protocol |
| Duplicate exact | same canonical config |
| Duplicate display-only | same endpoint, different label |
| Security variant | same endpoint and UUID, different key/SNI/short ID |
| Oversized | long query or adversarial payload |

## Step 4 - Split async actions into interruption phases

An async operation must be tested at phase boundaries, not only start and end.

Generic phases:

```text
before I/O
during download
after download before validate
during validate
after validate before persist
during persist
after persist before runtime switch
during child-process start
after runtime start before UI acknowledgement
```

For each relevant phase ask:

- what if the user cancels?
- what if the app closes?
- what if disk write fails?
- what if a process crashes?
- what if the network stops making progress?
- what state is visible after relaunch?

## Step 5 - Test adjacent actions

Bugs often appear between two individually correct functions.

For every action, identify:

- action immediately before it;
- action attempted during it;
- action immediately after success;
- action immediately after failure;
- action after app relaunch.

Examples:

- refresh subscription -> select server -> connect;
- remove app in Exclude mode -> apply pending changes -> reconnect;
- search public configs -> select weak row -> attempt connect;
- update helper -> connect helper -> remove helper;
- start VPN -> switch active server -> reconnect;
- import rules -> edit one rule -> export -> re-import.

## Step 6 - Add fault injection

Every function that crosses a boundary needs injected failure tests.

| Boundary | Inject |
|---|---|
| HTTP | timeout, slow stream, truncation, malformed body, redirect, oversized response |
| Filesystem | read denial, write denial, disk full, partial temp file, atomic-replace failure |
| Process | missing binary, non-zero exit, no output, malformed output, early crash, hang |
| OS capability | no admin, VPN permission denied, service missing, TUN conflict |
| Persisted state | empty, stale, corrupt, legacy, duplicate, unknown enum |
| Time | expired freshness, future timestamp, wall-clock shift |
| UI lifecycle | double-click, tab switch, window close, Android background, relaunch |

## Step 7 - Use risk-weighted combination coverage

Use different coverage depths for different risks.

| Risk | Required combination strategy |
|---|---|
| Traffic leak, credential leak, data loss, update brick | exhaustive small state matrix plus fault injection |
| Connection lifecycle, firewall, DNS, cache replacement | pairwise dimensions plus all phase interruptions |
| Ordinary CRUD and filters | equivalence partitions plus adjacent-action sequences |
| Visual layout | viewport, language, theme and representative state snapshots |
| Cosmetic text | localization key parity and one rendered pass |

Do not generate thousands of low-value combinations for a cosmetic toggle.
Do not use pairwise sampling as the only coverage for a silent-leak path.

## Step 8 - Convert every production bug into a contract

When a bug is fixed:

1. add the smallest failing regression;
2. identify the missing invariant or missing transition rule;
3. update the relevant feature contract;
4. add a release check if the bug depended on packaged runtime behavior;
5. reuse the same class of check in neighboring features.

Example:

```text
Bug: Android apply failure erased Servers.
Missing contract: convenience apply must preserve unrelated user-owned data.
Global invariant: G2 Preserve user-owned durable data.
Neighbor checks: config import, subscription refresh, migration, updater rollback.
```

## Verification layers

Each scenario belongs at the cheapest layer that can prove the behavior.

| Layer | Purpose | Examples |
|---|---|---|
| `L0` static contract | Catch missing guard, missing localization key, wrong command wiring | code-shape tests, parser guards |
| `L1` pure unit | Prove transformation and state decision | parser, merge, validator, dedupe, state transition |
| `L2` hermetic integration | Exercise I/O boundary with fake seams | slow HTTP stream, disk failure, process exit code |
| `L3` headless UI | Prove binding, visible/enabled state and layout assembly | AvaloniaFact, screenshot, visual diff |
| `L4` packaged local E2E | Prove real app, real binaries and click path | MCP UIA desktop, ADB Android |
| `L5` live canary | Prove external ecosystem assumptions | GitHub asset smoke, public source monitor, updater live gate |

### Layer selection examples

| Scenario | Cheapest sufficient layer |
|---|---|
| RU filter excludes cached Verified record | `L1` |
| Slow gzip fetch preserves local cache | `L2` |
| Apply CTA disabled for weak Android candidate | `L3` or Android characterization |
| Installed app can update from previous stable | `L4` |
| Rolling pool asset is fresh and decompression works | `L5` |

## Feature inventory and rollout priority

Apply the contract format incrementally.

### Tier A - Safety and data-preservation surface

| Feature ID | Surface | Why first |
|---|---|---|
| `VPN` | start, stop, reconnect, crash recovery, service ownership | silent leak and lifecycle risk |
| `CFG` | generated, subscribe and custom config adoption | determines actual routing |
| `APP` | Applications Include/Exclude/Full Tunnel | leak-from-intent risk |
| `RULE` | Network custom rules import/edit/apply/clear | route-policy mutation |
| `SUB` | subscriptions add/refresh/sync/remove | external input and active server mutation |
| `FC` | Public Configs search/recheck/apply | external input, cache, verification and connect |
| `DNSFW` | DNS lockdown and firewall | can leak or strand connectivity |
| `UPD` | update download/apply/rollback | can brick all users |

### Tier B - Process lifecycle and external tooling

| Feature ID | Surface |
|---|---|
| `ZAP` | DPI bypass install/update/probe/start/stop/remove |
| `TGP` | Telegram proxy install/start/stop/link handling |
| `WGT` | Emergency channel install/update/connect/disconnect/remove |
| `SVC` | Windows Service install/start/stop/reinstall |
| `DIAG` | health check and diagnostics export |

### Tier C - Product shell and lower-risk surfaces

| Feature ID | Surface |
|---|---|
| `UI` | Simple/Advanced mode, navigation, theme, language |
| `SHARE` | QR and config import/export |
| `ABOUT` | update check, about, links |

## Global interaction boundaries to standardize

## Boundary B1 - One primary mutation at a time

During VPN start, stop, reconnect or apply:

- block another VPN lifecycle mutation;
- block server/config mutation that would invalidate the in-flight request;
- allow navigation and read-only inspection;
- allow cancellation only where the runtime can converge safely.

During a Public Config search:

- allow tab navigation;
- allow Cancel;
- block a second search;
- block destructive Saved mutations unless explicitly serialized;
- allow apply only for an already-Verified row if applying does not corrupt the
  search owner state; otherwise disable until idle.

The product decision must be explicit per feature. A button that remains
enabled but silently returns is a contract violation under G3.

## Boundary B2 - Persisted state and runtime state are separate

For each settings mutation define:

| State | Expected behavior |
|---|---|
| VPN stopped | persist immediately; next start uses new value |
| VPN running and hot-reload safe | persist and hot-reload |
| VPN running and restart required | mark pending or restart deliberately |
| VPN starting/stopping | block or defer with visible status |
| Service-owned VPN | route change through service contract or explain limitation |

This boundary is especially important for Applications, custom rules, selected
server and subscription changes.

## Boundary B3 - Convenience data never owns user data

Define ownership:

| Data class | Examples | Replacement policy |
|---|---|---|
| User-owned | manual servers, manual categories, custom rules, custom JSON | preserve unless user explicitly deletes |
| Imported but user-adopted | subscription URL, selected public config added to Servers | preserve after adoption |
| Derived cache | public pool, GeoIP, probe results, updater temp files | replace or clear atomically |
| Runtime-only | PID, progress, temp verifier config | recreate and cleanup |

Cache recovery must never delete user-owned data to simplify derived-state
recovery.

## Boundary B4 - Connectable means one thing

Across desktop and Android:

```text
connectable
  = structurally valid
  + accepted by policy
  + final verification level required by that surface
  + not blocked by current operation state
```

If expert override exists, it must be a separate action with a separate label.

## Boundary B5 - Destructive actions are deliberate

Require confirmation or undo when the action destroys:

- user-owned configuration;
- many records;
- expensive verification history;
- operating-system state;
- an installed helper or service.

Single-row deletion can remain lightweight if the effect is visible and
recoverable.

## Boundary B6 - Every external replacement is last-known-good

Use candidate validation and atomic replacement for:

- public pool;
- subscriptions;
- updater archives;
- rule-set downloads;
- helper binaries;
- GeoIP data;
- config import;
- migrated settings.

## Boundary B7 - Platform parity is explicit, not assumed

For each contract mark:

| Value | Meaning |
|---|---|
| `same` | same semantics and UX expectation |
| `adapter` | same semantics, platform-specific implementation |
| `reduced` | intentionally smaller capability with visible explanation |
| `unsupported` | feature unavailable on platform |

This prevents Android, macOS and Linux from accidentally drifting into silent
partial implementations.

## Filled example - Public Configs

## FC state model

### Page state

```text
FC.page.closed
FC.page.search
FC.page.saved
```

### Search operation

```text
FC.search.idle
  -> FC.search.fetching-pool
  -> FC.search.validating-pool
  -> FC.search.filtering
  -> FC.search.fast-probing
  -> FC.search.deep-verifying
  -> FC.search.saving
  -> FC.search.done
  -> FC.search.idle
```

Failure and cancel transitions:

```text
any running phase -> FC.search.cancelling -> FC.search.idle
any running phase -> FC.search.failed     -> FC.search.idle
```

### Candidate trust state

```text
FC.candidate.discovered
  -> FC.candidate.parsed
  -> FC.candidate.fast-ok
  -> FC.candidate.verified

FC.candidate.* -> FC.candidate.rejected
```

Only `FC.candidate.verified` is normal-connectable.

### Saved entry state

```text
FC.saved.fresh
FC.saved.stale
FC.saved.failed-last-check
FC.saved.policy-excluded
FC.saved.malformed-quarantined
```

The historical row may remain visible after a failed recheck, but it must not
pretend that the latest attempt succeeded.

## FC action catalog

| Action ID | User intent |
|---|---|
| `FC.SEARCH.START` | find working public configs |
| `FC.SEARCH.CANCEL` | stop current search without damaging prior Saved state |
| `FC.TAB.SEARCH` | inspect new search controls/results |
| `FC.TAB.SAVED` | inspect previously saved verified configs |
| `FC.SAVED.RECHECK.ONE` | verify one saved config again |
| `FC.SAVED.RECHECK.STALE` | recheck stale or failed-last-check configs |
| `FC.SAVED.REMOVE.ONE` | forget one saved config |
| `FC.SAVED.CLEAR.ALL` | intentionally forget all saved verification history |
| `FC.APPLY.SELECTED` | adopt selected Verified config and connect |
| `FC.FILTER.RU` | exclude known-RU candidates |
| `FC.SET.TARGET` | set requested Verified result count |
| `FC.SET.MAXPING` | set precheck latency threshold |

## FC action boundaries

### FC.SEARCH.START

| Field | Contract |
|---|---|
| Visible when | Public Configs page is available |
| Enabled when | `FC.search.idle` |
| Rejected when | another search, recheck or destructive cache mutation owns the operation |
| Inputs | target and max ping inside bounded numeric range; RU policy |
| Durable effect | add or refresh Verified Saved records; preserve prior Saved records on failure |
| Runtime effect | network fetch, parse, probe and transient verifier processes/libbox |
| Cancel | preserve prior Saved state; keep already-finished Verified results only if policy says partial success is durable |
| Relaunch | recover Saved cache; clean verifier temp artifacts; no stuck busy state |
| Invariants | G2, G3, G4, G5, G6, G7, G9, G10 |

Required edge cases:

- [ ] empty cache, fresh compressed pool, normal success;
- [ ] stale local pool, online success;
- [ ] stale local pool, offline fallback;
- [ ] corrupt gzip with valid old cache;
- [ ] slow gzip stream with progress;
- [ ] cancel in each phase;
- [ ] repeated Start click;
- [ ] switch tab during deep verify;
- [ ] close/relaunch after verifier process starts;
- [ ] pool contains duplicates, malformed entries and security variants;
- [ ] no candidate reaches Verified;
- [ ] some weak candidates pass fast probe but fail deep verify;
- [ ] target is lower bound, upper bound and invalid persisted value;
- [ ] max ping is lower bound, upper bound and invalid persisted value;
- [ ] Android background during deep verify;
- [ ] VPN already running: obey explicit product policy.

### FC.SEARCH.CANCEL

| Field | Contract |
|---|---|
| Visible when | search or recheck is running |
| Enabled when | active operation supports cooperative cancellation |
| Rejected when | idle |
| Durable effect | no deletion; optional save of already-final Verified results only |
| Runtime effect | stop new work; terminate owned temp verifier safely |
| User status | distinguish cancelled from failed and timed out |
| Relaunch | no stale busy state |

Required edge cases:

- [ ] cancel before network response;
- [ ] cancel during stream;
- [ ] cancel between fast probe and deep verify;
- [ ] cancel during desktop sing-box verification;
- [ ] cancel during Android primary and fallback probe;
- [ ] cancel twice;
- [ ] navigate away immediately after cancel;
- [ ] app process terminates before cleanup completes.

### FC.SAVED.RECHECK.ONE

| Field | Contract |
|---|---|
| Enabled when | Saved row exists, operation idle |
| Input | one historical Verified row |
| Success | refresh current successful checkpoint |
| Failure | keep prior usable row, mark failed-last-check, do not report fresh success |
| Cancel | restore prior state without marking failure |
| Invariants | G2, G3, G4, G10 |

Required edge cases:

- [ ] fresh success;
- [ ] TCP failure;
- [ ] bind failure;
- [ ] verifier spawn failure;
- [ ] HTTP failure;
- [ ] timeout;
- [ ] user cancel;
- [ ] row removed by adjacent action;
- [ ] app close and relaunch.

### FC.APPLY.SELECTED

| Field | Contract |
|---|---|
| Visible when | row is selected |
| Enabled when | selected row is final Verified and apply owner is idle |
| Rejected when | weak, stale-policy-blocked, malformed, no selection or another lifecycle mutation is running |
| Durable effect | add/update adopted server and set generated/manual mode without deleting unrelated servers |
| Runtime effect | stop prior VPN if required; start selected server through guarded lifecycle |
| Warning | one-time public-proxy risk acknowledgement on both desktop and Android |
| Failure | preserve existing server list and surface actionable status |
| Invariants | G1, G2, G3, G4, G6, G9, G10 |

Required edge cases:

- [ ] selected Verified config with VPN stopped;
- [ ] selected Verified config while VPN running;
- [ ] selected weak candidate;
- [ ] selected stale/failed-last-check row;
- [ ] malformed raw URI;
- [ ] exact duplicate already in Servers;
- [ ] same endpoint and UUID but different Reality security fields;
- [ ] server-list persist failure;
- [ ] VPN phase-A start timeout;
- [ ] VPN phase-B warm-up timeout;
- [ ] conflicting VPN;
- [ ] public warning cancel and proceed;
- [ ] double-click Apply;
- [ ] app closes between persistence and runtime start.

### FC.SAVED.CLEAR.ALL

| Field | Contract |
|---|---|
| Enabled when | Saved list non-empty and no conflicting operation |
| User intent | delete derived verification history only |
| Confirmation | required |
| Must not change | manual Servers list, active VPN config, custom sources unless explicitly included |
| Relaunch | Saved remains empty; unrelated state intact |
| Invariants | G2, G3, G5 |

## FC scenario matrix

This matrix is intentionally compact. It is a seed for tests, not a demand to
execute every theoretical combination.

| ID | Action | Boundary tuple | Expected |
|---|---|---|---|
| `FC-001` | Search | `D.empty + E.online-fast + L.none` | target Verified rows saved |
| `FC-002` | Search | `D.stale + E.offline` | stale/fallback state explained; old Saved preserved |
| `FC-003` | Search | `D.one-good-cache + E.malformed` | corrupt candidate pool rejected; old pool retained |
| `FC-004` | Search | `E.online-slow + L.cancel@download` | prompt cancellation; cache intact |
| `FC-005` | Search | `L.double-click` | one operation owner only |
| `FC-006` | Search | `P.android + weak candidates` | weak rows not connectable; scan continues to Verified target |
| `FC-007` | Recheck | `prior Verified + E.process-missing` | prior row kept with failed-last-check |
| `FC-008` | Recheck | `prior Verified + L.cancel@deep` | prior row restored; no false failure |
| `FC-009` | Apply | `Verified + Servers many + E.disk-full` | Servers list unchanged |
| `FC-010` | Apply | `weak Ok` | rejected on desktop and Android |
| `FC-011` | Apply | `VPN.running + phase-A timeout` | prior runtime converges safely; status truthful |
| `FC-012` | Clear all | `Saved many + confirm=no` | no mutation |
| `FC-013` | Filter RU | `cached Verified RU` | absent from eligible search/connect result |
| `FC-014` | Search | `Android + L.background@deep` | bounded stop or resume policy; no stuck busy |
| `FC-015` | Relaunch | `L.process-kill@verifier` | temp artifacts cleaned; no stuck busy |

## Public Configs product decisions — RESOLVED 2026-06-02

Decided by Claude under the user's "прими решения" directive, grounded in the
current code (read-only sweep) + the global invariants. Each row: the decision,
the current behavior, and whether code work is needed (those land in Phase 2 /
`v2.40.0-r1`, NOT during the v2.39-r7 soak).

1. **Search while main VPN connected?** → **ALLOWED.** Verifier is SOCKS-isolated
   (see #2), so finding a better config while connected is safe + useful.
   *Current:* ungated both platforms. *Action:* confirm + document. No code.

2. **Verifier traffic isolation?** → Verifier runs sing-box/libbox with a
   **SOCKS inbound only (no TUN of its own)**. In **split tunnel** the verifier
   process is NOT in the routed app-list, so its probe goes **direct** (correct).
   In **full tunnel** the active TUN captures the verifier's outbound, so
   deep-verify then measures "reachable *through the current VPN*", not direct.
   *Decision:* accept + document this; deep-verify-while-full-tunnel is a
   reasonable "does it work" proxy. *Action (backlog, P3):* optionally exclude
   the verifier process from the TUN for true direct measurement.

3. **Partial Verified saved after Cancel?** → **YES**, keep. Deep-verify is
   expensive (~3-5 s each); discarding confirmed-Verified on cancel wastes it.
   Only Verified is persisted (no weak candidates — #148). *Current:* saves on
   cancel both platforms. *Action:* confirm. No code.

4. **Stale / failed-last-check row connect via expert override?** → **NO
   override. Connectable ⇔ Status == Verified, uniformly (B4).** Staleness and
   the failed-last-check marker are advisory (badge/sort/recheck-prompt), NOT a
   connect block — a once-Verified row may be applied; connecting is itself the
   ultimate test. *Current:* Android Apply IS Verified-gated (#148
   `ApplyFcConnectGate` + click backstop); **desktop `ApplyFreeConfigAsync` is
   NOT status-gated** (theoretical gap — Saved is Verified-only by retention, so
   low-risk). *Action (Phase 2, P1):* add a defensive Verified-gate to desktop
   Apply for B4 parity.

5. **Apply while a search runs?** → **BLOCK** (B1 — one primary mutation at a
   time; Apply stops+starts the main VPN, racing the verifier processes / TUN
   lock). *Current:* allowed both platforms (Apply not gated by the busy flag).
   *Action (Phase 2, P1):* gate Apply on operation-idle, both platforms.

6. **Closing the page cancels search?** → **YES, cancel** on page-close /
   tab-switch / Android-background (battery; no stuck-busy; FC-014). *Current:*
   desktop cancels on `Dispose`; Android cancels via
   `StopFreeConfigsBackgroundWork` on shell-close/tab-switch. *Action (Phase 2,
   P2):* verify + wire an Android `OnPause` → cancel hook if missing.

7. **Max target count + max-ping range?** → **target ∈ [1, 50], maxPing ∈
   [50, 2000] ms — both platforms** (G5 bounded work; >50 Verified deep-verifies
   ~forever). *Current:* Android clamps in the click handler (#148); **desktop
   has no clamp** (defaults 10/400 only). *Action (Phase 2, P2):* add desktop
   clamps matching Android.

8. **Stale-pool threshold before fallback/warning?** → **No hard block.** The
   deep-verify validates every candidate live, so a stale pool only means an
   older candidate *list* — staleness ≠ stale verification. On fetch failure,
   use the local cache at any age (better candidates than none). *Action
   (optional, P3):* a soft "pool may be outdated" hint if cache > 7 days AND the
   fetch failed. *Current:* no threshold; ETag-conditional fetch + cache
   fallback. No required code.

9. **Custom sources combined or separate mode?** → **COMBINED** (additive to the
   14 built-in), as desktop does today. Android is **pool-only by design** — it
   consumes the server-aggregated `pool.json` for mobile efficiency; this is an
   intentional `reduced` parity (B7), documented, not a bug. *Action:* confirm +
   document Android `reduced`. No code.

### Resulting Phase 2 code work (queued for v2.40.0-r1, post-soak)

| Item | Decision | Invariant | Priority |
|---|---|---|---|
| Block Apply during an active search/recheck (both) | #5 | B1, G4 | P1 |
| Defensive Verified-gate on desktop `ApplyFreeConfigAsync` | #4 | B4, G3 | P1 |
| Desktop target/maxPing clamps `[1,50]` / `[50,2000]` | #7 | G5 | P2 |
| Android `OnPause` → cancel search (if not already wired) | #6 | G4, G7 | P2 |
| Verifier-excluded-from-TUN (direct deep-verify in full tunnel) | #2 | G10 | P3 |
| Soft "outdated pool" hint (> 7 days + fetch failed) | #8 | G3 | P3 |

Each ships with the cheapest sufficient test (per the layer table): P1/P2 items
get L1/L3 (command-guard + headless enabled-state) regression tests; #2 is L2.

## Filled mini-example - Applications routing

Applications is a useful second example because most risks occur between UI
state and generated routing policy.

## APP state model

```text
APP.mode.include
APP.mode.exclude
APP.mode.full

APP.runtime.vpn-stopped
APP.runtime.vpn-running-clean
APP.runtime.vpn-running-pending
APP.runtime.applying
```

### APP action catalog

| Action ID | User intent |
|---|---|
| `APP.MODE.SET` | choose Include, Exclude or Full Tunnel |
| `APP.PRESET.TOGGLE` | enable or disable prepared application category |
| `APP.CUSTOM.ADD` | add one process |
| `APP.CUSTOM.REMOVE` | remove one process |
| `APP.CATEGORY.ADD` | add custom category |
| `APP.CATEGORY.REMOVE` | remove custom category and its routing effects |
| `APP.APPLY` | make pending routing changes active |
| `APP.SHELL.ADD/REMOVE` | mutate routing list through Explorer context menu |

### APP invariants

- A visible removed app has no hidden routing rule.
- Include and Exclude lists remain independent.
- Switching mode never silently reinterprets the other list.
- sing-box `process_name` casing is preserved.
- Manual input, executable picker and shell verb use one normalization policy.
- Prepared presets retain scanner patterns and child-process expansion.
- If VPN is running, UI truthfully distinguishes persisted changes from active
  runtime changes.

### APP seed scenarios

| ID | Action | Boundary tuple | Expected |
|---|---|---|---|
| `APP-001` | Remove app | `R.include + V.running` | hidden proxy route removed after Apply |
| `APP-002` | Remove app | `R.exclude + V.running` | hidden direct-route exception removed after Apply |
| `APP-003` | Remove category | `R.exclude + selected children` | all child effects scrubbed |
| `APP-004` | Add manual | `I.valid-normal=Discord.exe` | preserved executable basename |
| `APP-005` | Add manual | `I.valid-normal=full path` | normalized to basename |
| `APP-006` | Add manual | `I.blank / malformed / folder` | rejected with actionable feedback |
| `APP-007` | Toggle preset | `scan-pattern-only child launches later` | generated config receives runtime-expanded process |
| `APP-008` | Mode switch | `include selections + exclude selections` | each mode restores its own list |
| `APP-009` | Shell remove | `case variant` | matching entry scrubbed without lowercasing stored name |
| `APP-010` | Apply | `V.starting` | blocked or deferred explicitly |

## Suggested implementation artifacts

Do not start by adding a large custom test framework. Start with living
documents and small reusable helpers.

### Phase 1 - Markdown contract registry

Create one file per Tier A feature:

```text
plans/interaction-contracts/
  VPN-vpn-lifecycle.md
  CFG-config-adoption.md
  APP-applications-routing.md
  RULE-custom-rules.md
  SUB-subscriptions.md
  FC-public-configs.md
  DNSFW-firewall-dns.md
  UPD-updater.md
```

Each file uses the action contract template and scenario notation from this
document.

### Phase 2 - Scenario manifest

When the Markdown shape stabilizes, add a small machine-readable manifest:

```yaml
feature: FC
action: APPLY.SELECTED
scenario: FC-009
risk: data-loss
dimensions:
  platform: android
  vpn: stopped
  data: servers-many
  environment: disk-full
expect:
  server_list: unchanged
  selected_server: unchanged
  user_status: apply-failed-preserved
layers: [L1, L2]
```

The manifest can initially be documentation only. Later it can drive test
filters, release checklists and coverage reports.

### Phase 3 - Coverage report

Generate a simple report:

| Scenario | Contract exists | Automated layer | Desktop E2E | Android E2E | Last checked |
|---|---|---|---|---|---|
| `FC-009` | yes | `L2` | n/a | planned | 2026-06-02 |
| `APP-002` | yes | `L1` | verified | n/a | 2026-06-01 |

This answers a practical question better than raw test count:

> Which user promises are pinned, and at what layer?

## Review checklist for every new feature

Before implementation:

- [ ] Write the one-sentence user intent.
- [ ] Choose feature ID and action IDs.
- [ ] List touched state dimensions only.
- [ ] Define supported, handled-invalid, forbidden-concurrent and unsupported
  interactions.
- [ ] State durable data ownership.
- [ ] Define success, failure, cancel and relaunch behavior.
- [ ] Set size, count, retry and timeout bounds.
- [ ] Mark desktop/Android/platform parity.
- [ ] Reference applicable global invariants.

During implementation:

- [ ] Align UI enabled predicate, ViewModel guard and Core validator.
- [ ] Use typed state/result instead of loosely related booleans where behavior
  differs.
- [ ] Keep external replacements last-known-good and atomic.
- [ ] Add structured non-secret logs.
- [ ] Add the cheapest sufficient tests first.
- [ ] Add packaged E2E when behavior depends on binaries, OS state or update
  scripts.

Before release:

- [ ] Run automated regression scenarios for touched contracts.
- [ ] Run adjacent-action sequences.
- [ ] Run cancellation at each changed async phase.
- [ ] Verify narrow desktop window, RU/EN and representative Android viewport
  for changed pages.
- [ ] Run MCP UIA or ADB end-to-end through the final user-visible effect.
- [ ] Scan logs and diagnostics for errors and secret leakage.
- [ ] Record scenario IDs in release notes or verification report.

## Recommended next pass

Create Tier A contracts in this order:

1. `VPN` lifecycle and ownership.
2. `CFG` config adoption across generated, subscribe and custom modes.
3. `APP` Include/Exclude/Full Tunnel routing mutations.
4. `FC` Public Configs search, recheck and apply.
5. `SUB` subscription refresh and active-server adoption.
6. `RULE` custom-rules import, edit, apply and clear.
7. `DNSFW` firewall and DNS-lockdown lifecycle.
8. `UPD` previous-stable to candidate update and rollback.

Public Configs and Applications already have audit material, so their contract
files can be extracted from existing plans with relatively little discovery.

## Definition of success

The framework is working when:

- each high-risk user action has a documented supported envelope;
- forbidden concurrent actions are visibly and programmatically guarded;
- safety invariants map to tests, not only comments;
- bug fixes update contracts as well as regressions;
- release verification names scenario IDs instead of saying only "page opens";
- platform differences are deliberate and visible;
- test expansion follows risk and state transitions rather than raw click count;
- a maintainer can answer what happens after success, failure, cancel and
  relaunch without reading the entire implementation.

## Cross-references

- `plans/public-configs-pipeline-audit-and-hardening-plan-2026-06-02.md`
- `plans/applications-page-audit-2026-06-01.md`
- `plans/critical-audit-targets.md`
- `plans/test-coverage-audit-2026-05-17.md`
- `plans/vpnrouter-parity-audit-plan.md`
- `VPNRouter.Tests/CLAUDE.md`
