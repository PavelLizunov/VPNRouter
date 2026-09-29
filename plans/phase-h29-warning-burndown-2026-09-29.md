# H-29: first step of the compiler warning burndown

## Why

`Directory.Build.props` plans to enable `TreatWarningsAsErrors` after a warning burndown. The full
build of `VPNRouter.App`, `.CLI` and `.Service` on `windows-worker` (2026-09-29) reports 29 warnings in
product code besides the platform-analyzer `CA1416` noise. Most are noise, none is a runtime defect.

## What

- `MainWindowViewModel*.cs`: `_logger?.` becomes `_logger.` (53 sites). `_logger` is a non-null
  readonly field; mixing `?.` and `.` made the compiler flag 16 later `_logger.` calls as possible null
  dereferences (CS8602).
- `SimpleMode`: three reads of the generated field `_smpInput` use the `SmpInput` property (MVVMTK0034).
- `ServerTesting`: remove the unused local `done` (CS0219).
- `ZapretManager`: remove the public event `OutputReceived`, which was never raised and had no subscriber
  (CS0067).

## Not covered

The remaining CS8602/CS8604/CS8619 in `AutoFailoverEngine`, `ConfigGenerator.Rules`,
`CustomConfigInjector`, `ServerHealthProbe`, `ApplicationsPage.axaml.cs` and the obsolete
`Bitmap.Save` call in `AppAutomationDriver` need individual reading. `CA1416` is not addressed.

## Verification

Rebuild on `windows-worker` and count the warnings again; related tests; exact-head CI.

## Outcome

Pending.
