# Library And Upstream Documentation

Use this as a link map when asking ChatGPT about VPNRouter design, bugs, or implementation.

The exact package versions are pinned in the repository `.csproj` files. These links are for concepts, APIs, and upstream behavior.

## Core VPN / Routing

### sing-box

- Main docs: https://sing-box.sagernet.org/
- Configuration index: https://sing-box.sagernet.org/configuration/
- TUN inbound: https://sing-box.sagernet.org/configuration/inbound/tun/
- Route config: https://sing-box.sagernet.org/configuration/route/
- Route rules: https://sing-box.sagernet.org/configuration/route/rule/
- DNS config: https://sing-box.sagernet.org/configuration/dns/
- VLESS outbound: https://sing-box.sagernet.org/configuration/outbound/vless/
- Hysteria2 outbound: https://sing-box.sagernet.org/configuration/outbound/hysteria2/
- TUIC outbound: https://sing-box.sagernet.org/configuration/outbound/tuic/
- Shadowsocks outbound: https://sing-box.sagernet.org/configuration/outbound/shadowsocks/
- Naive outbound: https://sing-box.sagernet.org/configuration/outbound/naive/
- Clash API experimental config: https://sing-box.sagernet.org/configuration/experimental/clash-api/
- Changelog: https://sing-box.sagernet.org/changelog/
- Source repo: https://github.com/SagerNet/sing-box

VPNRouter-specific notes:

- `process_name` matching is case-sensitive.
- `route_exclude_address` is critical: local networks must bypass TUN before sing-box sees them.
- DNS stalls in sing-box logs are important for Discord/voice diagnostics.

### sing-box-lx / AWG / XHTTP

- sing-box-lx fork: https://github.com/Leadaxe/sing-box-lx
- wireguard-go-awg2-lx: https://github.com/Leadaxe/wireguard-go-awg2-lx
- upstream wireguard-go mirror: https://github.com/WireGuard/wireguard-go

VPNRouter-specific notes:

- Windows desktop release builds currently bundle `sing-box-lx.exe`.
- AWG and XHTTP support depend on this fork, not vanilla upstream sing-box.

## Windows True Split

- Mullvad Windows split-tunnel driver: https://github.com/mullvad/win-split-tunnel
- Mullvad split tunneling user docs: https://mullvad.net/en/help/split-tunneling-with-the-mullvad-app

VPNRouter-specific notes:

- True Split is Windows-only.
- Other VPN split-tunnel drivers/services can conflict with the same kernel driver object/service.
- If another split driver is already active, fail open and explain the conflict.

## Avalonia UI

- Main docs: https://docs.avaloniaui.net/docs/welcome
- Getting started: https://docs.avaloniaui.net/docs/get-started/
- Data binding: https://docs.avaloniaui.net/docs/data-binding/introduction-to-data-binding
- Binding syntax: https://docs.avaloniaui.net/docs/data-binding/data-binding-syntax
- Styles: https://docs.avaloniaui.net/docs/styling/styles
- XAML compilation: https://docs.avaloniaui.net/docs/xaml/compilation
- Headless testing platform: https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform
- Android guide: https://docs.avaloniaui.net/docs/platform-specific-guides/android/
- Android deployment: https://docs.avaloniaui.net/docs/deployment/android
- API reference: https://docs.avaloniaui.net/api/
- Source repo: https://github.com/AvaloniaUI/Avalonia

VPNRouter-specific notes:

- Desktop UI is Avalonia.
- Android UI also uses Avalonia, with linked design tokens from desktop.
- Headless screenshots in this context pack come from Avalonia Headless tests.
- Compact-window overflow is a recurring UI risk.

## MVVM / Desktop App Pattern

- CommunityToolkit.Mvvm docs: https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/
- CommunityToolkit.Mvvm NuGet: https://www.nuget.org/packages/CommunityToolkit.Mvvm
- .NET Generic Host: https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host
- .NET configuration: https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration

VPNRouter-specific notes:

- Main desktop state lives in Avalonia ViewModels.
- Prefer simple properties/commands over new frameworks.
- Do not add a new DI abstraction unless there is a clear payoff.

## Serialization / Config

- System.Text.Json overview: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/overview
- System.Text.Json migration notes: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft
- YamlDotNet repo/docs: https://github.com/aaubry/YamlDotNet
- YamlDotNet project page: https://aaubry.net/pages/yamldotnet.html

VPNRouter-specific notes:

- Generated sing-box config is JSON.
- User settings are YAML.
- Avoid ad-hoc string editing of config files when structured APIs are available.

## Logging / Diagnostics

- Serilog site: https://serilog.net/
- Serilog getting started: https://github.com/serilog/serilog/wiki/Getting-Started
- Serilog source repo: https://github.com/serilog/serilog

VPNRouter-specific notes:

- User diagnostics often arrive as `vpnrouter*.log`, `current.json`, and `config.yaml`.
- Good diagnostics should say what happened, why it matters, and the next safe action.

## CLI / Console

- Spectre.Console docs: https://spectreconsole.net/
- Spectre.Console repo: https://github.com/spectreconsole/spectre.console

VPNRouter-specific notes:

- CLI uses Spectre.Console / Spectre.Console.Cli.
- Keep CLI flows boring and scriptable.

## Testing

- xUnit.net home: https://xunit.net/
- xUnit v3 getting started: https://xunit.net/docs/getting-started/netcore/cmdline
- xUnit v3 changes: https://xunit.net/docs/getting-started/v3/whats-new
- xUnit shared context: https://xunit.net/docs/shared-context
- Avalonia headless testing: https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform

VPNRouter-specific notes:

- Small targeted tests are preferred for narrow fixes.
- UI screenshot tests produce the PNGs in this pack.

## Android

- .NET Android overview: https://learn.microsoft.com/en-us/dotnet/android/
- Avalonia Android guide: https://docs.avaloniaui.net/docs/platform-specific-guides/android/
- Avalonia Android deployment: https://docs.avaloniaui.net/docs/deployment/android
- AndroidX Core NuGet: https://www.nuget.org/packages/Xamarin.AndroidX.Core
- ZXing Android Embedded: https://github.com/journeyapps/zxing-android-embedded

VPNRouter-specific notes:

- Android source-links Core code instead of using a normal Core project reference.
- Android uses `libbox.aar` from sing-box gomobile.
- QR scanning uses ZXing Android Embedded.

## DPI / Telegram Helpers

- Flowseal zapret-discord-youtube: https://github.com/Flowseal/zapret-discord-youtube
- Original zapret: https://github.com/bol-van/zapret
- zapret English docs: https://github.com/bol-van/zapret/blob/master/docs/readme.en.md
- Flowseal tg-ws-proxy: https://github.com/Flowseal/tg-ws-proxy

VPNRouter-specific notes:

- These are Windows helper surfaces, not the core VPN engine.
- They should not complicate the main connect flow.

## Graphics / Rendering

- SkiaSharp repo: https://github.com/mono/SkiaSharp
- SkiaSharp API docs: https://github.com/mono/SkiaSharp-API-docs
- SkiaSharp NuGet: https://www.nuget.org/packages/SkiaSharp/

VPNRouter-specific notes:

- Avalonia rendering goes through Skia/SkiaSharp.
- Screenshot tests depend on stable rendering enough to catch major layout regressions, not pixel-perfect design approval.

