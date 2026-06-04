# macOS parity plan: leak protection, DNS, firewall, update verification, shipped QA

Date: 2026-06-04

## Goal

Bring the macOS build closer to the Windows build in reliability, leak protection,
update confidence, and shipped-binary QA.

This is not about making every Windows-only feature available on macOS. Zapret,
Windows Service, ETW, and Windows Firewall are naturally platform-specific. The
goal is narrower and more important:

- macOS must not silently leak DNS when the user expects VPN protection.
- macOS must have an honest kill-switch story, not a no-op placeholder.
- macOS shipped packages must be installed and smoke-tested before stable cuts.
- macOS update/install paths must have gates comparable to Windows.
- macOS support bundles must include enough platform evidence to debug leaks.

## Current Gap Summary

| Area | Windows state | macOS state | Gap |
|---|---|---|---|
| Leak protection validation | `LeakProtection` validates generated config; Windows-specific DNS hardening exists | Shared config validation exists, but platform leak risks are not fully represented | macOS can look "valid" while system DNS leaks outside TUN |
| DNS hardening | `WindowsDnsHardening` + DNS lockdown firewall rules | No `MacDnsHardening`; fresh Olga_K logs show zero DNS queries in sing-box log while raw-IP traffic appears | HIGH |
| Firewall / kill-switch | `FirewallManager` via `netsh`; `block_on_vpn_fail`; DNS lockdown rules | `NullFirewallManager`; logs warning only | HIGH |
| Process monitoring | ETW, low-latency process events | polling monitor, about 0-2s latency | MED |
| Update verification | Windows update integration workflow exercises real helper/copy path | macOS build produces DMG/ZIP; no equivalent install/update smoke | HIGH |
| Shipped QA | Windows has local MCP/UIA post-ship verification discipline | macOS has CI build + sing-box parser smoke, but no full install/connect/DNS run | HIGH |
| Diagnostics | Windows logs + firewall/DNS state are easier to reason about | macOS needs `scutil --dns`, `networksetup`, `route`, `ifconfig`, `pfctl` evidence | MED |

## Non-Goals

- Do not promise per-process macOS firewall parity until we prove it is feasible.
  macOS `pf` is packet-level; Windows Firewall can target application paths more
  naturally. If true per-process macOS kill-switch needs Network Extension, that
  is a separate larger feature.
- Do not block all macOS releases until every item is complete. Instead add
  explicit release gates by severity.
- Do not hide missing platform support behind generic "All platforms" language.
  If macOS protection is partial, the UI and docs should say so.

## Phase 0 - Evidence Baseline

Purpose: stop guessing. Create a repeatable baseline that proves where the leak
or packaging failure happens.

Inputs to collect from affected macOS users:

```bash
cp ~/Library/Application\ Support/VPNRouter/config.yaml ~/Desktop/
cp ~/Library/Application\ Support/VPNRouter/config/current.json ~/Desktop/
scutil --dns > ~/Desktop/vpnrouter-scutil-dns.txt
networksetup -listallnetworkservices > ~/Desktop/vpnrouter-network-services.txt
ifconfig > ~/Desktop/vpnrouter-ifconfig.txt
netstat -rn > ~/Desktop/vpnrouter-routes.txt
```

Local reproduction on the Mac build host:

- Install latest stable and latest candidate DMG.
- Use the same subscription/config mode as the user.
- Test both split and full tunnel.
- Capture:
  - `tcpdump -i en0 port 53`
  - `tcpdump -i en0 port 853`
  - `tcpdump -i en0 host <proxy-server-ip>`
  - sing-box log DNS lines
  - `scutil --dns` before connect, during VPN, after stop
  - `networksetup -getdnsservers <service>` before/during/after

Pass criteria:

- We can reproduce or rule out DNS egress outside the TUN.
- We know whether the leak is caused by system DNS settings, route exclusions,
  sing-box config shape, or macOS resolver behavior.
- We know whether full tunnel leaks differently from split tunnel.

## Phase 1 - Platform-Aware Leak Protection

Problem: shared `LeakProtection` can validate a sing-box config while macOS
system behavior still leaks DNS outside TUN.

Plan:

1. Add a platform-aware leak model.
   - Introduce a small report concept, for example `PlatformLeakProtectionReport`.
   - It should answer:
     - DNS is captured by sing-box config.
     - System resolver is forced into the tunnel.
     - DNS lockdown is available.
     - kill-switch is available.
     - full tunnel is safer than split for current platform.
2. Keep existing config validation.
   - `LeakProtection.ValidateConfig` remains the generated-config guard.
   - Add macOS-specific advisory/error checks around runtime protection.
3. Surface macOS limitations honestly in UI.
   - If `DnsLeakLockdown` is enabled on macOS but not implemented, show warning.
   - If `block_on_vpn_fail` is selected on macOS before `MacFirewallManager`
     exists, show "not supported yet" instead of silently accepting it.
4. Add tests.
   - macOS DNS hardening unavailable -> warning/error depending on mode.
   - macOS block-on-fail unavailable -> warning/error.
   - Windows behavior remains unchanged.

Files likely involved:

- `VPNRouter.Core/Services/LeakProtection.cs`
- `VPNRouter.Core/Services/ConfigPipeline.cs`
- `VPNRouter.Core/Services/StartupPipeline.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel*.cs`
- `VPNRouter.Tests/LeakProtection*Tests.cs`

Acceptance criteria:

- macOS no longer presents unsupported leak-protection settings as fully active.
- The generated config still must pass existing leak-protection checks.
- The runtime platform protection status is visible in logs and UI.

## Phase 2 - macOS DNS Hardening

Problem: current evidence suggests macOS `mDNSResponder` can resolve via the
physical network DNS while sing-box only sees already-resolved raw IP connects.
That defeats the user's expectation in both full tunnel and routed-app scenarios.

Plan:

1. Implement `MacDnsHardening`.
   - Mirror the lifecycle of `WindowsDnsHardening`:
     - apply after sing-box/TUN is ready,
     - enable optional lockdown,
     - restore on stop,
     - best-effort restore on app exit / next launch cleanup.
2. Decide the DNS capture method after Phase 0 evidence.
   - Option A: add/enable a local sing-box DNS listener and point macOS network
     services to it.
   - Option B: force active network services DNS to a TUN-reachable resolver.
   - Option C: use `pf` redirect rules for UDP/TCP 53 into the local resolver.
   - Option D: adjust sing-box route exclusions if upstream DNS is being
     excluded from TUN by auto-route loop prevention.
3. Save and restore original DNS settings.
   - Enumerate active network services:
     - Wi-Fi
     - Ethernet
     - USB/network adapters
   - Store original DNS per service in a small state file.
   - Restore exactly on stop.
   - If restore fails, log and show UI warning.
4. Handle edge cases.
   - No DNS servers configured.
   - "Empty" DNS means DHCP-provided DNS; restore must preserve that.
   - Multiple network services active.
   - IPv6 DNS.
   - DoH/DoT in browsers.
   - Captive portal / network switch while VPN is running.
   - Crash while DNS is hardened.
5. Add diagnostics.
   - Log `scutil --dns` summary before and after apply.
   - Log changed services and restored services.
   - Redact nothing sensitive except server credentials.

Verification commands on macOS:

```bash
scutil --dns
networksetup -listallnetworkservices
networksetup -getdnsservers "Wi-Fi"
sudo tcpdump -i en0 port 53
sudo tcpdump -i en0 port 853
```

Acceptance criteria:

- During VPN, `tcpdump -i en0 port 53` shows zero ordinary DNS egress for
  routed traffic.
- DNS queries appear in sing-box logs or in the chosen local resolver path.
- After stop, original DNS settings are restored.
- Crash/relaunch cleanup restores stale DNS hardening state.

## Phase 3 - macOS Firewall / Kill-Switch

Problem: `PlatformServices.CreateFirewallFactory` returns `NullFirewallManager`
on non-Windows platforms. On macOS, `block_on_vpn_fail` currently logs a warning
but does not block leaks.

Plan:

1. Replace the macOS no-op with `MacFirewallManager`.
   - Use `pf` / `pfctl` anchors.
   - Keep `NullFirewallManager` only as a last-resort fallback.
2. Start with two realistic protection layers.
   - DNS lockdown:
     - block outbound UDP/53, TCP/53, TCP/853 outside the VPN/local resolver
       path while VPN is active.
   - Session kill-switch:
     - if sing-box crashes and the profile has `block_on_vpn_fail`, block
       outbound traffic until VPN is stopped/recovered.
3. Be honest about per-process semantics.
   - Windows can create app-path firewall rules.
   - macOS `pf` is packet-level. True per-process blocking may not be possible
     without a Network Extension.
   - If macOS kill-switch is global/session-level, UI copy must say that.
4. Use pf anchors safely.
   - Anchor name: `com.ninitux.vpnrouter.killswitch` or similar.
   - Load rules via `pfctl -a <anchor> -f -`.
   - Enable pf via `pfctl -E` and store the token if macOS returns one.
   - Flush only VPNRouter's anchor on cleanup.
   - Never rewrite `/etc/pf.conf` directly unless absolutely required.
5. Add tests with a fake process runner.
   - Exact `pfctl` command shape.
   - Idempotent create/enable/disable/delete.
   - Cleanup on partial failure.
   - Does not flush unrelated anchors.
6. Add live Mac verification.
   - Start VPN.
   - Kill sing-box.
   - Confirm traffic is blocked or DNS is blocked according to selected mode.
   - Stop VPN.
   - Confirm traffic restores.

Files likely involved:

- `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs`
- `VPNRouter.Core/Platform/PlatformServices.cs`
- `VPNRouter.Core/Interfaces/IFirewallManager.cs`
- `VPNRouter.Tests/MacFirewallManager*Tests.cs`

Acceptance criteria:

- `block_on_vpn_fail` is no longer a no-op on macOS, or the UI explicitly
  labels the remaining limitation.
- DNS lockdown can be enforced at packet level.
- Cleanup cannot remove unrelated user/system pf rules.

## Phase 4 - macOS Update Verification

Problem: Windows has a real update integration workflow. macOS currently builds
DMG/ZIP and does a lightweight sing-box config smoke, but does not install and
exercise the shipped package.

Plan:

1. Implement shipped-package smoke workflow.
   - Use the existing design in
     `plans/smoke-matrix-macos-linux-2026-06-02.md`.
   - Start `workflow_dispatch` only.
   - Add release trigger only after repeated green runs.
2. macOS DMG smoke steps:
   - download release DMG,
   - verify SHA256 sidecar,
   - mount DMG,
   - verify `.app` structure,
   - remove quarantine for test,
   - run bundled `VPNRouter.CLI doctor`,
   - optionally launch GUI headlessly only as non-fatal crash smoke.
3. Add macOS update/install live gate.
   - Install previous stable.
   - Trigger update or simulate update path if current macOS update semantics
     are manual/download-only.
   - Verify app version after update.
   - Verify no stale mixed-version bundle.
   - Verify config/state survived.
4. Add integrity checks.
   - Hard-fail if DMG/ZIP hash sidecar mismatch.
   - Hard-fail if app bundle missing required binaries:
     - `VPNRouter.App`
     - `VPNRouter.CLI`
     - `VPNRouter.Core.dll`
     - `sing-box`
   - Soft-warn only where AOT trimming makes string-version checks unreliable.

Acceptance criteria:

- A release cannot be considered macOS-ready until the DMG mounts and the
  bundled CLI health check passes.
- Stable cut checklist includes macOS shipped-package smoke.
- Update/install verification is documented and repeatable.

## Phase 5 - macOS Post-Ship QA Gate

Problem: Windows has a culture/tooling path for post-ship MCP/UIA verification.
macOS needs an equivalent that runs on the Mac host.

Plan:

1. Create a macOS post-ship verification checklist.
   - Download newest release DMG.
   - Install over existing app.
   - Launch app.
   - Import/use a known safe subscription.
   - Connect.
   - Verify TUN appears.
   - Verify DNS does not leak.
   - Verify disconnect restores DNS/firewall.
2. Use Mac-native commands.
   - `screencapture` for screenshots.
   - `osascript` or accessibility tooling for minimal UI actions if available.
   - `scutil --dns`, `ifconfig`, `netstat -rn`, `tcpdump`.
   - `log show` if app/system events are needed.
3. Add a `post-ship-macos-verify` skill or checklist.
   - It should mirror the Windows `post-ship-mcp-verify` discipline.
   - It should produce PASS/FAIL with logs and screenshots.
4. Verification scenarios:
   - Fresh install.
   - Upgrade over previous stable.
   - Subscription connect.
   - Free configs connect if cache/pool is available.
   - Full tunnel.
   - Split tunnel with Safari/Chrome.
   - Hide/show app.
   - DNS restore after crash.

Acceptance criteria:

- Every macOS-affecting release has a local Mac verification report.
- DNS/firewall/update results are recorded, not assumed.
- A failure blocks stable cut or is explicitly called out as macOS-known-risk.

## Phase 6 - Diagnostics and Support Bundle Parity

Problem: macOS leak/debug evidence currently requires manual back-and-forth.

Plan:

1. Extend diagnostics export on macOS.
   - Include redacted `config.yaml`.
   - Include redacted `config/current.json`.
   - Include latest `vpnrouter*.log` and `singbox.log`.
   - Include:
     - `scutil --dns`
     - `networksetup -listallnetworkservices`
     - `networksetup -getdnsservers <service>` for each service
     - `ifconfig`
     - `netstat -rn`
     - `pfctl -s info`
     - `pfctl -a <vpnrouter-anchor> -sr`
2. Add one-click UI text:
   - "Export diagnostics" should explain that macOS DNS/firewall state is
     included.
3. Add redaction rules.
   - Do not leak UUID, Reality public key, short_id, subscription URLs if they
     are considered sensitive.

Acceptance criteria:

- A macOS user can send one ZIP and we can answer:
  - which DNS resolver is active,
  - whether VPNRouter altered it,
  - whether pf rules are loaded,
  - whether sing-box saw DNS,
  - whether generated config matches expected mode.

## Phase 7 - UX Honesty and Platform Docs

Problem: the README says many categories are "All" while platform protection
details are not equal.

Plan:

1. Update UI copy.
   - macOS DNS protection: show active/inactive state.
   - macOS kill-switch: show exact semantics.
   - If a setting is unsupported, disable it or label it.
2. Update docs.
   - Requirements:
     - explain sudoers/TUN.
     - explain DNS hardening behavior.
     - explain kill-switch mode.
   - Feature matrix:
     - distinguish "Core VPN" from "Leak hardening".
3. Add release notes discipline.
   - macOS fixes must mention:
     - verification performed,
     - DNS leak status,
     - package smoke status,
     - known limitations.

Acceptance criteria:

- No user can enable a macOS safety feature that is silently no-op.
- Docs describe macOS behavior accurately enough for support.

## Proposed Priority Order

1. Phase 0: evidence baseline.
2. Phase 2: macOS DNS hardening.
3. Phase 6: diagnostics export additions, because it shortens every future bug.
4. Phase 3: `MacFirewallManager` with DNS lockdown first, session kill-switch second.
5. Phase 4: shipped-package smoke workflow.
6. Phase 5: post-ship macOS verification checklist/skill.
7. Phase 1 and Phase 7 throughout, so UI and validation do not overpromise.

Rationale:

- DNS leak is the highest user-risk issue.
- Diagnostics makes all later Mac bugs cheaper.
- Firewall/kill-switch can be staged because true per-process parity may need
  product decisions.
- Update/package QA can be implemented in parallel once the workflow is kept
  `workflow_dispatch`-only until stable.

## Release Gate Proposal

Before cutting a stable release with macOS claims:

1. macOS build workflow green.
2. macOS DMG/ZIP SHA256 sidecars present and verified.
3. macOS shipped-package smoke green.
4. Mac host install/launch verified.
5. At least one connect/disconnect run verified.
6. DNS check:
   - `tcpdump -i en0 port 53` shows no normal DNS leak during protected mode,
     or release notes explicitly say DNS hardening is not complete.
7. Stop/crash cleanup verified:
   - DNS restored,
   - pf anchor flushed,
   - app relaunch does not leave stale protection state.
8. Diagnostics ZIP export verified.

## Open Questions

1. Should macOS kill-switch be global/session-level, or do we need true
   per-process semantics?
2. Is Network Extension acceptable for future per-app firewall parity, or is
   `pf` enough for the product promise?
3. Should DNS hardening be always-on while connected, or only when
   `dns_leak_lockdown` is enabled?
4. How should split tunnel treat Apple system services?
   - direct by design,
   - routed in full tunnel only,
   - optional "route Apple services through VPN" toggle.
5. Should macOS default theme follow system appearance instead of saved
   `theme: light` on first launch?

## Definition of Done

macOS is "caught up enough" with Windows in this area when:

- DNS leak behavior is measured and controlled.
- macOS has non-no-op DNS/firewall protection, or explicit UI warnings for any
  remaining limitation.
- macOS shipped packages are installed and smoke-tested in CI or release gate.
- macOS has a repeatable post-ship verification checklist.
- macOS diagnostics can explain DNS/firewall/routing state without asking the
  user for five separate manual files.
- The README and release notes no longer imply stronger macOS protection than
  actually exists.

## Related Files and Plans

- `plans/macos-olga-debug-2026-06-04.md`
- `plans/firewall-killswitch-linux-macos-2026-06-02.md`
- `plans/smoke-matrix-macos-linux-2026-06-02.md`
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs`
- `VPNRouter.Core/Platform/macOS/MacProcessScanner.cs`
- `VPNRouter.Core/Platform/macOS/MacProcessMonitor.cs`
- `VPNRouter.Core/Platform/PlatformServices.cs`
- `VPNRouter.Core/Services/WindowsDnsHardening.cs`
- `VPNRouter.Core/Services/FirewallManager.cs`
- `VPNRouter.Core/Services/LeakProtection.cs`
- `.github/workflows/build-mac.yml`
- `.github/workflows/test-windows-update.yml`
