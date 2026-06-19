# Independent review: server health, failover and TUN MTU

Date: 2026-06-19

Reviewed files:

1. `plans/server-health-failover-backlog-2026-06-19.md`
2. `plans/mtu-default-9000-research-2026-06-19.md`

Diagnostic bundles:

- `C:\Project\logs\VPNRouter-diagnostics-20260619-205004.zip`
- `C:\Project\logs\VPNRouter-diagnostics-20260619-214717.zip`

Review method:

- the diagnostic archives were extracted and their sing-box logs were counted
  independently rather than relying on figures stated in the plans;
- `current.redacted.json`, `config.redacted.yaml` and VPNRouter application logs
  were cross-checked;
- relevant VPNRouter source code was inspected;
- claims about sing-box were checked against sing-box 1.13.13 source and current
  official documentation;
- current v2rayN and v2rayNG source was inspected for competing failover
  functionality.

## Executive conclusion

The diagnosis is only partly confirmed.

The mass VLESS `EOF` failures are real and the counts in the server-health plan
are accurate. The claim that the second bundle contains a storm of 737 resets of
established Reality connections is false: 733 of those messages describe the
local upload/TUN side closing. Only four reset messages reference the outer TCP
connection to `104.194.156.93:443`.

The available evidence makes a client routing, DNS or QUIC configuration defect
unlikely. It does not, however, uniquely identify the server as the cause. The
remaining candidates include server/node capacity or limits, the network path,
ISP/DPI interference and endpoint-specific behaviour.

The failover backlog is implementable, but stages B and C must not use the
proposed naive `(EOF + RST) / minute` signal. The preferred live source is the
Clash API `/logs` stream, not filesystem tailing.

The MTU research note contains two important factual errors:

- 9000 is not the universal current sing-box TUN default;
- the system-stack TCP path terminates and re-originates TCP rather than using
  sing-box MSS clamping.

VPNRouter should keep 1280 as its production default until controlled tests
justify another value.

## A. Diagnostic validation

### A1. First bundle: 205004

Status: Confirmed

Independent full-file count:

- exact `using outbound/vless[proxy]: EOF`: 1587;
- `forcibly closed by the remote host`: 206;
- EOF duration median: 109 ms;
- EOF duration p90: approximately 804 ms;
- 1280 of 1587 EOF events completed in less than 200 ms.

EOF destinations:

| Destination group | Count |
|---|---:|
| Roblox `128.116.*` | 868 |
| Telegram `149.154.*` | 113 |
| Cloudflare `172.64.*` | 6 |
| Cloudflare `104.18.*` | 7 |

Top exact destinations:

| Address | EOF count |
|---|---:|
| `128.116.44.3` | 703 |
| `185.253.32.19` | 122 |
| `128.116.21.3` | 96 |
| `149.154.167.222` | 79 |
| `52.168.117.168` | 49 |
| `128.116.31.3` | 35 |
| `128.116.13.3` | 31 |

The log maps `128.116.44.3` to the Roblox Frankfurt endpoint:

- extracted `205004\singbox-tail.log:2`
- extracted `205004\singbox-tail.log:3`

The plan's 868 Roblox count is correct.

The 206 reset messages do not support an outer Reality reset diagnosis:

- 184 are upload-side raw reads on the local TUN tuple
  `172.19.0.1 -> 172.19.0.2`;
- 22 are download-side messages;
- zero reference `104.194.156.93:443`.

### A2. Second bundle: 214717

Status: Confirmed for totals; Refuted for RST interpretation

Independent full-file count:

- exact `using outbound/vless[proxy]: EOF`: 1952;
- `forcibly closed by the remote host`: 737;
- EOF duration median: 96 ms;
- EOF duration p90: approximately 315 ms;
- 1670 of 1952 EOF events completed in less than 200 ms.

EOF destinations:

| Destination group | Count |
|---|---:|
| Anthropic `160.79.*` | 656 |
| ninitux `83.97.*` | 546 |
| Telegram `149.154.*` | 239 |
| Cloudflare `172.64.*` | 93 |
| Cloudflare `104.18.*` | 67 |

Top exact destinations:

| Address | EOF count |
|---|---:|
| `160.79.104.10` | 656 |
| `83.97.108.34` | 546 |
| `149.154.167.222` | 191 |
| `172.64.155.209` | 81 |
| `104.18.32.47` | 60 |

DNS records in the log associate:

- `160.79.104.10` with `api.anthropic.com`, `claude.ai` and related Anthropic
  services;
- `83.97.108.34` with `ninitux.com`;
- `172.64.155.209` and `104.18.32.47` with ChatGPT/Cloudflare destinations.

RST classification:

| RST form | Count |
|---|---:|
| `connection upload closed` | 733 |
| `connection download closed` | 2 |
| `open connection ...` | 2 |
| References outer proxy `104.194.156.93:443` | 4 |

The only four messages that directly reference the outer proxy TCP connection
are:

- extracted `214717\singbox-tail.log:34425`
- extracted `214717\singbox-tail.log:34520`
- extracted `214717\singbox-tail.log:34757`
- extracted `214717\singbox-tail.log:38834`

Consequently, the plan's raw count of 737 is correct, but the claim that all 737
events are resets of established Reality connections is refuted.

### A3. Client configuration

Status: Confirmed as internally coherent

First bundle, split mode:

- `config.redacted.yaml:5`: `routing_mode: split`;
- `config.redacted.yaml:108`: node server `104.194.156.93`;
- `config.redacted.yaml:315`: strict DNS enabled;
- `config.redacted.yaml:400`: active server `chachkamuti`;
- `config.redacted.yaml:405`: TUN MTU 1280;
- `current.redacted.json:10-14`: VPN DNS detours through `proxy`;
- `current.redacted.json:109-110`: proxy outbound and node address;
- `current.redacted.json:160-161`: QUIC rejection is process-scoped;
- `current.redacted.json:284`: route final is `direct`.

Second bundle, full-tunnel mode:

- `config.redacted.yaml:5`: `routing_mode: full`;
- `config.redacted.yaml:87`: node server `104.194.156.93`;
- `config.redacted.yaml:299`: strict DNS enabled;
- `config.redacted.yaml:349`: custom TUN MTU 1337;
- `current.redacted.json:10-15`: VPN DNS detours through `proxy`;
- `current.redacted.json:57-58`: proxy outbound and node address;
- `current.redacted.json:121`: global QUIC rejection;
- `current.redacted.json:125`: route final is `proxy`.

Both bundles therefore use the same node IP, `104.194.156.93`, despite different
display names/config snapshots.

No route inversion, missing proxy outbound, DNS detour error or incorrect QUIC
scope was found.

### A4. Server-side root cause

Status: Uncertain

The evidence supports the following narrower statement:

> New connections routed through the VLESS outbound frequently fail while
> opening the relay. DNS, route final and QUIC-reject configuration are
> internally consistent.

The evidence does not prove:

> The server itself, rather than the client-to-server path or an intermediary,
> is definitely responsible.

The rapid approximately 100 ms EOF pattern across unrelated destinations is
consistent with the VLESS endpoint or its path rejecting relay creation. It is
not sufficient to distinguish:

- server CPU, file-descriptor, conntrack or connection/stream limits;
- provider-side policy;
- ISP/DPI or another middlebox;
- a path-specific transport failure.

Server logs or simultaneous client/server packet captures are required for a
definitive localization.

### A5. Split works, full tunnel fails

Status: Uncertain but plausible

The application log shows:

- Roblox processes detected and added around
  `vpnrouter20260619_001.log:526`;
- transition to full-tunnel mode at
  `vpnrouter20260619_001.log:796`;
- Roblox VLESS EOF events immediately afterward at lines 897 and 914;
- another full-tunnel startup at line 935.

This supports a load-sensitive hypothesis:

- split mode routes fewer processes through the TCP-only VLESS endpoint;
- full-tunnel mode routes all eligible traffic through it;
- Roblox creates a large number of short concurrent connections;
- the same node then emits a high rate of relay-open EOF failures.

This is correlation, not proof of a server overload mechanism.

### A6. Node overload versus DPI

Status: Uncertain; node/path capacity is currently more likely than the stated
DPI theory

The DPI argument in the plan relies heavily on the 737 reset count. That
argument collapses after classifying the reset endpoints: only four reset
messages directly involve the outer proxy connection.

A useful controlled test matrix is:

1. Same client, access network and protocol; change only the node.
2. Same node and protocol; change only the access network, for example ISP
   versus mobile hotspot.
3. Same node/provider and access network; compare Reality/TCP with an available
   UDP protocol and, where possible, a different port/SNI.
4. Ramp concurrent connections while recording server CPU, memory, open file
   descriptors, conntrack and server-side core logs.
5. Capture packets simultaneously at client and server.

An RST seen at the client but absent at the server, or inconsistent TTL/path
evidence, would support a middlebox hypothesis. A matching server-originated
close combined with resource pressure or explicit server logs would support
node overload.

## B. sing-box and ecosystem facts

### B1. Current default TUN MTU

Status: Refuted

VPNRouter bundles sing-box 1.13.13:

- `build.ps1:48`

In sing-box 1.13.13, when `mtu` is omitted:

- Apple NetworkExtension uses 4064;
- Android uses 9000;
- other platforms use 65535.

Primary source:

- <https://github.com/SagerNet/sing-box/blob/v1.13.13/protocol/tun/inbound.go>

The current documentation includes 9000 in its example configuration, but the
field documentation does not declare it as the universal default:

- <https://sing-box.sagernet.org/configuration/inbound/tun/>

The statement "the default sing-box TUN MTU is 9000" is therefore historically
or contextually true for some versions/platforms, but false for the version
currently bundled by VPNRouter as a universal claim.

### B2. 65535 to ENOBUFS to 9000 rationale

Status: Confirmed for Android

The sing-box source explicitly states that some Android devices report
`ENOBUFS` with MTU 65535 and therefore selects 9000 on Android.

This must not be generalized to Windows, Linux and macOS defaults.

### B3. TCP MSS clamping

Status: Refuted

No sing-box or sing-tun implementation of the claimed MSS-clamping path was
found.

For `stack = "system"`, sing-tun accepts a local TCP connection and forwards the
accepted stream to the handler:

- <https://github.com/SagerNet/sing-tun/blob/v0.8.10/stack_system.go>
- `stack_system.go`, `acceptLoop`, `listener.Accept`,
  `handler.NewConnectionEx`.

The accurate description is:

> The TUN/system stack terminates the application's TCP flow locally. sing-box
> then establishes a separate outbound proxy connection, so TCP is
> re-segmented by the outbound stack.

That is not the same operation as rewriting/clamping the MSS option in forwarded
TCP SYN packets.

The comments in:

- `VPNRouter.Core/Models/AppSettings.cs:1170-1177`
- `VPNRouter.Core/Services/SettingsMigrator.cs:635-645`

overstate the mechanism and the degree of proof. The observed real-user
correlation may justify the conservative 1280 default, but does not prove that
9000-byte HTTP/2 packets were forwarded unchanged onto the physical path.

### B4. GSO

Status: Confirmed only for a narrow upstream path; not applicable to current
VPNRouter configuration

sing-box 1.13.13 enables its internal GSO condition only when:

- the operating system is Linux;
- TUN stack is `gvisor`;
- there is no platform interface;
- explicit MTU is greater than zero and below 49152.

VPNRouter always emits:

- `VPNRouter.Core/Services/ConfigGenerator.cs:1022`: `Stack = "system"`.

The official documentation also says the exposed `gso` option is deprecated,
has no advantage in transparent proxy scenarios and no longer works:

- <https://sing-box.sagernet.org/configuration/inbound/tun/>

GSO therefore cannot be used as the explanation for how VPNRouter's current
Windows/system-stack configuration handles large UDP datagrams.

### B5. QUIC PMTU discovery

Status: Confirmed

The quic-go dependency used by sing-box states:

- path MTU discovery normally finds the path MTU;
- setting the initial packet size too high can make the QUIC handshake time out;
- values below 1200 are invalid.

Local dependency source:

- `C:\Users\x3d_mutant\go\pkg\mod\github.com\sagernet\quic-go@v0.59.0-sing-box-mod.4\interface.go:160-169`

Its default initial packet size is 1280:

- `C:\Users\x3d_mutant\go\pkg\mod\github.com\sagernet\quic-go@v0.59.0-sing-box-mod.4\internal\protocol\params.go:11-12`

Standards reference:

- <https://www.rfc-editor.org/rfc/rfc8899.html>

### B6. VPNRouter MTU

Status: Confirmed

VPNRouter:

- defaults to MTU 1280 in
  `VPNRouter.Core/Models/AppSettings.cs:1179`;
- passes the setting to sing-box in
  `VPNRouter.Core/Services/ConfigGenerator.cs:1015`;
- migrates the exact previous default 9000 to 1280 in
  `VPNRouter.Core/Services/SettingsMigrator.cs:649-651`.

The first diagnostic bundle uses 1280. The second uses a custom 1337.

### B7. "1280 traverses any path"

Status: Refuted as an absolute claim

1280 is the IPv6 minimum link MTU. RFC 8200 requires links carrying IPv6 to
support it directly or provide lower-layer fragmentation/reassembly:

- <https://www.rfc-editor.org/rfc/rfc8200.html#section-5>

This does not establish a universal guarantee for every IPv4, encapsulated or
misconfigured path. IPv4 may have lower path MTUs and different fragmentation
semantics.

The safer wording is:

> 1280 is a conservative value that materially reduces PMTU and fragmentation
> risk and aligns with the IPv6 minimum MTU.

### B8. Relationship between MTU and the reviewed EOF failures

Status: Refuted as a likely cause for these bundles

The reviewed sessions use MTU 1280 and 1337, not 9000. Most relay EOF failures
occur around 100 ms after connection creation and affect many unrelated
destinations.

This pattern is inconsistent with a failure that requires a large HTTP/2 or UDP
payload to encounter a PMTU blackhole. There is no positive MTU evidence in
these bundles.

### B9. Raising the default to 1420 or 1500

Status: Uncertain

No benchmark or controlled compatibility data accompanies the recommendation.
Raising the value may reduce local packet and syscall overhead, but could also
reintroduce failures on paths that motivated the 1280 migration.

Required validation before changing the default:

- HTTP/1.1 and HTTP/2;
- large uploads and downloads;
- QUIC-capable and TCP-only proxy protocols;
- Windows, macOS, Linux and Android;
- Ethernet, Wi-Fi, PPPoE, mobile and nested VPN paths;
- paths with blocked or broken ICMP PMTU feedback;
- throughput, CPU and connection reliability at 1280, 1420 and 1500.

Until those tests exist, keep 1280 as the default and treat higher values as an
experimental advanced option.

### B10. sing-box urltest semantics

Status: Confirmed with qualification

Official defaults:

- URL: `https://www.gstatic.com/generate_204`;
- interval: 3 minutes;
- tolerance: 50 ms.

Source:

- <https://sing-box.sagernet.org/configuration/outbound/urltest/>

The implementation runs an HTTP URL test through each outbound, records delay
for successful probes and deletes URL-test history for failed probes:

- <https://github.com/SagerNet/sing-box/blob/v1.13.13/protocol/group/urltest.go>

Thus `urltest` evaluates probe reachability and latency. It can reject a dead
node when the probe itself fails. It does not observe the error rate of arbitrary
real user traffic and can miss a node that serves a lightweight 204 probe while
dropping connections under load.

VPNRouter currently emits tolerance 150 ms, not 50 ms:

- `VPNRouter.Core/Services/ConfigGenerator.cs:1227-1230`.

### B11. Clash API error visibility

Status: Refuted as stated

The `/connections` endpoint exposes current connections and aggregate traffic,
but no close/error reason for connections that have already disappeared:

- `VPNRouter.Core/Services/ClashSingBoxApi.cs:223-262`
- `VPNRouter.Core/Services/ClashSingBoxApi.cs:481-491`

However, sing-box's Clash API also exposes `/logs`. The implementation supports
streamed HTTP output and WebSocket output and sends each log entry's type and
payload:

- <https://github.com/SagerNet/sing-box/blob/v1.13.13/experimental/clashapi/server.go>

Recommended primary source for stages B/C:

1. subscribe to Clash API `/logs` over WebSocket;
2. parse structured `{ type, payload }` messages;
3. reconnect with backoff;
4. use the log file only as fallback/postmortem input.

This avoids filesystem rotation, encoding and partial-line races. The diagnostic
ZIP application logs are UTF-8 even though live project logs may be UTF-16LE,
which further favours the API stream.

### B12. Competitor failover claims

Status: Refuted in broad form; Uncertain in the narrow passive-error form

Current v2rayN and v2rayNG source contains:

- policy groups;
- `leastPing`;
- `leastLoad`;
- round-robin/random balancing;
- periodic observatory/burst-observatory probes;
- fallback-related configuration.

Current sources:

- <https://github.com/2dust/v2rayN/blob/master/v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayBalancerService.cs>
- <https://github.com/2dust/v2rayNG/blob/master/V2rayNG/app/src/main/java/com/v2ray/ang/core/CoreConfigManager.kt>

Therefore the claim that these clients only support manual selection and simple
ping is false.

What was not found is a client-level passive controller that counts real
traffic EOF/reset rates and immediately penalizes or changes the active node.
That narrower statement remains plausible but cannot be converted into the
categorical claim that no competitor implements auto-failover.

For archived Nekoray, node auto-test/switch remained a requested feature:

- <https://github.com/MatsuriDayo/nekoray/issues/417>

VPNRouter's differentiator should be stated narrowly as:

> Passive runtime failure-rate detection from real traffic, integrated with
> node penalty, cooldown and failover.

## C. VPNRouter implementation validation

### C1. urltest tagged as proxy and LeakProtection

Status: Confirmed

LeakProtection first checks only that an outbound tagged `proxy` exists:

- `VPNRouter.Core/Services/LeakProtection.cs:284-287`.

It then recognizes a `urltest` proxy and validates its child references and
concrete child protocols:

- `VPNRouter.Core/Services/LeakProtection.cs:299-345`.

Custom mode also explicitly accepts selector/urltest groups without their own
server field:

- `VPNRouter.Core/Services/LeakProtection.cs:607-611`.

A valid `urltest` outbound tagged `proxy` therefore passes LeakProtection.

### C2. Existing urltest support

Status: Confirmed; important missing context in the plan

`ConfigGenerator.AddOutboundGroup` already implements:

- one server: a concrete outbound;
- multiple servers: child outbounds plus a `urltest` wrapper;
- `generate_204`;
- 3-minute interval;
- tolerance 150;
- no interruption of existing inbound connections.

Source:

- `VPNRouter.Core/Services/ConfigGenerator.cs:1191-1231`.

The missing behaviour is primarily server-pool selection. `GetActiveServers`
currently returns only the selected server and same-IP companions:

- `VPNRouter.Core/Models/AppSettings.cs:839-860`.

Option A is therefore not a greenfield urltest implementation.

### C3. QUIC reject scope

Status: Confirmed

`ConfigGenerator.BuildRoute` emits:

- global QUIC rejection for full-tunnel and exclude modes;
- `process_name`-scoped QUIC rejection for split include mode.

Source:

- `VPNRouter.Core/Services/ConfigGenerator.cs:1659-1675`.

### C4. FindNaiveUdpSibling same-host restriction

Status: Refuted

`ConfigGenerator.FindNaiveUdpSibling` delegates to:

- `VPNRouter.Core/Services/NaivePairing.cs:44-71`.

The pairing algorithm uses:

1. matching `PairGroup`;
2. an unambiguous stripped display-name fallback.

It does not compare server address or hostname.

The same-IP restriction exists elsewhere in `GetActiveServers`:

- `VPNRouter.Core/Models/AppSettings.cs:858-860`.

If option D searches the complete subscription pool, the current pairing helper
can return another host. Even a matching hostname is not a sufficient exit-IP
guarantee when TCP and UDP protocols terminate on different backends.

Required safety conditions for D:

- explicit provider pair identity such as `PairGroup`;
- exact endpoint/host policy where appropriate;
- ideally verified common exit identity;
- no name-only fallback for cross-pool routing decisions.

### C5. Existing failover infrastructure

Status: Confirmed; the plan underuses it

VPNRouter already has `AutoFailoverEngine`, which:

- selects another server;
- tracks attempts;
- persists the active server;
- invokes a restart delegate;
- surfaces user-facing failures.

Sources:

- `VPNRouter.Core/Services/AutoFailoverEngine.cs:78-225`
- `VPNRouter.Core/Services/VpnEngine.cs:1102-1145`

The current post-start health decision runs two Clash delay probes through
`proxy`:

- `VPNRouter.Core/Services/ConfigSanityCheck.cs:23-36`.

It detects a dead probe endpoint/config, not a high error rate in arbitrary
runtime traffic. Stage B should feed a new runtime health state into the
existing failover engine rather than create a second server-cycling subsystem.

## D. Estimate review

### Option A: urltest over subscription servers

Plan estimate: 1-2 days

Verdict: Plausible but slightly optimistic

Reason:

- the core urltest generator and validators already exist;
- server-pool selection, UI option, migration and tests remain;
- mixed-protocol and exit-IP semantics need explicit design.

Suggested estimate: 2-3 days.

### Stage C: warning only

Plan estimate: approximately 1 day

Verdict: Underestimated for production quality

A one-day prototype could tail text and show a toast, but it would reproduce the
RST misclassification found in this review unless the signal is designed
carefully.

Production work includes:

- `/logs` stream client;
- cancellation and reconnect;
- message classification;
- connection-id and endpoint correlation;
- denominator definition;
- rolling windows;
- startup/reconnect grace periods;
- debounce and cooldown;
- tests based on both diagnostic fixtures;
- UI and localization.

Suggested estimate: 2-4 days for warning-only production behaviour.

### Stage B: warning plus auto-failover

Plan estimate: 3-5 days

Verdict: Optimistic

Existing `AutoFailoverEngine` reduces implementation cost, but production
failover still requires:

- a trustworthy health classifier;
- anti-flapping state;
- node penalty and cooldown;
- recovery probing;
- selection policy;
- persistence semantics;
- handling of custom/generated modes;
- restart versus in-core selection decisions;
- end-to-end tests.

Suggested estimate: 5-8 days after telemetry and warning logic are calibrated.

### Option D: UDP sibling

Plan estimate: 1-2 days

Verdict: Optimistic and currently blocked by identity semantics

The routing change is small, but safe implementation requires:

- hard pairing rules;
- cross-protocol config tests;
- QUIC-reject interaction tests;
- DNS/TCP/UDP exit consistency checks;
- behaviour when the UDP sibling fails independently.

Suggested estimate: 2-4 days after the pairing contract is defined.

The current subscription does not contain an immediately useful same-node
Germany TCP/UDP pair, so this option has limited short-term value.

## E. Risks missing from the plans

### E1. Reset direction and endpoint must be classified

`forcibly closed` cannot be counted without determining:

- whether the error is on upload or download;
- whether it references the local TUN tuple;
- whether it references the outer proxy;
- whether it references a direct destination;
- whether it is attached to relay creation or later stream teardown.

### E2. A percentage requires a valid denominator

The plans mention an EOF percentage but do not define the population.

Possible denominators have different meanings:

- all attempted outbound relay opens;
- unique connection IDs;
- active Clash connections;
- all logged connections including direct traffic;
- per-process or per-destination connection attempts.

`/connections` alone is unsuitable because short failed connections can vanish
before polling and no close reason remains.

### E3. Normal application closes are noisy

Games, browsers, Happy Eyeballs, speculative connections and cancelled requests
can create benign EOF/reset-like patterns. A raw absolute threshold can warn on
normal behaviour.

The health model needs:

- per-node attribution;
- relay-open failures distinguished from later application closes;
- minimum sample size;
- sustained-window requirements;
- comparison with a baseline or successful opens.

### E4. DNS, TCP and UDP may diverge across nodes

An independent urltest selection for multiple groups can produce:

- TCP through node A;
- UDP through node B;
- DNS through whichever group is selected at that moment.

This can cause login/session, geo, fraud-detection and IP-consistency failures.
Selection should operate on a logical node bundle where possible.

### E5. Changing a group does not repair existing connections

VPNRouter currently sets `interrupt_exist_connections = false`. A new urltest
selection affects new connections. Existing broken or long-lived connections
may remain broken until applications reconnect or VPNRouter intentionally
interrupts them.

### E6. Probe success is not service success

A node may successfully fetch a 204 URL while:

- refusing high concurrency;
- throttling selected destinations;
- failing after a particular byte count;
- dropping only long-lived streams;
- failing UDP while TCP succeeds.

`urltest` is useful for reachability and latency, but does not replace runtime
telemetry.

### E7. Toasting before calibration creates false authority

A warning that says the proxy is dropping traffic may be more harmful than
silence if the classifier is still counting local application closes. The
diagnostic bundles themselves demonstrate this risk.

The first telemetry release should be observe-only and log its classification
without user-facing blame.

### E8. Current code comments overstate the MTU diagnosis

The MTU comments assert:

- oversized HTTP/2 segments were placed unchanged on the wire;
- 1280 traverses any path;
- the mechanism was confirmed.

The observed incident supports retaining a conservative default, but the
specific packet mechanism was not established by the sources reviewed here.
These comments should eventually be rewritten as an evidence-based operational
rationale rather than a proven packet trace.

## F. Recommended implementation order

The plan's `C -> A -> B -> D` sequence is not recommended.

### F1. B0: passive telemetry, no warning or switching

Implement a live `/logs` subscriber and classify:

- VLESS relay-open `EOF`;
- actual outer-proxy TCP resets;
- local TUN/application-side closes;
- destination and connection id;
- successful versus failed relay opens where observable.

Use both supplied logs as regression fixtures.

Acceptance:

- the second fixture must report 1952 relay-open EOF;
- it must not report all 737 resets as outer-proxy failures;
- only the four messages naming `104.194.156.93:443` may enter the outer-proxy
  reset category;
- no toast or failover is emitted.

### F2. A: opt-in multi-server urltest

This is the safest first user-facing feature.

Reuse `AddOutboundGroup`, but define:

- eligible server pool;
- protocol compatibility;
- node bundle/exit-IP rules;
- UI opt-in;
- behaviour for existing connections;
- DNS and UDP group consistency.

### F3. C: calibrated warning

Enable a warning only after B0 produces enough normal and failure data.

The warning should be neutral:

> The active proxy is showing an unusually high connection-open failure rate.
> Try another server or reduce full-tunnel traffic.

It should not claim DPI or server overload.

### F4. B: auto-failover

Feed a sustained bad-node state into the existing `AutoFailoverEngine`.

Minimum controls:

- minimum attempt/sample count;
- multiple consecutive bad windows;
- startup grace period;
- cooldown after switching;
- maximum switches per time interval;
- penalty expiry and recovery probe;
- visible reason and selected replacement;
- no silent swap of custom JSON configurations.

### F5. D: paired UDP path

Implement only after a strict node-pair identity contract exists and a real
provider/subscription supplies useful TCP/UDP pairs.

## G. Final verdict

### Should the work be implemented?

Yes, with corrections.

The diagnostic evidence demonstrates a real reliability problem that simple
single-node routing does not handle well. Multi-node reachability selection and
runtime failure telemetry are worthwhile.

### Which option should start first?

For internal engineering, start with B0 telemetry.

For the first user-facing capability, start with option A, opt-in urltest over a
carefully defined server pool.

Do not start with the current stage C toast because its proposed signal
misclassifies most reset messages in the supplied failure bundle.

### MTU decision

Keep 1280 as the production default.

Do not change it to 1420 or 1500 without controlled cross-platform and
cross-network tests. Rewrite the research note to distinguish:

- Android's 9000 fallback;
- desktop sing-box's current 65535 default;
- TCP termination/re-origination from MSS clamping;
- deprecated GSO configuration from QUIC PMTU discovery;
- operational correlation from proven packet-level causation.

## H. Required corrections to the two plans

### `server-health-failover-backlog-2026-06-19.md`

1. Replace the categorical server-side root-cause statement with a bounded
   diagnosis.
2. Keep the verified EOF counts.
3. Reclassify the 737 reset messages and state that only four directly reference
   the outer proxy.
4. Replace filesystem log tailing as the primary signal with Clash API `/logs`.
5. Do not use raw `(EOF + RST) / minute`.
6. Document existing `AutoFailoverEngine` and urltest generation.
7. Remove the claim that `FindNaiveUdpSibling` is same-host.
8. Narrow the competitor claim to passive real-traffic error-rate failover.
9. Replace `C -> A -> B -> D` with `B0 -> A -> C -> B -> D`.
10. Increase the production estimates for C, B and D.

### `mtu-default-9000-research-2026-06-19.md`

1. State the platform-dependent sing-box 1.13.13 defaults.
2. Limit the ENOBUFS rationale to Android.
3. Replace MSS-clamping language with TCP termination and re-origination.
4. State that VPNRouter's system stack does not use the cited gVisor/Linux GSO
   path.
5. Retain QUIC PMTU discovery, including the dependency's initial size of 1280.
6. Replace "1280 traverses any path" with a conservative-risk statement.
7. Mark the 1420/1500 proposal as unvalidated experimental research.
8. State that the reviewed EOF incident provides no evidence of an MTU cause.
