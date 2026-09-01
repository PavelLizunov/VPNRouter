# Iteration B — Core runtime counter-audit index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage: `CR-1` through `CR-4`
Status: independent adversarial counter-audit; signals are not lead verdicts.

## Coverage receipts

| Leaf | Reviews | Fresh lenses | A-candidate checks | New reports |
|---|---:|---|---:|---:|
| CR-1 | 3/3 | counterexamples/state; failure bounds/ordering; upstream schema/negative evidence | 15 | 6 |
| CR-2 | 3/3 | counterexamples/state; failure concurrency/cleanup; upstream platform/negative evidence | 15 | 3 |
| CR-3 | 3/3 | counterexamples/state; failure bounds/ambiguity; upstream protocol/negative evidence | 15 | 8 |
| CR-4 | 3/3 | counterexamples/state; failure concurrency/bounds; upstream network/negative evidence | 15 | 9 |

## Cross-iteration signals

| Candidate | Iteration B signal | Primary cited evidence |
|---|---|---|
| CR-1-1 | mixed | `ConfigGenerator.cs:1599-1601`; `XhttpTransportTests.cs:70` |
| CR-1-2 | mixed | `ConfigGenerator.cs:375,402,559,572`; `CustomRule.cs:43,62` |
| CR-1-3 | mixed | `LeakProtection.cs:156,183-211`; config-model initializers |
| CR-1-4 | mixed | `CustomConfigInjector.cs:836-848,1282-1300` |
| CR-1-7 | mixed/intended behavior possible | `ConfigGenerator.cs:1990-2008,2040-2049`; `ConfigGeneratorQuicBlockTests.cs:119-128` |
| CR-2-1 | mixed | `VpnEngine.cs:1508,1608-1632`; `AutoFailoverEngine.cs:35-70,180-239,372-375` |
| CR-2-2 | mostly contradicted by guards | `SingBoxManager.Lifecycle.cs:386-456`; `TunAdapterPnpSettleGateTests.cs:206-235` |
| CR-2-3 | mixed | `StartupPipeline.cs:1182,1215-1327`; `VpnEngineProbeFailoverGateTests.cs:25-41` |
| CR-2-4 | contradicted | `VpnEngine.cs:994-1013` |
| CR-2-5 | mixed | `SingBoxManager.CrashDetect.cs:64-114` |
| CR-2-7 | contradicted by attempt caps | `AutoFailoverEngine.cs:138-149,180-184,365-367`; `AutoFailoverEngineTests.cs:120-135` |
| CR-2-8 | supported | `SingBoxManager.Health.cs:26,35-38`; `HealthMonitor.cs:490-497` |
| CR-3-1 | supported by 3/3 | `VlessUriParser.cs:54-58`; `VlessServersResolver.cs:102-147`; `VlessConfig.cs:113-118` |
| CR-3-2 | supported by 3/3 | `SubscriptionFetcher.cs:182,202-208,233` |
| CR-3-3 | supported by 3/3 | `SubscriptionResolver.cs:63-85`; `VlessServersResolver.cs:68` |
| CR-3-4 | supported by 3/3 | `VlessDeepVerifier.cs:321-326`; `CrashReporter.cs:198` |
| CR-3-5 | supported by 3/3 | `ServerUriParser.cs:660-666` |
| CR-4-1 | supported by 3/3 | `EmergencyChannelManager.cs:44-54,122-127,181-184` |
| CR-4-2 | supported by 3/3 | `EmergencyChannelEngine.cs:116-167` |
| CR-4-3 | supported by 2 lenses | `EmergencyChannelEngine.cs:120-122,196-208` |
| CR-4-4 | supported by 3/3 | `FreeConfigAggregator.cs:185-198`; `FreeConfigModels.cs:96,120`; `FreeConfigsPageViewModel.cs:852-855` |
| CR-4-6 | supported/duplicate of SU-2-2 | `FreeConfigFetcher.cs:54,63`; `FreeConfigPoolFetcher.cs:37-38` |
| CR-4-8 | supported by 3/3 | `FreeConfigDeepVerifier.cs:111,249-254` |
| CR-4-9 | supported by 2 lenses | `EmergencyChannelConfig.cs:62-91` |

## Materially new Iteration B candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| CR-B-1 | P1 | Empty custom `dns.servers` may lead to index access during injection | `CustomConfigInjector.cs` DNS injection path | pending lead trace |
| CR-B-2 | P1 | Exclude-mode unmatched UDP may bypass intended routing | `ConfigGenerator.cs` exclude-mode UDP rule ordering | pending lead trace |
| CR-B-3 | P1 | Partial-start disposal may skip process/TUN cleanup when `IsRunning` is false | `VpnEngine` and `SingBoxManager` partial-start cleanup paths | pending lead trace |
| CR-B-4 | P1 | Subscription deduplication key may collapse materially different protocol/transport/Reality peers | `SubscriptionFetcher.cs` deduplication path | pending lead trace |
| CR-B-5 | P1 | Batch deep verification may release `SemaphoreSlim` after cancellation without acquisition | `VlessDeepVerifier` batch loop | pending lead trace |
| CR-B-6 | P1 | URI conversion may corrupt base64 userinfo containing `/` | `ServerUriParser.cs` scheme conversion path | pending lead trace |
| CR-B-7 | P1 | Disposed emergency engine may retain UI event subscriptions | `EmergencyChannelEngine` / `MainWindowViewModel` event wiring | pending lead trace |
| CR-B-8 | P1 | Free-config cache serialization may race collection mutation | `FreeConfigsPageViewModel` cache/update paths | pending lead trace |
| CR-B-9 | P1 | Free-config verifier shares a non-thread-safe stderr/stdout buffer across callbacks | `FreeConfigDeepVerifier.cs:111,249-254` | pending lead trace |

## Lead status

Iteration B coverage is complete. Mixed and contradicted signals are intentionally retained so lead verification can reject false positives instead of inheriting Iteration A labels.
