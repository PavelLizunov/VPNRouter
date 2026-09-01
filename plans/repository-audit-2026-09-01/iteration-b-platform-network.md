# Iteration B — Platform/network counter-audit index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage: `PN-1` through `PN-4`
Status: independent adversarial counter-audit; signals are not lead verdicts.

## Coverage receipts

| Leaf | Reviews | Fresh lenses | A-candidate checks | New reports |
|---|---:|---|---:|---:|
| PN-1 | 3/3 | counterexamples/state; failure races/cleanup; upstream Windows/negative evidence | 15 | 8 |
| PN-2 | 3/3 | counterexamples/state; failure privilege/cleanup; upstream Linux/negative evidence | 18 | 9 |
| PN-3 | 3/3 | counterexamples/state; failure races/cleanup; upstream macOS/negative evidence | 21 | 9 |
| PN-4 | 3/3 | counterexamples/state; failure races/cleanup; upstream Android/negative evidence | 21 | 8 |

## Cross-iteration signals

| Candidate | Iteration B signal | Primary cited evidence |
|---|---|---|
| PN-1-1 | supported by 3/3 | `EtwProcessMonitor.cs:38,51-98,125-169`; `EtwProcessMonitorTests.cs:34-49` |
| PN-1-2 | supported by 3/3 | `StartupPipeline.cs:1370`; `EtwProcessMonitor.cs:135,188-195`; `ProcessScanner.cs:149-163` |
| PN-1-3 | supported by 3/3 | `ProcessScanner.cs:54,106-109,268-277` |
| PN-2-1 | supported | `LinuxFirewallManager.cs:206-212,264-269`; `LinuxFirewallManagerTests.cs:186-195` |
| PN-2-2 | supported | `LinuxFirewallManager.cs:126-135` |
| PN-2-3 | supported/duplicates SU-3-3 | `vpnrouter-update-helper:45`; `SingBoxManager.LinuxStop.cs:48,65,86,147` |
| PN-2-4 | duplicate, fixed in green unmerged PR #204 | policy/helper/UpdateChecker trust boundary; PR #204 head `b665a66b` |
| PN-2-5 | supported | `LinuxDnsHardening.cs:80-102,226-235`; `VpnEngine.cs:411` |
| PN-2-6 | supported | `UpdateChecker.cs:1088-1111` |
| PN-3-1 | supported by 3/3 | `StartupPipeline.cs:1370`; `MacProcessMonitor.cs:126`; `MacProcessScanner.cs:44,49,192` |
| PN-3-2 | supported by 3/3 | `VpnEngine.cs:404-416`; `MacDnsHardening.cs:44-56,159-163`; `MacDnsParsers.cs:113-140` |
| PN-3-3 | supported by 3/3 | `StartupPipeline.cs:407,456,1095,1157`; `MacFirewallManager.cs:117,137,349-355` |
| PN-3-4 | supported by 3/3 | `MacFirewallManager.cs:342-344,402` |
| PN-3-5 | supported by 3/3 | `MacProcessScanner.cs:33,44,50,57-58` |
| PN-3-6 | supported by 3/3 | `SingBoxManager.Lifecycle.cs:716`; `MacDnsHardening.cs:177`; `MacFirewallManager.cs:152,169` |
| PN-4-1 | supported by 3/3 | `SideloadSource.cs:239-252`; `build-android.yml:347,387` |
| PN-4-2 | contradicted by control flow | `AndroidApp.ServerList.cs:1363-1386`; `AndroidApp.SubscribePage.cs:744-764` (`WaitAsync` precedes `try`) |
| PN-4-3 | supported by 3/3 | `AndroidStorage.cs:591-671`; `MainActivity.cs:1033-1144` |
| PN-4-4 | supported by 3/3 | `VpnRouterService.java:996-1024,1477-1482` |
| PN-4-5 | supported by 3/3 | `VpnRouterService.java:1616,1932,1970,1992` |
| PN-4-6 | supported by 3/3 | `VpnRouterService.java:673-690,1216-1256` |
| PN-4-7 | supported by 3/3 | `VpnRouterService.java:1496-1504,1515,1521` |

## Lead status

Iteration B coverage is complete. PN-4-2 is retained as a concrete counterexample to swarm agreement: the third reviewer reopened the exact `try/finally` boundary and refuted the first two reports. All other signals still require lead tracing and caller/test validation.
