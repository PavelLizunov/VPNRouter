# H-70: characterization tests for `MainWindowViewModel.ProbeAndStartZapretAsync`

## Why

`ProbeAndStartZapretAsync` (235 lines, `VPNRouter.App/ViewModels/MainWindowViewModel.Zapret.cs`, Windows only) is the
orchestrator behind the "Enable bypass" button: it cleans a stale ipset, tries a cached winner, otherwise runs the Flowseal
sweep, starts the winning strategy, reads back whether winws is alive and records the result in the probe cache and the
settings. It is one of the six functions still over 200 lines (ledger entry LONG-FUNCTIONS-REMAINING) and it has no test:
the steps before it (H-58..H-62) pinned the parsing, the file switches, the download and the sweep itself, but nothing
pins what the view model does with a sweep result. Splitting it needs these tests first.

## What

- A characterization suite, `MainWindowViewModelZapretProbeTests` (Windows builds only, like the method), that drives the
  private method through reflection on a real view model in a headless Avalonia test and checks the observable outcome:
  view model properties, the process requests sent to a fake runner, the probe cache file, the saved settings.
- One seam, behaviour-neutral by construction: `MainWindowViewModel.FlowsealProbe`, an `internal static` delegate whose
  default is the method group `ZapretAutoStrategy.RunFlowsealProbeAsync`; the method's only call of the real sweep now goes
  through it. It is needed because three branches cannot be reached with the real sweep on the CI runner (the `not_admin`
  diagnostic on an elevated runner, the `sweep_timeout` diagnostic that takes ten minutes, and exact progress values).
  A test pins that the default is the real sweep, and an elevated-runner test drives the real sweep with a fake Flowseal
  script through the default wiring.
- Everything else uses existing seams: `AppPaths.OverrideDataDir` (zapret dir, cache, logs), `ZapretManager(logger, runner)`
  with `FakeProcessRunner`, the file-based ipset flag, a loopback TCP listener for the warm-start target probe with a
  `utils/targets.txt` that points at it (the built-in defaults would contact the internet), and `InMemorySettingsStore`.

No production logic changed. The method was not refactored.

## Verification

CI (`test` and `characterization-windows`). No build or test run on the workstation; nothing in the suite starts winws,
touches WinDivert or installs Zapret (the fake runner receives every process request; the test skips if a real winws is
already running so `ZapretManager.WinwsPid` cannot change the outcome).

## Outcome

Merged after green exact-head CI (`test` and `characterization-windows` both run the new suite: 27 test methods, 23 `AvaloniaFact`, 3 `AvaloniaTheory` and 1 `Fact`, the theories with 9 data rows in total). ProbeAndStartZapretAsync is now pinned and can be split in a later step; the ledger entry LONG-FUNCTIONS-REMAINING still lists it until that split.
