# VPNRouter MTU end-to-end audit — 2026-08-03

Status: the research/code-audit phase was read-only. A user-requested
post-implementation verification was later run only on the fixed WINBRAT test
VM; no product code, release, tag, merge, local user configuration, or dev-box
VPN state was changed. The test VM MTU was restored to 1420.

## 1. Verdict

The useful work is narrow. VPNRouter does not need a new speculative
auto-MTU subsystem. The static audit found four contracts for repair, and the
later WINBRAT pass confirmed one additional manual-persistence defect:

1. A lower user MTU is ignored on the AWG path even though the model and the
   resolved defect ledger say it remains available for narrow paths.
2. UI persistence, validation, generation, and `config.example.yaml` expose
   four different MTU contracts.
3. IPv6 can be enabled with an interface MTU below the RFC-required 1280.
4. The Windows "auto-tune" is a conservative ICMPv4 heuristic to a fixed
   public target, not a measurement of the active proxy transport path.
5. Editing the MTU field manually changes the ViewModel but does not persist it,
   despite the settings footer claiming that autosave is active.

The minimal safe work is therefore the contract repair in draft PR #113 plus a
separate focused manual-commit persistence fix: one accepted range, an IPv6
lower-bound check, `min(user, 1420)` on the AWG TUN inbound, honest diagnostic
wording, and one save edge for the field. Android's hard-coded 1500 and any
automatic underlay-derived value remain measurement-gated.

## 2. Method, worker isolation, and limits

- Codex read the repository instructions, current code, existing MTU plans,
  `plans/OPEN-DEFECTS.md`, and `plans/refactor-backlog.md`.
- `.claude_handoff.md` is not present in this checkout. The audit did not
  search outside the checkout because the task explicitly forbids it.
- Codex collected only primary sources: RFC/IETF, Microsoft, Apple, Linux
  kernel/iproute2, WireGuard, sing-box/sing-tun source, and Android SDK docs.
- Qwen Code 0.21.3, exact model `qwen3.8-max-preview`, was the primary
  analytical worker. It received the source list below and selected checkout
  files through stdin. It had no web, shell, read, write, hook, MCP, skill, or
  chat-recording capability.
- Exact worker invocation:

  ```text
  <primary-source summary + selected checkout files> |
    qwen -p "Analyze the complete evidence on stdin. Produce the requested audit only; no tool calls." \
      -m qwen3.8-max-preview --safe-mode --approval-mode plan \
      --no-chat-recording --max-tool-calls 0 --max-wall-time 30m \
      --output-format text
  ```

- Codex rechecked every accepted finding in current code. Qwen claims that
  required runtime evidence, assumed a particular DF policy, or converted a
  deliberate safety margin into an "off-by-28" bug were downgraded or
  rejected.

Selected checkout evidence given to Qwen:

- `TunSettings.cs`, `AppSettings.cs`, `SettingsMigrator.cs`,
  `SettingsValidator.cs`, `HealthCheck.cs`, and `ConfigGenerator.cs`;
- `MainWindowViewModel.Settings.cs` and `AndroidConfigBuilder.cs`;
- `AwgDnsAndMtuTests.cs`, `MtuJumboFixTests.cs`,
  `SettingsMigratorMtuTests.cs`, and `SettingsValidatorTests.cs`.

## 3. Primary-source constraints

| Area | Primary evidence | Constraint used here |
|---|---|---|
| IPv6 | [RFC 8200 §5](https://www.rfc-editor.org/rfc/rfc8200.html#section-5) | Every IPv6 link must support an MTU of at least 1280, or supply lower-layer fragmentation/reassembly. |
| IPv4 PMTUD | [RFC 1191](https://www.rfc-editor.org/rfc/rfc1191.html) | IPv4 PMTUD depends on DF plus ICMP Fragmentation Needed. |
| IPv6 PMTUD | [RFC 8201](https://www.rfc-editor.org/rfc/rfc8201.html) | IPv6 uses Packet Too Big; filtered ICMP can create PMTU black holes. |
| Datagram PMTUD | [RFC 8899](https://www.rfc-editor.org/rfc/rfc8899.html) | A robust packetization layer accounts for lower-layer overhead, probes, and detects black holes; a fixed ICMP result alone is insufficient. |
| TCP/MSS | [RFC 9293](https://www.rfc-editor.org/rfc/rfc9293.html) | PMTUD/PLPMTUD is recommended for TCP. MSS is TCP-specific and cannot repair UDP or every IPv6 path. |
| Windows interface MTU | [Set-NetIPInterface](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipinterface?view=windowsserver2025-ps), [MIB_IPINTERFACE_ROW](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_ipinterface_row) | `NlMtuBytes` minimum is 576 for IPv4 and 1280 for IPv6; Windows IP will not transmit a larger packet through that interface. |
| Windows path state | [MIB_IPPATH_ROW](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/mib-ippath-row) | Windows has per-path `PathMtu`; it is not the same thing as one interface's configured MTU. |
| Wintun | [Wintun site](https://www.wintun.net/), [Wintun README](https://git.zx2c4.com/wintun/tree/README.md) | Wintun is an L3 adapter API and accepts packets up to 65535, but exposes no separate Wintun MTU setter. Windows IP owns the interface MTU. |
| Apple NetworkExtension | [MTU](https://developer.apple.com/documentation/networkextension/nepackettunnelnetworksettings/mtu), [tunnelOverheadBytes](https://developer.apple.com/documentation/networkextension/nepackettunnelnetworksettings/tunneloverheadbytes) | An NE packet tunnel can set MTU or derive it from physical MTU minus overhead. VPNRouter desktop macOS does not use this API; it launches sing-box/utun directly. |
| Linux | [kernel IP sysctls](https://www.kernel.org/doc/html/latest/networking/ip-sysctl.html), [ip-route(8)](https://man7.org/linux/man-pages/man8/ip-route.8.html) | PMTU and TCP probing behavior is configurable; unlocked route MTU may update through PMTUD. No one sysctl supplies a cross-platform tunnel-MTU oracle. |
| WireGuard | [wireguard-go 1420 default](https://git.zx2c4.com/wireguard-go/commit/src/tun.go?follow=1&id=dd4da93749fd9a8a231942a6b75ad137cc308e02), [wg-quick(8)](https://git.zx2c4.com/wireguard-tools/tree/src/man/wg-quick.8), [Linux MTU calculation](https://git.zx2c4.com/wireguard-tools/commit/?h=v1.0.20200820&id=48a31572f199d6c6ac9eeed1015374bb9bbf258b) | 1420 is the conventional default for a 1500-byte underlay. wg-quick can derive an MTU from the endpoint/default route; its Linux implementation subtracts 80. This does not prove every VPNRouter path has a 1500-byte underlay. |
| sing-box TUN | [official TUN docs](https://sing-box.sagernet.org/configuration/inbound/tun/) | MTU is explicit; system/gVisor/mixed stacks differ. Deprecated GSO is not a fallback design. |
| Exact desktop fork | [sing-box-lx `c7a2592e`](https://github.com/Leadaxe/sing-box-lx/blob/c7a2592e750406ade9ebaae1d0fdb7482fc0773e/protocol/tun/inbound.go) | When MTU is zero, defaults are platform-specific: 4064 under NetworkExtension, 9000 on Android, otherwise 65535. VPNRouter supplies a non-zero value. |
| Exact AWG fork | [wireguard-go-awg2-lx `0c0c10b`](https://github.com/Leadaxe/wireguard-go-awg2-lx/blob/0c0c10b5d3236796bd3832a6813223d6dc7d0bb1/device/tun.go) | The fork retains `DefaultMTU = 1420`. |
| Exact sing-tun | [sing-tun v0.8.10 system stack](https://github.com/SagerNet/sing-tun/blob/v0.8.10/stack_system.go) | The inspected handler drops fragmented IPv4 UDP packets when MF is set or the fragment offset is non-zero. This finding must not be generalized to every IP family, protocol, or outer socket. |
| Android VPN | [VpnService.Builder.setMtu](https://developer.android.com/reference/android/net/VpnService.Builder#setMtu(int)), [setUnderlyingNetworks](https://developer.android.com/reference/android/net/VpnService.Builder#setUnderlyingNetworks(android.net.Network%5B%5D) | `setMtu` accepts a positive MTU; omission uses the system default. Underlying Wi-Fi/mobile networks can change. The API does not say 1500 is universally safe. |

## 4. Current MTU data flow

| Stage | Desktop Windows/Linux/macOS | Android | Audit result |
|---|---|---|---|
| Model default | `TunSettings.DefaultMtu = 1420` (`TunSettings.cs:8`) | Core model begins at 1420 | Consistent at model entry. |
| UI | `TunMtu` defaults to 1420; warning below 1332 or above 1420 | No equivalent user MTU setting was found | Desktop presents the value as transport-agnostic. |
| Save | `MainWindowViewModel.cs:3834` clamps 576..9000 | Android storage follows its separate configuration path | Desktop permits values that generation will discard. |
| Schema migration | v5→v6: 9000→1280; v6→v7: 1500/9000→1280; v7→v8: 1500, invalid, or >1500→1420; 1280 remains 1280 | Core migrations apply before Android patching | Code and tests contradict `AppSettings.cs:40-43`, which says legacy 1280 also migrates. |
| Validation | Fatal only outside 576..65535; warnings below 1332 and above 1420 | Same Core validation can pass before platform patch | Validator does not match generator or IPv6 minimum. |
| Normalization | `NormalizeTunMtu`: <=0 or >1500 becomes 1420; 1..1500 passes | Result is later overwritten | Silent fallback hides user/config mistakes. |
| VLESS/other desktop TUN | Normalized user value | Overwritten | User lowering works on this desktop branch. |
| AWG desktop TUN | Always 1420 when `proxyIsUdpNative` | Not the Android AWG configuration surface audited here | Lower user MTU is ignored. |
| AWG endpoint | Always 1420 | N/A in the examined Android path | Endpoint is not inverted relative to the current 1420 TUN, but underlay suitability is unmeasured. |
| Stack | Windows/Linux `system`; macOS `gvisor` | Forced `gvisor` | Apple NE overhead API does not apply to desktop macOS. |
| Android patch | N/A | Every TUN inbound gets `stack=gvisor`, `mtu=1500`, `auto_route=false`; Java calls `Builder.setMtu(options.getMTU())` | The platform override is real and bypasses the Core/user MTU. Harm is measurement-gated; its explanatory comment is false. |
| Windows diagnostic | DF IPv4 ping payloads 1420..1240 to 8.8.8.8; warning reports payload+28 as approximate path MTU | Not available | This is a fixed-target heuristic, not an active-proxy transport measurement. |
| Windows apply action | Saves the successful payload, capped at 1420; refuses values below 1332 | Not available | Saving the payload supplies an intentional 28-byte cushion relative to its own IPv4 ping. It is not proven transport overhead accounting. |

### 4.1 IPv4, IPv6, fragmentation, PMTUD, and MSS

- A TUN MTU limits the inner IP packet. The outer transport then adds its own
  headers. Interface MTU, path MTU, ICMP payload, and safe inner MTU are not
  interchangeable numbers.
- The fixed Windows probe's `payload + 28` is correct for its IPv4 ICMP packet.
  Saving the raw payload is conservative by those 28 bytes; it is not a proven
  off-by-28 bug. It also is not a derivation of the active VLESS/AWG overhead.
- TCP MSS can reduce TCP segment size after the TUN MTU is installed. It does
  nothing for application UDP and is not a general answer to IPv6 PMTU.
- The exact sing-tun finding is limited to fragmented IPv4 UDP entering its
  system-stack handler. Claims about all fragmentation, outer WireGuard DF
  policy, or every platform require packet capture/runtime evidence.
- Nested VPN and mobile paths can reduce available underlay MTU, but no static
  code reading proves a particular user's underlay. Keep this measurement-gated.

## 5. Confirmed defects

### MTU-1 — AWG discards a lower user TUN MTU

Evidence:

- `TunSettings.cs:32-36` says mobile/PPPoE/nested-VPN users can explicitly use
  1400/1380.
- `ConfigGenerator.cs:48-55` says AWG TUN is `min(user, 1420)`.
- `plans/OPEN-DEFECTS.md` resolved item at line 82 says the same.
- Current `BuildInbounds` instead selects `AwgEndpointMtu` unconditionally at
  `ConfigGenerator.cs:1144-1146`.
- `AwgDnsAndMtuTests.cs:96-105` pins the contradiction: user 1200 produces 1420.

Confirmed symptom: the generic setting has no effect for AWG. Whether 1420
causes packet loss on a particular PPPoE/mobile/nested underlay is a separate
measurement-gated question.

Minimum future fix: for the AWG TUN inbound, emit
`Math.Min(NormalizeTunMtu(settings.Tun.Mtu), AwgEndpointMtu)`. Keep the endpoint
at 1420 in this minimal change; do not derive or lower it without an AWG-specific
packet test. Replace the test that asserts 1200 is ignored with lower/equal/upper
boundary tests.

### MTU-2 — UI, validation, generation, and example expose different ranges

Evidence:

- UI save: 576..9000 (`MainWindowViewModel.cs:3834`).
- UI XAML comment: 576..9000 (`NetworkPage.axaml:1958-1962`).
- validator: 576..65535 (`SettingsValidator.cs:233-236`).
- generator: values >1500 silently become 1420 (`ConfigGenerator.cs:31-34`).
- example: `config.example.yaml:77` still uses 9000.

Confirmed symptom: a value can save and validate successfully, then be silently
replaced by a different value in generated sing-box JSON. The shipped example
actively demonstrates such a value.

Minimum future fix: publish the existing generator contract 576..1500 at the
input boundary, make >1500 a validation error, align the UI clamp and example,
and retain generator normalization only as defense in depth with a diagnostic.
No new policy class is needed.

### MTU-3 — IPv6 accepts MTU below 1280

Evidence:

- `TunSettings.Ipv6Enabled` is independent of `Mtu`.
- `SettingsValidator.ValidateTun` accepts every value from 576 upward.
- RFC 8200 §5 requires at least 1280 for an IPv6 link unless lower-layer
  fragmentation/reassembly is provided; none is established by this code path.

Confirmed symptom: VPNRouter accepts a configuration outside the cross-platform
IPv6 interface contract.

Minimum future fix: when IPv6 is enabled, reject MTU below 1280. Keep the 576
lower bound only for IPv4-only configuration. Add paired validator/generator
tests; do not silently raise an explicit value.

### MTU-4 — fixed-target ICMP heuristic is presented as path auto-tune

Evidence:

- `HealthCheck.ProbePathMtuPayload` probes only IPv4 ICMP DF to `8.8.8.8`.
- It does not select the active proxy endpoint, identify whether the probe route
  is pre- or post-tunnel, test IPv6, or account for the chosen outer transport.
- RFC 8899 requires packetization-layer overhead and black-hole handling for a
  robust datagram PMTU process.

Confirmed symptom: the label and applied recommendation claim more scope than
the measurement supplies. This does not prove that its deliberately conservative
payload suggestion is harmful.

Minimum future fix: describe it as an "IPv4 DF ping safety check to 8.8.8.8" and
make the target/result semantics visible. Do not add endpoint auto-selection,
transport overhead tables, or automatic underlay tracking until controlled
measurements justify them.

### MTU-5 — manual MTU edit is not persisted

Evidence:

- `NetworkPage.axaml` binds the MTU `TextBox` directly to `TunMtu` and displays
  the unconditional `Auto-saved` footer.
- `MainWindowViewModel.cs` has no `OnTunMtuChanged` or MTU commit command;
  `SaveSettings` clamps correctly but is reached only through other actions.
- On WINBRAT, entering `1600` immediately updated the warning and visible field.
  Leaving the page did not normalize it, and an app restart loaded the previous
  `1420`, proving the manual edit had not reached storage.
- Repeating the same input and invoking the existing Start-VPN save path before
  its expected dummy-server validation failure made the next restart load
  `1500`. The same procedure made `575` reload as `576`. Therefore PR #113's
  clamp is correct once save is invoked; the missing edge is manual persistence.

Confirmed symptom: the user receives an explicit autosave success cue but loses
the manual MTU value on restart unless some unrelated action happens to call
`SaveSettings` first.

Minimum future fix: reuse an existing TextBox commit pattern and save once on a
valid focus-loss/Enter commit, not on every keystroke. Normalize both the stored
value and the displayed `TunMtu` so UI and disk agree. Do not add a settings
framework or change the fixed-target probe.

## 6. Confirmed cleanup drift, not separate runtime defects

These belong in `plans/refactor-backlog.md`, not in a new runtime architecture:

- `AppSettings.cs:40-43` says legacy 1280 migrates to 1420, while code and tests
  preserve it.
- `SettingsMigrator.cs:646-648` calls 1280 "guaranteed to traverse any path".
  RFC 8200 establishes an IPv6 minimum, not a universal end-to-end guarantee.
- `AndroidConfigBuilder.cs:445-459` says 1500 matches the Android and sing-box
  defaults. Android documents only the omitted/system-default behavior; the
  exact fork defaults to 9000 on Android when no MTU is supplied.
- Several ConfigGenerator comments say fragments/PMTUD have no fallback more
  broadly than the inspected sing-tun IPv4 UDP code supports.
- `config.example.yaml:77` is part of MTU-2 and should move to the accepted
  value/range in the same contract fix.

Do not migrate every stored 1280 to 1420: the schema has no provenance bit that
can distinguish an old migrated default from a deliberate user value. Current
v7→v8 behavior correctly preserves both rather than guessing.

## 7. Measurement-gated hypotheses

| Hypothesis | Why static code is insufficient | Minimum measurement |
|---|---|---|
| AWG 1420 fails on a given PPPoE/mobile/nested path | Underlay MTU and outer socket behavior are runtime facts. | Packet capture plus controlled 1380/1400/1420 inner UDP sizes to the real AWG endpoint, recording PTB/fragment/drop. |
| Android 1500 is harmful | `Builder.setMtu(1500)` is confirmed, but Android/gVisor/transport segmentation behavior and the device underlay are not. | Real device, Wi-Fi and cellular, IPv4/IPv6, 1280/1380/1420/1500, TCP and UDP, with packet capture or interface counters. |
| Fixed 8.8.8.8 result predicts the proxy path | Internet paths and ICMP policy can differ by destination and route state. | Compare probe, active endpoint, and application success before/after connect; include ICMP-blocked control. |
| A transport-specific overhead table can safely auto-pick MTU | VLESS/TLS/TCP, QUIC, WG, outer IPv4/IPv6, and nesting have different packetization behavior. | Controlled per-transport DPLPMTUD-style harness; no product auto-pick until repeatable. |
| IPv6 outer transport needs a lower AWG endpoint value | Header overhead differs, but current endpoint family and socket behavior must be observed. | Dual-stack AWG endpoint tests with outer IPv4 vs outer IPv6 packet capture. |

## 8. Minimal safe design decision

Do now in one focused future code task:

1. Align accepted values to 576..1500 everywhere; require >=1280 when IPv6 is
   enabled; show an explicit validation failure rather than silent correction.
2. Make AWG TUN `min(normalized user, 1420)` and keep endpoint 1420.
3. Update the example and contradictory comments in the same task.
4. Rename/scope the Windows probe honestly; preserve its conservative behavior
   unless a measurement task proves a better algorithm.

Do not do now:

- no speculative cross-platform auto-MTU service;
- no per-transport overhead constants presented as universal truth;
- no automatic migration of all 1280 values;
- no Android runtime MTU change before device measurements;
- no MSS clamping marketed as an answer for UDP/IPv6;
- no use of Apple NetworkExtension APIs as evidence for the desktop macOS path.

## 9. Test matrix

### 9.1 Static tests for the contract repair

| Test | Cases | Expected |
|---|---|---|
| UI/save range | 575, 576, 1280, 1420, 1500, 1501 | UI and validator match; >1500 is not silently accepted. |
| IPv4-only validator | 576, 1279, 1280 | 576 remains allowed; normal warnings remain intentional. |
| IPv6 validator | 1279, 1280, 1500 | 1279 fatal; 1280/1500 valid within the common maximum. |
| generic TUN generation | 576, 1280, 1380, 1420, 1500, invalid | Valid values preserved; invalid input cannot normally reach generation; fallback remains defensive. |
| AWG TUN generation | 1200, 1380, 1420, 1500 | Emits 1200, 1380, 1420, 1420 respectively; endpoint stays 1420. |
| migrations | schema 5/6/7, 1280/1500/9000/custom | Existing deterministic migrations remain pinned; deliberate 1280 stays 1280. |
| Android patch source contract | current 1500 override and corrected comment | Runtime value remains unchanged until measurement task authorizes it. |
| diagnostic wording | blocked ICMP, no successful payload, success | Fixed target/family and payload-vs-IP-MTU semantics are explicit. |

### 9.2 Live measurement matrix

| Axis | Required values |
|---|---|
| Platform | Windows system stack; Linux system stack; macOS gVisor; Android gVisor/VpnService |
| Underlay | Ethernet/Wi-Fi 1500; controlled 1492; controlled 1400/1380; nested VPN; cellular |
| Inner family | IPv4; IPv6 when enabled |
| Outer endpoint family | IPv4; IPv6 where supported |
| Transport | VLESS/Reality TCP; AWG/WireGuard UDP; Hysteria2/TUIC/QUIC where available |
| Traffic | TCP bulk; TCP short requests; UDP sizes around 1280/1328/1380/1400/1420; DNS small and large responses |
| PMTU control | ICMP/PTB allowed; ICMP/PTB filtered |
| Evidence | actual TUN/interface MTU; route/path MTU; packet sizes; PTB/fragment/drop; application result; logs |

Pass criteria must be transport-specific. A rendered page, successful ping, or
TCP request does not prove UDP PMTU correctness.

## 10. Exact prompts for next tasks

### Task 1 — repair the confirmed MTU contract

```text
Implement only the confirmed MTU contract fixes from plans/mtu-end-to-end-audit-2026-08-03.md. Do not add auto-MTU.

Required behavior:
1. Make the accepted configured TUN MTU range 576..1500 consistently in MainWindowViewModel save/UI guidance, SettingsValidator, ConfigGenerator defense-in-depth handling, and config.example.yaml.
2. If Tun.Ipv6Enabled is true, SettingsValidator must reject MTU below 1280.
3. For an AWG endpoint, emit TUN MTU = min(NormalizeTunMtu(settings.Tun.Mtu), ConfigGenerator.AwgEndpointMtu); keep the AWG endpoint MTU itself at 1420.
4. Replace the AwgDnsAndMtu test that pins "1200 is ignored" with lower/equal/upper boundary cases.
5. Correct the stale AppSettings/SettingsMigrator/ConfigGenerator/AndroidConfigBuilder MTU comments identified in the audit, but do not change Android runtime MTU.
6. Rename or rewrite Windows MTU diagnostic text so it explicitly says it is an IPv4 DF ping to 8.8.8.8 and distinguishes ping payload from approximate IP path MTU. Preserve the current algorithm and the below-1332 safety refusal in this task.

Before editing, re-read current AGENTS/CLAUDE instructions and trace every Tun.Mtu caller. Add the smallest tests that pin these contracts. Build and run the targeted MTU, validator, migrator, generator, and health-check tests. Product code changes require the normal branch/review/CI lifecycle; do not release or deploy.
```

### Task 2 — measure Android and narrow underlays before changing policy

```text
Run the measurement-gated MTU study from plans/mtu-end-to-end-audit-2026-08-03.md; do not edit product code first.

On a real Android test device and controlled desktop underlays, record actual interface/route MTU and packet evidence for 1280, 1380, 1400, 1420, and 1500. Cover Wi-Fi, cellular if available, a 1492 underlay, a 1400/1380 underlay, and a nested VPN. Test inner IPv4/IPv6, VLESS/Reality TCP, AWG UDP, and QUIC-based transports where configured. Include ICMP/PTB allowed and blocked controls. For Android, confirm whether VpnService.Builder.setMtu(1500) is preserved, clamped, fragmented, or black-holed by the device/gVisor/outer transport.

Return raw commands, packet/counter evidence, and a result table. Separate observed behavior from inference. Recommend an Android or endpoint-MTU change only if at least one repeatable failure is removed by a specific lower value without regressing the Steam-SDR 1328-byte inner-IP case. Do not implement or ship from this task.
```

### Task 3 — only if a robust automatic detector is still desired

```text
Design, but do not implement, an endpoint-specific MTU measurement only after Task 2 supplies repeatable evidence. Use RFC 8899 as the protocol constraint. Define exactly which packetization layer is measured, active endpoint/family selection, transport overhead ownership, connected/disconnected route state, ICMP-blocked behavior, black-hole detection, rollback, and per-platform capability. Prove why the design is safer than the current fixed-target advisory. Reject the feature if the proof requires guessed overhead tables or cannot be tested on Windows, macOS, Linux, and Android.
```

### Task 4 — repair the confirmed manual MTU persistence defect

```text
Fix only MTU-5 from plans/mtu-end-to-end-audit-2026-08-03.md. Manual edits of the Leak Protection TunMtu TextBox currently update the ViewModel/warning but are lost on restart while the footer says Auto-saved.

Trace the repository's existing TextBox commit/save patterns first. Persist a valid MTU once on focus loss or Enter after the binding commits; do not call SaveSettings on every digit and do not add a generic settings abstraction. Reuse the existing IPv4/IPv6 bounds, set the displayed TunMtu to the normalized stored value, and leave AutoTuneMtu plus Apply/reconnect semantics unchanged.

Add the smallest regression that proves manual 1200 survives reload, 575 becomes 576, 1600 becomes 1500, and IPv6 1279 becomes 1280. Re-run the focused MTU tests and a WINBRAT UIA pass that edits the field, leaves/reopens the page, restarts the app, and confirms persistence. Do not release, tag, merge, or touch the dev-box VPN.
```
