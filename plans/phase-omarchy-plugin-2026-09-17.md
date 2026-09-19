# Phase - VPNRouter for Omarchy Quattro

Owner: DSH session; human approved the Micro-Spec on 2026-09-17.
Branch: `dsh/omarchy-plugin-2026-09-17`
Base: `517bf7e227e8c75a17479c07facd02033499aa5b`
Risk: HIGH (network lifecycle, privileges, secrets, cross-process ownership).

## Why

Reproduce VPNRouter as a native Omarchy plugin, not a launcher for Avalonia.
Reuse VPNRouter.Core and sing-box without maintaining a second routing engine.
The owner explicitly requested Gemini workers and authorized the public
`PavelLizunov/omarchy-vpnrouter` repository. That repository was created empty.
The owner supplied `omarchy-test` for initial read-only host inspection.
Installation, VPN mutations, shell restarts and publication remain unauthorized.

## What - approved intent and invariants

- Native QML/Quickshell bar widget, panel and companion singleton service inside
  the existing Omarchy shell; never a second Quickshell or Avalonia window.
- Linux feature parity: connection lifecycle, server import/selection/testing,
  subscriptions, free configurations, process include/exclude/full routing,
  profiles, custom rules/configurations, DNS/TUN settings and diagnostics.
- Simple/advanced modes, English/Russian, host theme and keyboard support.
- Windows-only Zapret, MTProto wrapper and Windows Service are excluded.
- C#/.NET 10 headless adapter references Core. Plugin source and setup live in
  the separate plugin repository with a root manifest. No forked Core sources.
- Closing the panel preserves VPN. Disabling the plugin stops only its own
  session and disposes owned resources; no implicit adoption of another owner.
- Existing desktop/mobile behavior remains unchanged except reviewed additive
  integration seams; no release/version changes or automatic deployment.

## How - interface and data contract

- Plugin ID: `io.github.pavellizunov.vpnrouter`; repository: `omarchy-vpnrouter`.
- One helper-owned engine; JSON-lines over stdin/stdout, no public HTTP server.
- Protocol version 1: request `id`, `method`, `params`; response matching `id`
  with `result` or structured `error`; asynchronous state/progress events.
- Explicit disconnected/connecting/connected/disconnecting/error/unavailable
  states. Only typed Core readiness establishes connected, not process spawn.
- Bounded input bytes before buffering, JSON depth, collection sizes, queues,
  response size, timeouts and cancellation. No secret-bearing argv or logs.
- Configuration changes validate and conflict-check before persistence; status
  snapshots omit secrets. UI preferences stay separate from VPN credentials.
- Privileged operations use system authorization and narrow operations; never
  QML password collection, NOPASSWD grants, privileged shell or implicit retry.
- Packaging pins compatible backend and sing-box artifacts, checks integrity,
  preserves existing configuration and documents manual authorized setup.
- Implementation sequence: source mapping -> precise shared protocol -> backend
  and disjoint QML/packaging Gemini assignments -> source review -> tests -> PRs.

## Research evidence and limits

Three explicitly routed Gemini workers inspected Core APIs, lifecycle/ownership
and Omarchy examples. Their reports are discovery leads, not acceptance.
Confirmed by coordinator: Core is UI-independent (`VPNRouter.Core.csproj`);
CLI status is terminal output (`Commands/StatusCommand.cs`); current CLI start
has an admin gate and Windows-oriented messages (`Commands/StartCommand.cs`).
`TunOwnershipLock.TryAcquire` fails open when named semaphore creation fails
(lines 78-99); a new Unix consumer must not assume this supplies exclusivity.
`LinuxFirewallManager` runs `sudo -n nft` and reports failure without blocking
(lines 38-44, 152-164); the new plugin must not introduce NOPASSWD or report an
unarmed firewall as protected. These integration findings are in OPEN-DEFECTS.

Omarchy's `manual/32-shell-plugins.md` requires a root manifest and reserves
`omarchy.*`. The `omarchy-` GitHub prefix is a convention, not a parser rule.
Examples: PavelLizunov/omarchy-rog-cetra-control, omarchy-tts, omarchy-mx-ergo.
User reference skill: PavelLizunov/omarchy-plugin-patterns (MIT); authoring and
security references read. Actual installed host API still needs inspection.

Read-only preflight: `omarchy-test` resolved using existing trusted SSH config;
identity `omarchytest`, user `tester`; Omarchy, Quickshell, dotnet, git and Python
were present. No installation or runtime scenario was performed. `linux-worker`
was reachable but dotnet was absent from PATH; do not provision it automatically.

## Tests and six gates

1. Build - PENDING: Release solution and headless adapter on exact-SHA prepared
   worker/CI; no heavy builds on harness-test.
2. Tests - PENDING: Core focused/full suite; JSON framing, malformed/oversized
   input, cancellation, readiness, ownership/config conflicts and secret safety.
3. Documentation - PENDING: feature parity matrix, setup/update/remove, protocol,
   limitations and final outcome; coordinated READMEs in both repositories.
4. Independent review - PENDING: Gemini correctness/test/security lenses;
   coordinator source verification and change-verification/security-review.
5. UI/runtime - PENDING: isolated QML/fixture checks, keyboard/scaling/RU/EN,
   themes/multi-monitor and real dataplane on approved Omarchy target. Read-only
   host permission is not authority to install, restart or connect VPN.
6. Integration - PENDING: compatible manifest/backend versions, packaging,
   lifecycle enable/disable/reload, no regression to other platform consumers.

## Rollback

Task commits can be reverted after review; neither main is pushed directly.
No live changes exist to roll back. Plugin removal must be documented to stop
its own session, remove its own artifacts and preserve user configuration.

## Outcome

Status: IN PROGRESS. Approved specification persisted; source discovery and
initial host preflight completed; public plugin repository created empty.
No implementation, build, runtime or security acceptance claim yet.
Unrelated untracked workspace files are preserved and excluded from commits.
PowerShell is absent on the control plane; exact PR checks will be observed via
GitHub as permitted by plans/v3.0-execution-methodology.md:48.
