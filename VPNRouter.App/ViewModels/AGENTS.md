# VPNRouter.App.ViewModels Sub-Zone Instructions

Applies to `VPNRouter.App/ViewModels/` and descendants. Follow
[App instructions](../AGENTS.md) and the [canonical contract](../../docs/agent-contract.md).

## Source navigation

`MainWindowViewModel.cs` holds construction and cross-concern wiring. The
`MainWindowViewModel.*.cs` partials group connection handling, traffic stats,
profiles, subscriptions, server testing, free configs, settings, simple mode,
runtime status, autostart, theme/logo state and localization. Use the current
files rather than treating this map as an exhaustive API inventory.

- [MainWindowViewModel.Connection.cs](MainWindowViewModel.Connection.cs):
  `ToggleConnectionAsync`, engine status handling and connection error branches.
- [MainWindowViewModel.RuntimeStatus.cs](MainWindowViewModel.RuntimeStatus.cs):
  runtime-status reconciliation; read alongside connection event handling.
- [Internals/TwoPhaseStartCoordinator.cs](Internals/TwoPhaseStartCoordinator.cs):
  start-task/event races, phase budgets and typed outcomes.
- `FreeConfigs/`: page and item ViewModels; `MainWindowViewModel.FreeConfigs.cs`
  integrates them with the shell.
- `ServerViewModel.cs`, `SubscriptionViewModel.cs`, `CustomConfigViewModel.cs`
  and `CustomRuleViewModel.cs`: server, subscription and configuration rows.
- `AppItemViewModel.cs` and `AppGroupViewModel.cs`: application routing rows.
- `ServiceViewModel.cs`, `SetupWizardViewModel.cs` and
  `UpdateNotificationViewModel.cs`: service, setup and update UI.
- `Internals/ToolTabAvailability.cs`: tool-tab availability decisions.

## Connection-state ownership

Do not equate a completed `VpnEngine.StartAsync` task with a connected tunnel.
`TwoPhaseStartCoordinator.RunAsync` waits for `SingBoxStarted` in phase A and
`Connected` in phase B; either phase can instead return `StartTaskCompleted`.
The caller awaits that task to surface exceptions. In `ToggleConnectionAsync`,
a clean `StartTaskCompleted` clears `IsConnecting` but does not itself set
`IsConnected`; the `Connected` outcome sets the connected UI state.

Also inspect `OnEngineStatus`, which updates UI state from status strings,
and runtime-status reconciliation. Typed readiness is not the only existing
UI-state writer, and a start return is not a data-plane guarantee. Timeout,
cancellation, service-managed and disconnect paths have their own branches in
`MainWindowViewModel.Connection.cs`; do not replace them with a symmetric
start/stop diagram.

## Editing rules

- Preserve the public-surface characterization contract in
  `VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs`; intentional
  API changes require deliberate review and re-pinning.
- Marshal background callbacks to the UI thread before changing bound state;
  use the existing dispatcher handling in the owning partial as the reference.
- Keep transition guards in `ToggleConnectionAsync` and inspect other entry
  points separately; one guard is not proof that every lifecycle race is covered.
- Use the shared bilingual strings, and clean up event subscriptions when their
  owner is disposed. Follow the App zone's verification guidance.
