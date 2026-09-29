# H-4c: remove production code that only tests used

## Why

A scripted scan showed production methods and one class referenced only by
tests, with no caller in the product, XAML, Java, scripts or workflows.
Their tests only verified that unused logic. The owner ordered dead-code
removal step by step on 2026-09-29.

## What

Removed: `UdpDegradationDetector` (class and tests), `TgProxyManager.KillAll` and
`KillByPort`, `RoutingAppListEditor.TryAddProcessName` and
`IsStillRoutedByAnother`, `ZapretAutoStrategy.ParseFlowsealTranscript`,
`ProcessQuery.CountAlive`, `ServerHealthProbe.AliveRanked`,
`SplitTunnelDriverProtocol.ClassifyEvent` and `EventSeverity`,
`RuntimeStatusDetector.IsTgProxyRunning`, `PlaceholderDefense.InspectUri`,
`LeakProtection.CollectIncompatibleSettings`,
`LaunchFailureCounter.RecordFailureType`, `PictogramText.SetPrefixOnly`, and the
test methods that called them (whole test methods; three test files became empty
and were deleted).

## Kept on purpose

`*ForTests` seams, methods bound through generated XAML commands
(`TestAllServersAsync`, `SmpToggleConnectAsync`, `AddServer`, ...), and
test-observation APIs of live classes (`ConnectionHealthState.Snapshot`,
`SingBoxManager.GetMetrics`, `CreateBridgedAppItem`, `ResetCooldown`).

## Verification

Exact-head CI builds and runs the remaining suite; no reference to a removed
name remains in tracked text.

## Outcome

Pending CI.
