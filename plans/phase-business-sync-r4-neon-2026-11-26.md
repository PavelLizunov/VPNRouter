# Business r4 sync and Neon interface completion

- Branch: `dsh/business-sync-r4-neon-20261126`
- Base: `origin/business` at `a4951195` (PR #742)
- Standard reference: `origin/main` at `6538682b` (v2.51.1-r4)
- Risk: High. The shared verifier, DNS resolver choice, core pins and the
  Business window are edition-specific surfaces.
- Rollback: revert the sync commits and the UI commits independently on
  `business`; no settings migration is added.

## Intent

Bring the applicable r4 changes into the Business line without importing the
Standard picker, and close the remaining gaps between the approved Neon
interaction sketch and the real Avalonia Business window.

## Scope

### r4 inventory

- `#741` (`3bfe504b`) and `#742` (`a4951195`) are already in the base. Their
  accent tokens, repair card, consent, receipt, menu routing and DNS/memory
  backports are not reimplemented.
- `#728` (`a782ec18`):
  - Port the `v1.14.4-vpnctl.3` core pins for the Standard desktop, macOS,
    Linux, Arch and Android paths in the Business tree. The Windows Business
    installer already pins `.3`, so both editions use one verified release.
  - Port the DNSTT/`ProtocolGroups` classification fix with its regression.
  - Exclude the Standard subscription picker, category filters, country-flag
    assets, `SubscriptionServerPreference`, Simple-mode copy and the release
    preparation workflow from `#740`. Business selection is policy-driven and
    has no matching category surface; `#740` is Standard publication only.
- `#743` (`50e12e4a`):
  - Port the real-traffic selection contract: fresh HTTPS trace, a second
    HTTPS 204 control probe and a required UDP STUN probe for games/voice
    candidates, using the configured `Dns.LocalDns` resolver.
  - Keep the existing Business generation counter, cancellation and stale
    result guards; verify them against the repaired selection flow.
  - Exclude the connected-category switch and its physical-interface bypass:
    the Business picker and repair never verify while a tunnel is connected or
    starting (`CanProbe`, `CanStartVerifiedBusinessConnect`), so that path has
    no Business scenario. The shared config builder does not gain unused
    interface options.

### Neon interface gaps after #741

Verified against the approved sketch SHA-256
`3dc81a57c8bb153b0f8e548691313ea51944c937eec701c5996b59a79a7e8433` and the
headless captures at `ci-cb6b1b4a`:

- The primary Connect/Disconnect action and the update action use
  `Classes="solid"` without a base style, so they render as the soft tonal
  button instead of the approved solid primary role.
- The Home server summary does not mark the active server or a warning
  selection with the approved semantic colour.
- The server row has the activity text but not the approved three-segment
  load meter that replaced the ascending antenna icon.
- The repair entry stays a ghost button after a failed connection, and the
  connection pill has no error state, so recovery is less prominent than in
  the sketch.

Do not replace the native `ComboBox`, the existing repair state machine, the
consent card or already matching copy. Reuse `Tokens.axaml` roles, bilingual
strings and synthetic headless fixtures.

### Invariants

- Managed Business policy, portal transport, DNS/TUN ownership and the policy
  launch acknowledgement stay authoritative.
- Manual server choice and automatic real-traffic selection keep their current
  persistence semantics; the policy picker is not replaced.
- Subscription refresh, update integrity checks and rollback confirmation stay
  unchanged.
- The Standard mode compiled from the Business tree keeps working and uses the
  same `.3` core pins as the Business installer.

## Verification

1. Build: GitHub CI only. Windows Standard/Business and Ubuntu jobs, the
   Business surface job, UI MCP/headless jobs and static gates at the exact PR
   head. No SDK, restore or build on the workstation.
2. Tests: ported regressions for the selection probe config, STUN validation,
   DNS resolver and DNSTT grouping, plus existing Business repair, window,
   update and policy suites in CI.
3. Documentation: this brief and its outcome, `plans/OPEN-DEFECTS.md`, link and
   ASCII checks, `git diff --check`, XML/JSON/YAML parse of touched files.
4. Review: trace the verifier, selection and UI paths against their callers.
   Independent review is requested by the parent task, not claimed here.
5. UI: headless synthetic captures from the Business matrix at 340, 360 and
   420 px, both languages. Compare with the approved sketch; do not capture a
   live window or real logs.
6. Integration: check the edition defines, core pins, workflow inputs and the
   market-specific settings. Live installation remains a release gate.

## Outcome

Pending implementation.
