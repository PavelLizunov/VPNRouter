# H-4a: remove unreferenced Core and App methods

## Why

After the repository cleanup, a scripted scan (no compiler; no local .NET SDK)
listed production methods and types whose name appears nowhere else in tracked
text (code, tests, XAML, Java, scripts, workflows). The owner ordered dead-code
removal step by step on 2026-09-29.

## What

Delete unreferenced: `Strings.AutoFailoverSwitching`, `FirewallManager.FindRulesByPrefix`,
`FreeConfigCache.SaveAsync`, `ScanResult.HasChanges`, `SingBoxManager.ReloadConfig`
and `TryReloadConfig` (thin wrappers over the Json variants),
`TunAdapterDiagnostics.IsNetAdapterModuleAvailable`,
`PlaceholderDefense.Layer6_DeepVerify`, `LayerB_MigratorStrip`,
`LayerD_LeakValidation`.

## Excluded after checking

`NavigateToVpn/Zapret/TgProxy` (bound in MainWindow.axaml through generated
`*Command`), `RegisterLazy`, `ReadYaml`/`WriteYaml` (external interfaces),
`PictogramText.SetContent` (attached-property accessor). Android candidates go in
H-4b because PR CI does not build Android.

## Verification

Exact-head CI compiles Core, App and CLI and runs the suite; a second scan finds
no newly orphaned helpers.

## Outcome

Merged as #338 on 2026-09-29; exact-head CI green.
