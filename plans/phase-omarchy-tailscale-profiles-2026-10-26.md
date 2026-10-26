# Omarchy profiles and Tailscale coexistence (2026-10-26)

## Intent

The owner reports unclear single-choice routing profiles in Omarchy, unlike the
Windows app groups, and Tailscale failing to connect on another machine. Recover
the intended application-group semantics and close source-confirmed coexistence
gaps. The reporting host is not available in this task; no causal live claim.

## Observed owning sources

- ProfilesView.qml sends only [modelData.id] and displays no description/apps.
- ProfileFeature.SelectAsync changes ActiveProfile but not the explicit include
  list; ResolveEffectiveAppProcesses prefers that explicit list after Apps edits.
- TunSettings excludes CGNAT only via startup detection of an already-up Tailscale
  adapter, not in mandatory ranges. Generated full/exclude routing has no service
  bypass before DNS/QUIC/proxy rules. IPv6 ULA is already excluded by fc00::/7.
- Windows has a bypass suggestion catalog, not proof of an always-active default
  Tailscale process exclusion. Do not promise parity that sources do not show.
- Counterevidence: normal Linux Tailscale policy rules use priorities 5200-5500,
  earlier than our 8900 start index. Missing exclusions alone therefore do not
  prove interception or explain service startup. See official routing docs:
  https://tailscale.com/docs/reference/troubleshooting/network-configuration/lan-traffic-overlapping-subnets
  Reserved addresses: https://tailscale.com/docs/reference/reserved-ip-addresses

## Scope

Add a persistent effective CGNAT route exclusion shared by generated/custom
configs. Route Linux Tailscale processes directly before DNS/QUIC/app rules in
both paths. Surface system process bypass separately from user app lists.
Expose profile descriptions and exact process names. The plugin reuses apps.set
for group and individual checkboxes instead of the legacy ActiveProfile selector;
all controls therefore edit the real explicit include list and reflect overlap.
No second profile-to-app synchronization layer is needed. Describe when groups
apply, preserve unrelated manual selections, and show concrete applications.

## Invariants, risk and rollback

Keep exact process casing, schema/storage revision checks, manual app selections,
other-platform process routing, private LAN and existing IPv6 exclusions. No
change to Tailscale service, live system routes, remote hosts or application
security. This is a route-exclusion change: CGNAT/Tailscale traffic is intentionally
direct. Revert the scoped commits to roll back; no automatic reconnect.

## Verification

1. Build: no build requested yet; source change will be checkpointed.
2. Tests: no tests or new test writing requested; do not run them locally.
3. Docs: record actual files/behavior and retain target-cause uncertainty.
4. Review: owning source and task diff only, no additional model review.
5. UI/live: no QML preview, VPN connection or target probes requested.
6. Integration: cover generated/custom consumers and plugin/backend response.

## Outcome

Implemented source changes: mandatory CGNAT exclusion; early Linux Tailscale
process bypass in generated and custom routing; system bypass metadata; profile
description/process metadata; legacy catalog baseline in apps.list before the
explicit include list is initialized. Plugin app-group controls reuse apps.set,
show concrete process toggles and mode applicability, and refresh the shared list.
No claim of live Tailscale recovery, visual acceptance or full Windows parity.

Task-owned source/diff inspected. Main and installed packages are unchanged;
this batch is delivered as a separate task checkpoint/PR, not a release or target
installation. No tests/build/runtime checks were requested or run locally.
