# H-43: split LoadSettingsIntoUI and SaveSettings into named sections

## Why

`MainWindowViewModel.LoadSettingsIntoUI` (191 lines) and `SaveSettings` (184 lines) copy every setting between the
settings object and the view model in one long body each, with the load side nested one level too deep inside a
`try`/`finally`. Finding where one setting is loaded or saved meant reading the whole method.

## What

- `LoadSettingsIntoUI` keeps the `_isLoadingUI` guard and calls, in the original order, `LoadAppearanceIntoUI`,
  `LoadRoutingIntoUI`, `LoadNetworkOptionsIntoUI`, `LoadZapretIntoUI`, `LoadWindowsToolsIntoUI` (the
  `PLATFORM_WINDOWS` block for hosts, Zapret files and the Telegram proxy), the update channel line,
  `LoadServersIntoUI`, `LoadSubscriptionsIntoUI` (with the legacy `subscription_url` migration),
  `LoadCustomConfigsIntoUI`, then `LoadApps` and `RefreshLocalization`.
- `SaveSettings` keeps the guard and the final `Save`, and calls `BackupConfigFile`, `ApplyConfigModeToSettings`,
  `ApplySubscriptionsToSettings`, `ApplyRoutingToSettings`, `ApplyOptionsToSettings`, `ApplyServersToSettings`,
  `ApplyCustomConfigsToSettings` and `ApplyAppSelectionToSettings`.
- The statements themselves moved unchanged. A multiset comparison of the trimmed lines of the file before and
  after shows nothing removed and only method headers, braces and the calls added.

## Verification

Worker-only corpus test (not committed): 40 seeded settings objects (language, theme, config mode, routing,
servers, subscriptions incl. the legacy URL, custom configs, custom rules, MTU, update channel) are loaded into a
real `MainWindowViewModel` through the constructor, then up to six safe properties are changed and `SaveSettings`
is invoked. The hash of every public view-model property and of the settings YAML (timestamps normalised) after
the load and after the save is compared between the parent SHA and this SHA. Exact-head CI runs the full suite.

## Outcome

Merged in #380 after green exact-head CI. Worker equivalence check passed (see the PR description).
