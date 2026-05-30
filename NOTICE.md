# Third-party notices

VPNRouter (GPL-3.0) builds on the third-party components below. This is a
best-effort inventory for attribution and transparency, not legal advice; each
component's authoritative license is in its own repository or package.

## Native runtime components

Invoked as separate processes or loaded as native libraries — downloaded on
demand or bundled with the installer, not statically linked into the managed code.

| Component | Used for | Source | License |
|---|---|---|---|
| sing-box | Core VPN/proxy engine (TUN, VLESS+Reality, DNS) | https://github.com/SagerNet/sing-box | GPL-3.0 |
| libbox.aar | sing-box gomobile binding (Android) | https://github.com/SagerNet/sing-box (experimental/libbox) | GPL-3.0 |
| Zapret / winws | DPI bypass (Discord / YouTube) | https://github.com/Flowseal/zapret-discord-youtube , https://github.com/bol-van/zapret | see upstream repositories |
| Telegram proxy (tg-ws-proxy) | Telegram proxy | fetched on demand from its upstream GitHub release (see `TgProxyUpdater`) | see upstream repository |
| GeoIP / geosite rule-sets | RU-bypass / routing | sing-box rule-sets + MaxMind GeoLite2 | respective upstream terms (e.g. MaxMind GeoLite2 EULA) |

## .NET / NuGet libraries

Authoritative license + version are in each package and in the `.csproj` files.

| Package(s) | License |
|---|---|
| Avalonia, Avalonia.* (Desktop, Android, Themes.Fluent, Fonts.Inter, HarfBuzz, Headless) | MIT |
| SkiaSharp | MIT |
| CommunityToolkit.Mvvm | MIT |
| YamlDotNet | MIT |
| Spectre.Console, Spectre.Console.Cli | MIT |
| Serilog + sinks (Console, File, Extensions.*) | Apache-2.0 |
| ZXing.Net | Apache-2.0 |
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 / MIT |
| Microsoft.* and System.* (Extensions.Hosting, Diagnostics.Tracing.TraceEvent, Management, Win32.SystemEvents, NET.Test.Sdk, ...) | MIT |
| Xamarin.AndroidX.* | MIT / Apache-2.0 |

## Notes

- VPNRouter does not modify these components; it downloads or bundles released
  artifacts and invokes them. The GPL-3.0 obligations for sing-box / libbox are
  met by linking to the upstream source above; VPNRouter's own source is in this
  repository.
- For the exact pinned native-tool versions and how to update them, see
  `tools/native-deps.md`.
