# H-6: remove unreferenced constants and localization keys

## Why

A scripted scan of static, const and readonly members and localization keys
found names that appear once in all tracked text (their own declaration).

## What

Removed 22 members: 10 localization keys (`Strings`, `Strings.Android`),
`DefaultControlUrls`, three `KnownPlaceholder*` forwarders, `DefaultSeedOrder`,
`CooldownMinutes`, `SlipstreamVersionPath`, `ProxyRepoPublic`,
three unused Win32 error constants and `EventErrorFlag`.

## Kept after checking

`MainWindowViewModel.ServerDeepConcurrency`: the characterization test pins a
hash of the type surface and must not be re-pinned; first CI run failed on it.

`FirewallManager.ConsoleEncoding`: its static initializer registers the code
pages provider (a side effect other code may rely on). Extension classes
`StyledElementResourceExtensions` and `HttpResponseExtensions` (called as
extension methods). Three unread model properties (`EngineSettings.AutoCheck`,
`VPNConfig.Inbound`, `VPNConfig.DownloadDetour`) are serialized schema and stay
until a schema decision.

## Verification

Exact-head CI compiles Core and App and runs the suite. Android files are not
touched.

## Outcome

Pending CI.
