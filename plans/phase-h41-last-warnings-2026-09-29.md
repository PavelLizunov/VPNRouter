# H-41: clear the remaining compiler warnings of the shipped projects

## Why

After the warning burndown (H-24, #364) the exact-head CI log of `main` at `b0b26148` still listed 12 compiler
warnings outside `CA1416` and outside the test project: five in `VPNRouter.Core`, seven in `VPNRouter.App`
(the last three only show in the non-Windows compile, where `PLATFORM_WINDOWS` code is excluded). The goal is a
build whose warning list is empty, so that a new warning is visible when it appears.

## What

Each change keeps the runtime behaviour; none of them is a blanket suppression except one, called out below.

- `AutoFailoverEngine.cs`: `_settings.App?.Subscriptions` became `_settings.App.Subscriptions`; the method
  already dereferences `_settings.App` a few lines earlier (`_settings.App.ConfigMode`), so the `?.` never
  protected anything and only made the later `_settings.App.ActiveSubscriptionServer` look nullable.
- `ConfigGenerator.Rules.cs`: `rule.Type.Equals(...)` became `string.Equals(rule.Type, ...)`; the switch above it
  maps a null `Type` to `domain_suffix`, so the geosite/geoip branch is never reached with null.
- `CustomConfigInjector.cs`: the local-DNS branch tests `dnsForFinal != null` as well; `dnsServersForFinal` is read
  from `dnsForFinal?["servers"]`, so it can only be non-null when `dnsForFinal` is.
- `ServerHealthProbe.cs`: `new ServerLiveness(s!, ...)` in the two failure branches. This one is an annotation, not
  a fix: the surrounding code already logs `s?.Name`, so null list entries are tolerated at runtime and the result
  keeps carrying whatever the list held.
- `ApplicationsPage.axaml.cs`: `.OfType<string>()` before the blank-name filter; guard `btn == null || flyout == null`
  before `flyout.ShowAt(btn)` (`flyout` is derived from `btn?.Flyout`, so null `btn` already returned).
- `AppAutomationDriver.cs`: `rtb.Save(ms, PngBitmapEncoderOptions.Default)` replaces the obsolete
  `Save(Stream, int?)`, whose default was PNG.
- `MainWindowViewModel.Settings.cs`: `_settings.App.Subscriptions?.FirstOrDefault` lost its `?.`; the same method
  reads `_settings.App.Subscriptions.Count` later, so a null list would have thrown anyway.
- `MainWindowViewModel.Zapret.cs`, `ServiceViewModel.cs`: `_forceFreshProbe` is declared only inside the
  `PLATFORM_WINDOWS` region that uses it; `_zapretProbeCts` and `_isLoading` get an explicit initializer, because
  their only assignments sit in Windows-only code while non-Windows code reads them.

## Verification

Build of App, CLI, Service and Core on `windows-worker` with the warning list compared to the CI log; exact-head
CI (Linux and Windows builds, full Windows suite, Android compile check).

## Outcome

Merged in #377 after green exact-head CI.
