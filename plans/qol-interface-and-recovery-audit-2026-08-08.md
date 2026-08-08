# VPNRouter QoL, recovery and interface audit — 2026-08-08

## Decision

The published Windows build `v2.48.0-r8` kept the tested session stable, and the
new setup/diagnostics wizard works on WINBRAT. After the independent GPT Pro
review and the owner's clarification that intermittent connect failure and game
disconnects are the real priority, do **not** implement this audit as one broad
QoL batch. The focused decision is:

1. **Fix now:** Custom JSON peeking must not make Simple Connect skip Smart
   Connect. This is a confirmed connection-path defect, not only display drift.
2. **Prove then fix if reproduced:** headless-test EN<->RU with a non-General
   Connection Intent; reject `SelectedIndex=-1` so Gaming cannot silently fall
   back to General.
3. **Measure before design:** capture one real game disconnect with active
   protocol, HTTP/core health, UDP-path health and the failover pick. Do not
   automatically switch to AWG until that evidence assigns ownership.

Public copy, Deep Verify presentation, URL-row masking, effective RU-bypass
status and MTU autosave remain valid findings, but they are deliberately outside
the connectivity-first patch. The test subscription link found in historical
plans still requires owner-side rotation.

Do not add speculative auto-MTU, a second “repair everything” button, coordinate
UI automation, or automatic firewall/killswitch changes. MTU reset to the
canonical `1420` and reversible safe-settings restore already exist in the
wizard.

## 1. Scope, safety and method

### Runtime target

- Published build: `v2.48.0-r8`.
- Fixed remote target: `WINBRAT` at `100.115.182.0`; identity reverified before
  UI actions.
- App installation, launch, screenshots, UIA and logs remained remote-only via
  `tools/brat-verify.ps1`. No VPNRouter process was launched on the dev box.
- The already configured test subscription was exercised through the released
  app. Raw subscription bodies, UUIDs, public keys, tokens and local user config
  files were not read or passed to Qwen.
- Screenshots are ignored evidence under
  `artifacts/brat-verify/20260808-r8-qol-audit/`; they are not committed because
  some UI states contain a subscription identifier. The exact evidence path is
  covered by the existing `/artifacts/brat-verify/` ignore rule and was
  rechecked with `git check-ignore`.
- The same live credential-like URL was discovered in historical tracked plans.
  Current-tree occurrences were replaced with a neutral placeholder. Rotation
  is still required because an ordinary commit cannot remove prior Git history.

### Review layers

1. Static inventory of every production Avalonia page, navigation state,
   recovery command and relevant ViewModel transition.
2. Live semantic UIA walkthrough of the published binary in EN and RU at the
   normal 520x640 window size.
3. Quick TCP/TLS and Deep verify runs against the configured subscription.
4. Remote log scan after the complete walkthrough.
5. Independent read-only Qwen review with exact model
   `qwen3.8-max-preview`, Qwen Code `0.21.7`, noninteractive `-p`, safe/plan
   mode, chat recording disabled, all read/write/shell/web/agent tools
   excluded, and `--max-tool-calls 0`. Codex validated or rejected each result.
6. Cross-check against the existing wizard plan, MTU audit, May UX audit,
   open-defect ledger and refactor backlog.

### Qwen corrections made by Codex

Qwen was useful as an adversarial reviewer, but three claims were rejected:

- it ranked `Full Tunnel` plus explicit Russian direct bypass as P1; this is a
  P2 summary/copy defect because the exception is visible and user-controlled,
  not an observed leak or routing failure;
- it treated Zapret and Telegram one-click actions as duplicate state machines;
  the visible shortcuts navigate to/reuse the same feature paths, so duplication
  was not established;
- it implied the new `Select` and `ScrollIntoView` verifier operations solve
  RadioButton, Expander and virtualized-list traversal. They do not; those gaps
  remain fail-closed and measurement-gated.

## 2. What was verified on WINBRAT

### Wizard

The released wizard was exercised before and during this audit:

- opens from the troubleshooting menu;
- renders correctly in EN and RU;
- runs read-only health checks;
- resets only MTU to `1420` while preserving routing;
- restores safe routing plus MTU only after explicit action;
- supports undo;
- exports redacted diagnostics;
- explicitly distinguishes persistent repair from temporary Safe Mode;
- does not change firewall rules or pretend to simulate a VPN crash.

The published-run health result was one warning and zero errors. The warning is
the already documented fixed-target IPv4 DF-ping limitation, not a new tunnel
failure.

### Remote verifier improvements

Two minimal native UIA operations were added to the remote-only verifier:

- `Select`: `SelectionItemPattern.Select()` plus a selected-state postcondition;
- `ScrollIntoView`: `ScrollItemPattern.ScrollIntoView()` for an already
  materialized semantic descendant.

They enabled deterministic navigation through the top-level pages, nested
Settings sections, Tools sections and the bottom Autostart control. Both fail
closed when Avalonia exposes no usable pattern. A proposed virtualized-list
page-scroll operation was tested against `AutomationId=SubList`, failed because
Avalonia reported `VerticallyScrollable=false`, and was removed rather than
leaving a misleading tool or adding coordinates.

### Page-by-page result

| Surface | Live result | Existing one-click/recovery path | Finding or next assertion |
|---|---|---|---|
| Simple EN/RU | PASS layout; status, config, routing, autostart and Connect fit | Connect, Autostart jump, Advanced, troubleshooting menu | Effective summary must name direct exceptions; sensitive subscription input is expected only in its edit surface |
| Servers / manual | PASS; orphan row clearly says `Not in subscription` | Test all, Deep verify, add/remove | Existing orphan explanation is good; no new pipeline |
| Servers / Custom JSON | PASS empty-state copy | Add Config | Selecting an empty Custom tab causes the confirmed presentation-mode drift |
| Subscribe | 20 rows processed; visible rows have protocol/use-case and health text | Test all, Deep verify, refresh all, per-sub refresh | Mask repeated row URL; final Deep summary needs outcome counts; locale switch can blank intent choice |
| Settings / Routing | PASS controls | direct edits; wizard for guided restore | `All traffic` copy contradicts the enabled Russian direct exception |
| Settings / Rules | PASS top and lower semantic content | import/export, cards/read/edit | Existing help card is useful; do not duplicate it in the wizard |
| Settings / Leak Protection | PASS controls | MTU ping helper; wizard reset/restore | Existing disconnected MTU autosave defect remains open; no auto-MTU |
| Settings / Content | PASS | one ad/tracker toggle | No new QoL surface justified |
| Settings / Updates | PASS | check updates, export diagnostics | Redaction explanation is clear and should remain an assertion |
| Settings / Autostart | PASS top and bottom | install service, component toggles, UI login | Disabled controls explain prerequisite and offer Install service; good pattern |
| Applications | PASS in Full Tunnel | one button to switch to split | Banner clearly says selection is ignored; no new wizard step needed |
| Tools / Zapret | PASS default page | Enable DPI bypass; selected strategy Run | Inner RadioButtons remain unreliable through Windows UIA; static/headless coverage stays primary |
| Tools / Telegram | PASS default page | Start and open Telegram | Clear first-run action; no second pipeline |
| Tools / Emergency Channel | PASS not-installed state | Install small component; Details | No evidence for automatic failover setup in this QoL cycle |
| Public / Search | PASS layout, FAIL instruction consistency | Find working configs | Empty state incorrectly says `Refresh`; distinct local/shell Connect copy remains a measurement-gated candidate |
| Public / Saved | PASS empty state | Search link via tab | Clear and minimal |
| Setup wizard | PASS released binary and RU layout | checks, MTU reset, safe restore, undo, export | Keep as the only guided recovery flow |
| About | Static/headless review PASS | menu About entry | Modal could not be opened reliably by the current compound-flyout UIA selector; no user-visible failure observed |

## 3. Subscription and protocol evidence

The configured subscription produced 20 rows.

- Quick TCP/TLS completed with responses from 15 of 20. The UI correctly says
  this is not a full protocol check.
- Deep verify processed 20 of 20 rows. The visible rows included
  VLESS+Reality/TCP, Naive and Hysteria2 and showed per-row `Works via VPN`
  verdicts where successful.
- The final remote log scan found three VLESS/WS verifier startup failures with
  `invalid public_key`. This repeats the measurement-gated finding from the MTU
  audit. It does not establish whether the subscription rows are invalid, the
  verifier transforms them incorrectly, or aggregated selected-family traffic
  succeeds through a sibling outbound.
- The label `Deep verify subscription: 20 / 20` means “processed 20,” not
  “passed 20.” Codex initially read it as success, which is direct evidence that
  the result summary needs passed/failed/untested counts.
- Lower virtualized rows could not be semantically scrolled into view because
  Avalonia exposes the list but reports no vertical ScrollPattern. Therefore
  this audit does not claim per-protocol success for hidden TUIC/Shadowsocks or
  other rows. Raw subscription data was intentionally not read to fill that gap.

Disposition:

- product/server ownership of the three WS failures remains measurement-gated;
- result-summary ambiguity is a confirmed P2 copy/state defect;
- quick-test timeout or threshold tuning is not justified from one 15/20 run;
- a secret-free WS fixture plus released lx `check` remains the correct next
  technical experiment.

## 4. Confirmed product findings

### Repository hygiene — credential-like test URL — P1, current tree resolved

Historical plans and captured evidence had copied the live test subscription
URL verbatim. This draft replaces every current-tree occurrence of that exact
identifier with `<redacted-test-subscription-url>`. Runtime URL logging was
already redacted. The owner must rotate or revoke the link because the old value
remains recoverable from repository history; history rewriting is deliberately
outside this audit.

### QOL-1 — Custom JSON browsing disables Simple Smart Connect — P1

Reproduction:

1. start in Subscribe mode with an enabled subscription and no custom JSON;
2. open Advanced -> Servers -> Custom Config (JSON);
3. return to Simple;
4. observe `custom · full` while the subscription URL remains in the input;
5. revisit Subscribe and return to Simple; the display becomes
   `subscribe · full` again.

Root cause:

- `OnSelectedServerModeIndexChanged` sets `IsVlessMode = value == 0` and saves;
- the existing persistence guard correctly keeps `ConfigMode=subscribe` when an
  active subscription exists;
- `SimpleConfigModeSummary` reads the stale presentation flags instead of the
  persisted/effective mode.

The independent review found a behavioral impact missed in the first pass:

- `SmpToggleConnectAsync` gates the complete protocol-aware
  `ServerHealthProbe` + `ConnectionIntentScorer` path on `IsSubscribeMode`;
- after the peek both transient flags are false, so Simple Connect dials the
  persisted active row without checking whether it is alive;
- `MaybeRefreshAutoSelectedAsync` also stops updating the runtime urltest label;
- the resolver still reads persisted `ConfigMode=subscribe`, so the generated
  tunnel remains a subscription tunnel. The defect is degraded server selection,
  not a wrong config mode or proven routing leak.

Smallest safe design: preserve the Servers-page peek flags, because they drive
that page's visible sub-tab. Extract one private effective-mode decision from
the existing `SaveSettings` guard and reuse it for the Simple summary, Smart
Connect gate and connected auto-selected label. Do not mutate the view flags
inside `SaveSettings`, do not touch the resolver, and pin the whole transition
through Connect rather than asserting only the label.

### QOL-2 — “All traffic” omits active direct exceptions — P2

Full Tunnel is selected while `Russian traffic via real IP` is enabled. The
behavior is intentional, but “All OS traffic through VPN” is false as an
effective summary. Display a compact exception, for example:

> Full tunnel · Russian destinations direct

Only the Simple current-state summary needs this suffix. Routing already shows
the RU-bypass card beside Full Tunnel, and its radio subtitle describes the
meaning of the option rather than claiming the complete effective state. Build
the suffix from effective geo availability, not merely the checked toggle.

### QOL-3 — Public empty state names a missing action — P2

Two live strings conflict with `Find working configs`: `FcStatusEmpty` names
`Refresh`, and `FcStatusNoDeepCandidates` names `Refresh list`. Change both to
the actual action. Do not add a second button. `FcRefreshSources` is not rendered
by desktop and remains a separate Android-aware cleanup question.

### QOL-4 — Subscription row repeats a sensitive opaque URL — P2

The Advanced subscription row exposes most of a bearer-like identifier in a
screen-share-friendly list even though Simple already has the edit surface.
Render only origin plus ellipsis in the row; never modify/canonicalize the
stored URL and never write it to diagnostics.

### QOL-5 — Locale switch can blank the connection-intent choice — P2

Changing EN -> RU replaces `ConnectionIntentChoices`; the ComboBox loses its
visible selection while `ConnectionIntentStatusText` falls to General. Since
the two-way index handler maps every unknown value, including `-1`, to General
and saves immediately, it may also erase a Gaming choice that prefers AWG/HY2.
The blank UI is live-confirmed; the disk write remains measurement-gated. First
pin both locale directions in a headless binding test. A negative-index guard is
safe; rebuild/restore logic is needed only if the test proves the guard alone is
insufficient.

### QOL-6 — Deep verify final state is overwritten by queued progress — P2

The first audit wording was incomplete: a final `Done. Verified: N / total`
summary already exists. The progress callback is already a `Progress<T>` created
on the UI command path, but posts a second `Dispatcher.UIThread.Post`. The last
callback can therefore enqueue its inner `processed / total` update after the
awaited continuation writes `Done`, reproducing the WINBRAT `20 / 20` final
screen and the earlier MTU-audit `19 / 19` observation.

Remove the redundant UI post first. After that ordering fix, the final copy may
be expanded from existing row verdicts to:

> Processed 20 · working N · protocol failed M · untested K

Exact buckets must come from existing `IsDeepVerified`, `IsDeepFailed` and
`IsDeepInconclusive` verdicts. Do not add state or change protocol thresholds.
This is not part of the connectivity-first patch.

### QOL-7 — Volatile test text stays in the previous language — P3

Quick/deep progress strings are materialized state. After locale change, clear
the completed volatile message or recompute it from stored counters. Do not
rerun network tests automatically.

### Existing MTU persistence defect — unchanged

Leak Protection still says Auto-saved while a disconnected manual MTU edit does
not invoke `SaveSettings`. It is already recorded in
`plans/OPEN-DEFECTS.md` and `plans/mtu-end-to-end-audit-2026-08-03.md`.
The wizard's explicit MTU reset persists correctly, but it does not make the
manual-field contract defect disappear.

## 5. QoL design: what already exists and what not to add

| User goal | Existing action | Persisted effect | Decision |
|---|---|---|---|
| “Why does VPN not work?” | Wizard checks / Health Check | none | Wizard is canonical; keep Health Check as expert shortcut |
| “My MTU is nonsense” | Wizard `Reset MTU only` | MTU -> 1420 | Done; no auto-MTU |
| “Restore safe network settings” | Wizard restore + undo | selected routing + MTU 1420 | Done; do not include firewall, subscriptions or app lists |
| “Start without risky stored settings” | Restart in Safe Mode | temporary launch only | Keep separate; its semantics differ from repair |
| “Collect support evidence” | Export diagnostics | redacted ZIP only | Done; keep redaction copy visible |
| “Refresh my subscription” | Refresh all / per-sub refresh | subscription cache | Done; no new one-click wrapper |
| “Choose a usable server” | Smart Connect, quick test, Deep verify, optional auto-select | selected/health state | Fix the confirmed Smart Connect skip first; do not tune from one sample |
| “Find a free config” | Find working configs | public cache/selection | Done; fix stale Refresh wording |
| “Fix DPI / Telegram” | one-click Zapret / Start and open Telegram | feature-specific | Done; these reuse existing surfaces |
| “Understand what is routed” | pieces across Simple/Routing/Apps/Rules | none | Defer broad projection; later add only a truthful Simple suffix if still useful |

The useful immediate concept is not another button or a new routing layer. It is
one reliable answer inside the already-visible Simple line:

```text
subscription · full · RU direct (only when geo data is effective)
```

This copy remains deferred from the connectivity patch. Do not compute
reachability, infer MTU, or claim firewall guarantees in the summary.

## 6. Stability and comprehension test matrix

The matrix below is deliberately the **focused next patch**, not QOL-1..QOL-7
as a batch.

### Unit / ViewModel — now

- `Subscribe -> Servers -> Custom(empty) -> Simple` keeps persisted
  `ConfigMode=subscribe`, shows effective subscribe mode and enters the existing
  Smart Connect probe/scorer on Connect.
- Dead active row plus one live candidate selects the live candidate after the
  Custom peek.
- Same transitions with a real custom config and no enabled subscription still
  select custom mode.
- The connected auto-selected label gate reads effective subscription mode.
- A negative `ConnectionIntentIndex` cannot overwrite a stored Gaming intent.

### Headless Avalonia — prove before expanding the fix

- EN->RU and RU->EN with Gaming selected preserve both visible index and stored
  intent.
- Restart after the locale switch still hydrates Gaming.
- If the one-line negative-index guard is insufficient, only then add explicit
  selection restoration around the collection refresh.

### Visual regression — now

- Keep existing baselines; add only Simple effective mode and Subscribe Gaming
  selection if the headless result changes layout-visible state.
- Public, Deep Verify, URL masking and RU-bypass status get no baseline in this
  patch because they are deferred.

### Published-binary WINBRAT E2E — now

1. identity and SHA256 gate;
2. active subscription -> Custom empty -> Simple state-coherence scenario;
3. Connect and prove `[ServerHealthProbe]` / Gaming scoring runs and does not
   dial a known-dead active row when a live row exists;
4. EN<->RU with Gaming selected, restart, and verify it remains Gaming;
5. disconnect cleanly and scan the complete test-window logs.

The game-disconnect measurement is a separate observation run: record active
protocol, exact event time, core/HTTP health, bounded UDP health and any
restart/failover decision. It does not authorize an automatic AWG switch.

RadioButton/Expander/virtualized-list E2E remains fail-closed. Add stable
`AutomationId` or an Appium flow only for a release-critical scenario that
cannot be proven by unit/headless/visual layers. Avalonia's own guidance favors
this layered approach rather than making every assertion a heavy E2E test.

## 7. Source cross-check

Primary official guidance used:

- [Microsoft: guidelines for app settings](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings) — smart defaults, simple settings, immediate reflection, and an explanation for disabled controls.
- [Microsoft: progressive disclosure controls](https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-progressive-disclosure-controls) — keep important status visible, reveal details on demand, and make state predictable/reversible.
- [Microsoft: error message guidelines](https://learn.microsoft.com/en-us/windows/win32/debug/error-message-guidelines) — state what happened, the result and a realistic next action.
- [Microsoft: Windows app design guidelines](https://learn.microsoft.com/en-us/windows/apps/design/guidelines-overview) — consistent navigation, commanding, layout, usability and writing.
- [Apple: Settings](https://developer.apple.com/design/human-interface-guidelines/settings) — minimize settings, prefer smart defaults, and keep task-specific controls with the task.
- [Apple: Feedback](https://developer.apple.com/design/human-interface-guidelines/feedback) — put status near the item, make failures actionable, and match interruption to severity.
- [Avalonia: Testing](https://docs.avaloniaui.net/docs/testing/) — combine unit, headless, visual and real-window/Appium layers according to risk.
- [Avalonia: Headless Testing Platform](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform) — use real Avalonia property/layout/input behavior for fast UI checks.
- [W3C WCAG: Labels or Instructions](https://www.w3.org/WAI/WCAG21/Understanding/labels-or-instructions.html) — every input/option needs an understandable label or instruction.

Repository cross-references:

- `plans/phase1-setup-diagnostics-wizard-2026-08-07.md`
- `plans/mtu-end-to-end-audit-2026-08-03.md`
- `plans/vpnrouter-ux-audit-2026-05-01.md`
- `plans/OPEN-DEFECTS.md`
- `plans/refactor-backlog.md`

The May audit already drove many fixes visible today: Autostart now explains
and repairs its prerequisite, Updates exposes current version/manual check and
redacted diagnostics, Custom JSON has a useful empty state, Public no longer
duplicates its hero title, and Applications explains Full Tunnel. Those are
not reopened here.

## 8. Exact next-task prompts

### Prompt A — connectivity-first state fix only

```text
Fix only the confirmed connectivity-first QOL-1 defect from
plans/qol-interface-and-recovery-audit-2026-08-08.md and the adjacent
Connection Intent measurement gate. Do not include Public copy, URL masking,
Deep Verify UI, RU-bypass copy, MTU persistence or any other QoL item.

Start with AGENTS.md, .claude_handoff.md when present,
VPNRouter.App/CLAUDE.md, OPEN-DEFECTS and audit section 9. Use exact
qwen3.8-max-preview as a read-only reviewer with safe/plan/no-recording and all
tools disabled; pass only sanitized code excerpts.

Preserve the existing SaveSettings subscription guard and Servers-page Custom
peek visuals. Extract the smallest private effective-mode decision needed by
SimpleConfigModeSummary, Simple Smart Connect and the connected urltest label;
do not mutate IsVlessMode/IsSubscribeMode inside SaveSettings and do not touch
the resolver or MainWindowViewModel.cs:4264.

First add an Avalonia-headless EN<->RU test with Gaming selected. Always reject
ConnectionIntentIndex < 0 before persistence; add more selection restoration
only if the headless test proves the guard insufficient.

Acceptance: Subscribe -> Servers -> empty Custom -> Simple still shows the
effective subscribe mode; Connect enters ServerHealthProbe and gaming scoring;
a dead active row is not dialed blindly when a live candidate exists; connected
auto-selected status can refresh; locale switch and app restart preserve Gaming.
Unit/headless tests plus published-candidate WINBRAT E2E are mandatory. Update
OPEN-DEFECTS, commit, push and open a draft PR; do not release.
```

### Prompt B — game disconnect measurement, no behavior change

```text
Investigate only the owner's intermittent game disconnect on the current
published candidate. Do not change product behavior, protocol priority,
failover, MTU or thresholds. Read AGENTS.md, the Gaming connection-stability
entry in OPEN-DEFECTS and audit section 9. Use exact qwen3.8-max-preview only as
a read-only reviewer of sanitized excerpts; never pass subscription URLs,
server addresses, keys, configs or raw logs.

Run only on WINBRAT through brat-verify. Before the game session record the
effective config mode, selected Connection Intent, active row identity as an
opaque local label, protocol family, HealthMonitor serving state and failover
cycle. At the exact disconnect time compare existing core/Clash HTTP health
with a bounded secret-free UDP-path check; do not add continuous traffic or a
new production monitor. Record whether sing-box crashed, HealthMonitor restarted,
AutoFailover chose a row, HTTP remained healthy while UDP failed, or no VPN-side
event occurred.

Classify: dead server/core failure, blind failover pick, UDP-only degradation,
provider/endpoint event, game-only event, or inconclusive. Only if ownership is
proven write a separate minimal implementation prompt. Restore the test machine,
update OPEN-DEFECTS and the audit, commit/push a docs-only result; no release.
```

### Prompt C — measurement-gated protocol/verifier experiment

```text
Investigate only the repeated Deep-verify WS invalid_public_key candidate from
plans/OPEN-DEFECTS.md and plans/mtu-end-to-end-audit-2026-08-03.md. Do not use a
real subscription or user credentials. Build secret-free deterministic fixtures
for VLESS+WS and the released lx sing-box check path, compare parser output,
ConfigGenerator output and VlessDeepVerifier output field by field, and test one
outbound at a time so sibling same-IP routes cannot mask attribution.

Classify parser defect, verifier transform defect, invalid fixture, or refuted.
Do not change thresholds, auto-select, MTU or production connection behavior
until ownership is proven. Record every finding in OPEN-DEFECTS before any fix;
Qwen is read-only and receives fixtures/code excerpts only. No release.
```

### Prompt D — parallel GPT Pro review

```text
Perform an independent adversarial UX/recovery audit of VPNRouter v2.48.0-r8.
Read AGENTS.md, VPNRouter.App/CLAUDE.md,
plans/phase1-setup-diagnostics-wizard-2026-08-07.md,
plans/qol-interface-and-recovery-audit-2026-08-08.md,
plans/OPEN-DEFECTS.md, plans/refactor-backlog.md and only the relevant
Views/ViewModels/tests. Do not change product code and do not read or reproduce
subscription URLs, UUIDs, public keys, local configs, diagnostic archives or
other credentials.

Independently validate or refute every QOL-1..QOL-7 finding. For each page
(Simple, Servers, Subscribe, all Settings sections, Applications, all Tools,
Public Search/Saved, wizard, About), answer: what the user thinks is active,
what is actually active, existing recovery/one-click path, ambiguity, smallest
safe correction, and exact unit/headless/visual/WINBRAT test. Use only primary
official Microsoft, Apple, Android, Avalonia and W3C sources for UX claims.

Challenge all proposed one-button pipelines: recommend one only if no existing
action covers the goal. Separate confirmed defect, QoL improvement and
measurement-gated hypothesis. No speculative auto-MTU, firewall automation,
threshold tuning, coordinate UI automation or merged connection pipeline.
Return a short priority table, disagreements with the audit, and a minimal
patch sequence; do not release.
```

## 9. Adversarial reconciliation and priority

The attached GPT Pro review was checked against the released source and then
given to exact `qwen3.8-max-preview` as sanitized facts with every tool excluded.
Accepted corrections:

- QOL-1 affects Smart Connect and the urltest label, not only presentation;
- QOL-2 should change Simple current-state copy, not the Routing option text;
- QOL-3 has two live misleading strings;
- QOL-5 may persist General, but that consequence needs a headless/disk proof;
- QOL-6 already has a final Verified summary; the nested dispatcher post can
  overwrite it;
- wizard Undo can persist a previously unsaved MTU, but the existing MTU-5 fix
  removes that path without new wizard machinery;
- RU bypass has no effective-state signal when geo data is unavailable.

Corrections to the external review:

- `P1-lite` is not a ledger severity. QOL-1 is recorded as P1 because it skips
  the pre-connect health/intent path in the owner's current primary scenario;
- the stale Deep Verify result existed in the MTU report, not as a duplicate
  `OPEN-DEFECTS` entry before this audit;
- blind runtime failover is real, but moving it into the NOW patch would be
  speculative. It selects the first structural candidate, while no evidence yet
  proves the game disconnect coincides with a dead HTTP/core probe or a UDP-only
  failure. Measure that divergence before changing selection.

Priority after reconciliation:

| Order | Decision | Why |
|---|---|---|
| NOW | Effective subscribe mode for Simple Smart Connect | Direct confirmed path to intermittent failed connect |
| PROVE/NEXT | Preserve non-General intent across locale refresh | Could silently remove AWG/HY2 gaming preference; disk impact not yet measured |
| MEASURE | Game disconnect: active protocol + HTTP/core + bounded UDP + failover row | Required before automatic AWG/protocol failover |
| DEFER | Deep result copy, Public text, URL-row masking, RU status, MTU autosave | Valid but not owners of the reported connectivity symptom |

## 10. Final connectivity-first verdict

- Wizard: useful, working and already sufficient as the guided recovery entry.
- Stability: no app crash or main-tunnel failure observed; the final log scan
  found the repeatable three-row WS verifier candidate, not a UI/session crash.
- Highest-value next change: make Simple Connect use the persisted effective
  subscription mode after Custom browsing so Smart Connect and Gaming scoring
  cannot be skipped.
- Lowest-value ideas: another wizard, another reset button, automatic MTU,
  automatic firewall repair, or a universal “fix everything” pipeline.
- Release decision: do not release the broad QoL list. Ship a later candidate
  only after the focused connectivity state fix passes unit/headless and the
  full WINBRAT connect scenario. Automatic AWG failover waits for one measured
  game-disconnect trace.
