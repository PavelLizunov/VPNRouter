# Iteration A — Core runtime raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category: `core-runtime`
Status: unverified swarm output; no item below is accepted until lead source verification.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| CR-1 | 3/3 | correctness; security/fail-closed/lifetime; tests/schema/upstream | 11 | 10 |
| CR-2 | 3/3 | correctness; security/fail-closed/lifetime; tests/schema/upstream | 9 | 9 |
| CR-3 | 3/3 | correctness; security/fail-closed/lifetime; tests/schema/upstream | 7 | 5 |
| CR-4 | 3/3 | correctness; security/fail-closed/lifetime; tests/schema/upstream | 15 | 11 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Reporters |
|---|---|---|---|---|
| CR-1-1 | P1 | Non-TCP VLESS transports retain Vision flow | `ConfigGenerator.cs:1599-1601` | correctness |
| CR-1-2 | P1 | Null custom-rule action/type can throw | `ConfigGenerator.cs:369,387,560` | correctness |
| CR-1-3 | P1 | Missing config sections can crash validation | `LeakProtection.cs:183,187,202,211` | correctness |
| CR-1-4 | P1 | Exclude custom mode may select proxy DNS when no local DNS exists | `CustomConfigInjector.cs:155,834-848,1282-1306` | correctness; security |
| CR-1-5 | P2 | Empty `VpnDns` may emit an unusable server | `ConfigGenerator.cs:2122,2138,2148` | correctness |
| CR-1-6 | P1 | IPv6-enabled DNS strategy may conflict with validation | `ConfigGenerator.cs:899`; `LeakProtection.cs:183`; `ConfigPipeline.cs:137-164` | security |
| CR-1-7 | P1 | Global QUIC rejection may precede excluded-process direct routing | `ConfigGenerator.cs:1990-2049` | schema/upstream |
| CR-1-8 | P2 | Endpoint tags may be omitted from urltest-child validation | `LeakProtection.cs:370-373` | schema/upstream |
| CR-1-9 | P1 | Trailing-dot LAN suffix may bypass public-TLD denial | `ConfigGenerator.cs:947-954` | schema/upstream |
| CR-1-10 | P2 | Single-child selector may produce one-child urltest | `CustomConfigInjector.cs:288,332`; `LeakProtection.cs:367` | schema/upstream |
| CR-2-1 | P1 | Failover object may retain stale settings across reconnects | `VpnEngine.cs:1625`; `AutoFailoverEngine.cs:35` | correctness |
| CR-2-2 | P1 | Faulted queued TUN-removal task may poison later operations | `SingBoxManager.Lifecycle.cs:393,429` | correctness |
| CR-2-3 | P1 | Warmup probe may inherit a caller-scoped cancellation token | `StartupPipeline.cs:1182,1205,1305` | correctness |
| CR-2-4 | P1 | Partial-start disposal may retain TUN ownership | `VpnEngine.cs:990-1010` | security |
| CR-2-5 | P1 | Intentional Unix exit code 0 may be classified as crash | `SingBoxManager.CrashDetect.cs:64-80` | security |
| CR-2-6 | P1 | Failed forced restart may leave firewall fail-open | `VpnEngine.cs:817-825`; `SingBoxManager.Lifecycle.cs:480-517` | security |
| CR-2-7 | P1 | Failed failover candidate may be retried indefinitely | `AutoFailoverEngine.cs:180-224,303-305` | tests |
| CR-2-8 | P1 | Linux health may inspect a launcher handle instead of Clash API | `SingBoxManager.Health.cs:26-39`; `HealthMonitor.cs:491` | tests |
| CR-2-9 | P1 | Deployed Unix sing-box copy may lose executable mode | `StartupPipeline.cs:1128` | tests/upstream |
| CR-3-1 | P1 | Fragment-less VLESS entries may have empty, non-unique names | `VlessUriParser.cs:58`; `VlessServersResolver.cs:102,135-136` | correctness; security |
| CR-3-2 | P1 | Subscription decoding may reject unpadded base64url | `SubscriptionFetcher.cs:182,202` | correctness |
| CR-3-3 | P2 | Resolver may not guard null subscription entries/server lists | `SubscriptionResolver.cs:63,71,83-85` | correctness; security |
| CR-3-4 | P1 | Deep-verifier stderr may bypass secret scrubbing | `VlessDeepVerifier.cs:321-325`; `CrashReporter.cs:198` | security |
| CR-3-5 | P1 | Shadowsocks authority parser may mishandle `/` in base64 userinfo | `ServerUriParser.cs:660` | schema/upstream |
| CR-4-1 | P1 | Emergency manager assigns process before successful start | `EmergencyChannelManager.cs:44-54,142-198` | all three lenses |
| CR-4-2 | P1 | Concurrent emergency start/stop may orphan a process | `EmergencyChannelEngine.cs:116-167` | correctness |
| CR-4-3 | P1 | Emergency engine may publish Connected after manager exit | `EmergencyChannelEngine.cs:120-123,196-208` | security |
| CR-4-4 | P1 | Cache merge may drop deep-verification timestamps | `FreeConfigAggregator.cs:185-198`; `FreeConfigsPageViewModel.cs:852-855` | correctness; tests |
| CR-4-5 | P2 | Deep verifier may read `StringBuilder` during async append | `FreeConfigDeepVerifier.cs:108,146-164,243` | correctness |
| CR-4-6 | P2 | Free-config HTTP downloads may lack a payload cap | `FreeConfigFetcher.cs:20-63`; `FreeConfigPoolFetcher.cs:37-38` | correctness; security |
| CR-4-7 | P2 | Server-pool path may omit user-configured sources | `FreeConfigAggregator.cs:87-98`; `FreeConfigsPageViewModel.cs:413-416` | security |
| CR-4-8 | P2 | Deep verifier may classify caller cancellation as endpoint timeout | `FreeConfigDeepVerifier.cs:238-254` | security |
| CR-4-9 | P1 | Invalid percent escape may escape `EmergencyChannelConfig.TryParse` | `EmergencyChannelConfig.cs:89` | tests/upstream |
| CR-4-10 | P2 | Free-config deep verifier may omit XHTTP transport | `FreeConfigDeepVerifier.cs:340-357`; `VlessDeepVerifier.cs:640-660` | tests/upstream |
| CR-4-11 | P2 | ViewModel may retain disposed emergency-channel engine | `MainWindowViewModel.Wgturn.cs:364-425` | tests/upstream |

## Lead status

Pending Iteration B and source verification. Similar-looking candidates remain separate until the lead traces their actual control flow.
