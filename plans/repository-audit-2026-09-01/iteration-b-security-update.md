# Iteration B — Security/update counter-audit index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage: `SU-1` through `SU-3`
Status: independent adversarial counter-audit; signals are not lead verdicts.

## Coverage receipts

| Leaf | Reviews | Fresh lenses | A-candidate checks | New reports |
|---|---:|---|---:|---:|
| SU-1 | 3/3 | trust data-flow; failure rollback/quoting; upstream platform/negative evidence | 12 | 7 |
| SU-2 | 3/3 | tests/schema; regex fuzz corpus; stream-size boundaries | 9 | 7 |
| SU-3 | 3/3 | ownership/arguments; privilege races; upstream platform/negative evidence | 21 | 5 |

Two original SU-2 reviewer calls returned null, and their single automatic retry also returned null. Two fresh replacement lenses were dispatched; both completed, preserving the minimum three independent successful reports.

## Cross-iteration signals

| Candidate | Iteration B signal | Primary cited evidence |
|---|---|---|
| SU-1-1 | supported by 3/3 | `UpdateBackup.cs:207-231`; startup recovery caller |
| SU-1-2 | supported by 3/3 | `packaging/windows/install.ps1:98-132` |
| SU-1-3 | supported by 3/3 | `update-helper.cmd:10-23`; `UpdateChecker.cs:248-250,352` |
| SU-1-4 | supported by 3/3 | `UpdateChecker.cs:188-190,1166-1188,1297` |
| SU-2-1 | contradicted by 3/3 | `CrashReporter.cs:174-176,203-208`; `CrashReporterScrubberTests.cs:70-78` |
| SU-2-2 | supported by 3/3 | `FreeConfigFetcher.cs:54-63`; bounded `PolicyHttpClient` comparison |
| SU-2-3 | supported by 3/3 | `VlessDeepVerifier.cs:321-325`; `FreeConfigDeepVerifier.cs:164-168`; `DeepVerifyProbe.cs:219-223` |
| SU-3-1 | 2 support / 1 contradiction | `StopCommand.cs:25-64`; third report reused only the pre-wait ownership guard and did not eliminate post-wait PID reuse |
| SU-3-2 | 2 support / 1 contradiction | `VPNRouter.Service/Program.cs:42-65`; TUN ownership guards VPNRouter peers but not arbitrary third-party `sing-box` |
| SU-3-3 | 2 support / 1 contradiction | `SingBoxManager.LinuxStop.cs:48-86,156`; post-kill API checks do not scope `pkill -f` targets |
| SU-3-4 | 2 support / 1 contradiction | `UpdateChecker.cs:1089-1114`; mode `0700` is applied after predictable-path creation |
| SU-3-5 | 2 support / 1 contradiction | Linux/macOS firewall hostname resolution and full-tunnel ruleset construction |
| SU-3-6 | 2 support / 1 contradiction | `ServiceInstaller.cs:47-53`; exact persisted `ImagePath` remains to be measured |
| SU-3-7 | duplicate of CL-3 path finding | CLI/service `%ProgramData%` paths |

## Materially new Iteration B candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| SU-B-1 | P1 | Update asset download path may buffer without a byte ceiling | `UpdateChecker` asset download flow | pending lead trace |
| SU-B-2 | P1 | Windows self-elevation bootstrap may fetch installer content without a pinned checksum | `packaging/windows/install.ps1` bootstrap flow | pending lead trace |
| SU-B-3 | P1 | Named TUN ownership semaphore may fail open on `UnauthorizedAccessException` | `TunOwnershipLock.cs:81-87` | pending lead trace |
| SU-B-4 | P1 | Firewall managers use predictable shared temporary ruleset paths before privileged load | `LinuxFirewallManager.cs:126-135`; `MacFirewallManager.cs:141-145,299-303` | pending lead trace |
| SU-B-5 | P2 | `FreeConfigGeoIp` buffers an uncapped HTTP response | `FreeConfigGeoIp.cs:145,152` | pending lead trace |
| SU-B-6 | P2 | Deep verifier subprocess capture has no bounded ring buffer | `FreeConfigDeepVerifier.cs:108-150`; `VlessDeepVerifier.cs:276,312-315` | pending lead trace |
| SU-B-7 | P2 | Rule-set cache downloads bypass the bounded HTTP primitive | `RuleSetCacheManager.cs:123,133,135` | pending lead trace |
| SU-B-8 | P2 | Quoted multi-word key/value secrets may be only partially redacted | `DiagnosticsRedactor.cs:117-119` | pending lead trace |

## Lead status

Iteration B coverage is complete. The third SU-3 report is retained despite several logically incomplete refutations; its exact evidence will be reopened rather than majority-voted.
