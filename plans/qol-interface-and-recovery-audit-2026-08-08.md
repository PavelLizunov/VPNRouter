# VPNRouter QoL, recovery and interface audit — 2026-08-08

## Decision

The published Windows build `v2.48.0-r8` is stable enough for the tested
configuration, and the new setup/diagnostics wizard works on WINBRAT. The next
useful work is a small clarity/state-consistency patch, not another recovery
framework:

1. rotate the test subscription link that appeared in historical tracked plans;
2. keep the wizard as the single guided diagnose/repair path;
3. fix the Custom JSON -> Simple mode display drift;
4. show an honest effective-routing summary with active direct exceptions;
5. correct Public-page instructions and measure whether its local Connect action
   needs clearer copy;
6. preserve the connection-intent selection and volatile status language after
   an in-process locale switch;
7. make Deep verify's final summary distinguish processed, passed, failed and
   untested rows.

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

### QOL-1 — Custom JSON browsing desynchronizes the displayed mode — P2

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

Smallest safe design: when an active subscription makes Custom a browse-only
view, do not leave the mode flags in custom state. Preserve the persistence
guard and add a transition test; do not redesign ConfigMode.

### QOL-2 — “All traffic” omits active direct exceptions — P2

Full Tunnel is selected while `Russian traffic via real IP` is enabled. The
behavior is intentional, but “All OS traffic through VPN” is false as an
effective summary. Display a compact exception, for example:

> Full tunnel · Russian destinations direct

The same projection should feed Simple and Routing. Details remain in Routing;
do not expand the Simple page with rule internals.

### QOL-3 — Public empty state names a missing action — P2

`Cache is empty — click 'Refresh'` conflicts with `Find working configs`.
Change the instruction to the actual action. Do not add a second Refresh button.

### QOL-4 — Subscription row repeats a sensitive opaque URL — P2

The Advanced subscription row exposes most of a bearer-like identifier in a
screen-share-friendly list even though Simple already has the edit surface.
Render only origin plus ellipsis in the row; never modify/canonicalize the
stored URL and never write it to diagnostics.

### QOL-5 — Locale switch can blank the connection-intent choice — P2

Changing EN -> RU replaces `ConnectionIntentChoices`; the ComboBox loses its
visible selection while `ConnectionIntentStatusText` still says general.
Preserve/reapply the index after refreshing localized choices. Pin both locale
directions in a headless binding test.

### QOL-6 — Deep verify final state lacks outcome counts — P2

Show, at completion:

> Processed 20 · working N · protocol failed M · untested K

Exact buckets must come from existing row verdicts; this is presentation only.
Do not treat “processed” as “passed” and do not change protocol thresholds in
the same task.

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
| “Choose a usable server” | quick test, Deep verify, optional auto-select | selected/health state | Improve result copy; do not tune from one sample |
| “Find a free config” | Find working configs | public cache/selection | Done; fix stale Refresh wording |
| “Fix DPI / Telegram” | one-click Zapret / Start and open Telegram | feature-specific | Done; these reuse existing surfaces |
| “Understand what is routed” | pieces across Simple/Routing/Apps/Rules | none | Add one read-only effective-routing summary projection |

The useful new concept is not another button. It is one short, shared answer to
“what is active now?” with details on demand:

```text
VPN off
Profile: subscription
Routing: full tunnel · Russian destinations direct
Protection: IPv4 only · DNS cache flush
```

Only already-known state belongs here. Do not compute reachability, infer MTU,
or claim firewall guarantees in this summary.

## 6. Stability and comprehension test matrix

### Unit / ViewModel

- `Subscribe -> Servers -> Custom(empty) -> Simple` keeps effective mode and
  persisted `ConfigMode` coherent.
- Same transitions with a real custom config and no enabled subscription still
  select custom mode.
- Effective-routing summary covers split/full, Russian bypass, and no bypass.
- Deep final summary counts passed/failed/untested independently of processed.
- EN/RU choice refresh preserves `ConnectionIntentIndex`.
- MTU-only wizard reset preserves routing; restore and undo remain pinned by
  existing tests.

### Headless Avalonia

- Simple, Subscribe, Public Search and Wizard render at 520, 440 and 360 widths
  in EN and RU.
- Runtime language switch preserves the visible intent selection.
- Public empty-state instruction exactly names its primary button.
- Public empty state and `Find working configs` expose matching visible and
  accessible instructions.
- Sensitive subscription display uses the masked presentation value while the
  model retains the original URL.

### Visual regression

- Keep existing page baselines; add only the confirmed regression surfaces:
  Simple effective summary, Subscribe RU intent, Public empty state, Deep final
  summary and the RU wizard first step.
- Do not baseline raw URLs, server IPs or timing values.

### Published-binary WINBRAT E2E

1. identity and SHA256 gate;
2. Simple EN/RU screenshot at default and narrow widths;
3. active subscription -> Custom empty -> Simple state-coherence scenario;
4. quick test -> Deep verify -> final outcome counts;
5. Public Search empty state names the visible primary action;
6. Wizard checks -> MTU reset -> undo -> restore -> diagnostics export;
7. bottom-of-page semantic assertion for Autostart;
8. 120-minute remote log scan.

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

### Prompt A — minimal confirmed QoL patch

```text
Implement only confirmed findings QOL-1 through QOL-7 from
plans/qol-interface-and-recovery-audit-2026-08-08.md. Start with AGENTS.md,
.claude_handoff.md when present, VPNRouter.App/CLAUDE.md, OPEN-DEFECTS and the
audit. Use qwen3.8-max-preview as a read-only independent reviewer with safe
mode, plan approval, chat recording off and all tools disabled; never pass it
URLs, keys, user configs or screenshots containing identifiers.

Constraints: no new recovery framework, no auto-MTU, no firewall behavior
change, no protocol threshold change, no connection-pipeline merge. Preserve
the existing ConfigMode subscription guard. Make the smallest shared state/copy
fixes, add focused unit/headless tests, update each OPEN-DEFECTS disposition,
and verify the published candidate only on WINBRAT through brat-verify.

Required acceptance: Custom-empty browsing cannot change the Simple effective
mode; full routing names active direct exceptions; Public names the real action;
subscription row masks the opaque path; EN/RU switch preserves intent; Deep
summary shows processed/passed/failed/untested; volatile status never remains in
the old language. Commit, push task branch, open draft PR; do not release.
```

### Prompt B — measurement-gated protocol/verifier experiment

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

### Prompt C — parallel GPT Pro review

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

## 9. Final verdict

- Wizard: useful, working and already sufficient as the guided recovery entry.
- Stability: no app crash or main-tunnel failure observed; the final log scan
  found the repeatable three-row WS verifier candidate, not a UI/session crash.
- Highest-value next change: presentation/state coherence, especially Custom
  browsing and effective-routing truth.
- Lowest-value ideas: another wizard, another reset button, automatic MTU,
  automatic firewall repair, or a universal “fix everything” pipeline.
- Release decision: this audit makes no stable-cut or new-release request. Ship
  a later candidate only after a focused QoL patch and the layered gates above.
