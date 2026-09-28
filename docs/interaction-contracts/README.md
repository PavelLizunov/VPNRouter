# Interaction contracts

Per-feature behavior contracts derived from the framework in
`plans/user-interaction-boundaries-and-edge-case-verification-framework-2026-06-02.md`.

Each contract documents a feature's **User Interaction Envelope**: states,
actions, allowed/forbidden transitions, durable-data ownership, failure +
cancel + relaunch behavior, platform parity, and the global invariants it must
uphold. Contracts are *living docs* — when a bug is fixed, update the contract,
not just the regression test (framework Step 8).

## Adoption status (lean)

- **Phase 1 (now):** Markdown contracts for the features that already have audit
  material. Written: `FC` (Public Configs), `APP` (Applications routing). The
  other Tier-A features get a contract lazily, the next time they're touched.
- **Phase 2 (deferred):** machine-readable YAML manifest — NOT adopted yet.
- **Phase 3 (deferred):** coverage-report tooling — NOT adopted yet.

The framework's own advice: "Do not start by adding a large custom test
framework. Start with living documents." We follow that — invariants as a
review checklist, contracts as docs, no bespoke tooling for a solo project.

## Contract index

| Feature | File | Status |
|---|---|---|
| `FC` Public Configs (search / recheck / apply) | `FC-public-configs.md` | written; 9 decisions resolved |
| `APP` Applications routing (Include/Exclude/Full) | `APP-applications-routing.md` | written |
| `VPN` lifecycle + ownership | _lazy_ | recommended next |
| `CFG` config adoption (generated/subscribe/custom) | _lazy_ | #147 closed the custom-JSON leak |
| `SUB` subscriptions | _lazy_ | |
| `RULE` custom rules | _lazy_ | |
| `DNSFW` firewall + DNS lockdown | _lazy_ | |
| `UPD` updater | _lazy_ | |

## Global invariants (review checklist)

Reference these in every contract; check them in every review.

- **G1** No silent traffic leak — route polarity never changes silently; missing
  proxy outbound / bad route / bad DNS fails closed; ownership change never
  reports protected while direct.
- **G2** Preserve user-owned durable data — convenience actions never wipe
  unrelated settings; parse/fetch/apply/migration failure keeps prior good state.
- **G3** UI tells the truth — enabled ⇒ accepted; disabled is real; status
  distinguishes running/cancelled/timeout/failed/stale; `Verified` means one thing.
- **G4** Single-flight operations — repeated click can't start a second
  conflicting op; cancel targets the active op only.
- **G5** Bounded work — bodies/files/lists/retries/waits have explicit limits;
  slow-progress and total-timeout are separate; cancel has bounded latency.
- **G6** Atomic adoption — read→validate→prepare→persist atomically→switch; never
  erase prior good state before the candidate is usable.
- **G7** Safe restart — relaunch converges to a valid state; orphan temp/procs
  cleaned; never assume the prior shutdown completed.
- **G8** Capability honesty — unsupported behavior is hidden/disabled/explained;
  a no-op stub must not look like a working safety feature.
- **G9** Secret hygiene — logs/diagnostics/temp/errors never expose credentials;
  redaction fails closed.
- **G10** Observability — every long op has enough structured (secret-free)
  context to reconstruct phase / cancel / fallback / recovery.

## Global boundaries

- **B1** One primary mutation at a time (VPN start/stop/reconnect/apply blocks
  conflicting lifecycle + config mutations; navigation + read-only allowed).
- **B2** Persisted state ≠ runtime state (define per-mutation: persist now,
  hot-reload, mark-pending, or block while starting/stopping; route service-owned
  changes through the service contract).
- **B3** Convenience data never owns user data (user-owned preserved unless
  explicitly deleted; derived cache replaced/cleared atomically).
- **B4** Connectable means one thing = structurally valid + accepted by policy +
  required verification level + not blocked by current op. Expert override, if
  any, is a separate labeled action.
- **B5** Destructive actions are deliberate (confirm/undo for user-owned config,
  many records, expensive history, OS state, installed helper/service).
- **B6** Every external replacement is last-known-good (candidate validation +
  atomic replace for pool/subs/updater/rule-sets/helpers/GeoIP/import/migration).
- **B7** Platform parity is explicit: `same` / `adapter` / `reduced` /
  `unsupported` — never an accidental silent partial.

## Per-feature review checklist

Before implementation:
- [ ] One-sentence user intent.
- [ ] Feature ID + action IDs.
- [ ] Only the touched state dimensions listed.
- [ ] Supported / handled-invalid / forbidden-concurrent / unsupported defined.
- [ ] Durable-data ownership stated.
- [ ] Success / failure / cancel / relaunch behavior defined.
- [ ] Size / count / retry / timeout bounds set.
- [ ] Desktop / Android / platform parity marked.
- [ ] Applicable global invariants referenced.

During implementation:
- [ ] UI enabled predicate ↔ ViewModel command guard ↔ Core validator aligned
      (Core guard mandatory for safety-sensitive behavior).
- [ ] Typed state/result where behavior differs (not loosely-related bools).
- [ ] External replacements last-known-good + atomic.
- [ ] Structured non-secret logs.
- [ ] Cheapest sufficient test first (L0 static → L5 live).

Before release:
- [ ] Regression scenarios for touched contracts.
- [ ] Adjacent-action sequences.
- [ ] Cancellation at each changed async phase.
- [ ] Narrow desktop window + RU/EN + representative Android viewport for changed pages.
- [ ] MCP UIA / ADB E2E through the final user-visible effect.
- [ ] Log + diagnostics scan for errors and secret leakage.
- [ ] Scenario IDs recorded in the release/verification report.

## Verification layers

`L0` static contract · `L1` pure unit · `L2` hermetic integration (fake seams) ·
`L3` headless UI · `L4` packaged local E2E (MCP UIA / ADB) · `L5` live canary.
Pin each scenario at the cheapest layer that proves it.
