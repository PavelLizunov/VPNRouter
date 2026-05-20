<p align="center">
  <img src="VPNRouter.App/Assets/penguin_logo.png" width="96" alt="VPNRouter logo"/>
</p>

<h1 align="center">VPNRouter</h1>
<p align="center"><b>Virtual Penguin Network</b> — process-based split-tunnel VPN router for Windows, macOS, Linux and Android.</p>

<p align="center">
  <a href="README.md"><b>English</b></a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <a href="https://github.com/PavelLizunov/VPNRouter/releases/latest">
    <img src="https://img.shields.io/github/v/release/PavelLizunov/VPNRouter?include_prereleases&color=7C3AED" alt="Latest release"/>
  </a>
  <a href="https://github.com/PavelLizunov/VPNRouter/releases">
    <img src="https://img.shields.io/github/downloads/PavelLizunov/VPNRouter/total?color=22C55E" alt="Downloads"/>
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/PavelLizunov/VPNRouter?color=2563EB" alt="License"/>
  </a>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/platform-Win%20%7C%20macOS%20%7C%20Linux%20%7C%20Android-lightgrey" alt="Platform"/>
  <img src="https://img.shields.io/badge/LOC-94k-blue" alt="94k LOC"/>
  <img src="https://img.shields.io/badge/tests-765-success" alt="765 tests"/>
</p>

---

## Install (one-liner, all three platforms)

<table>
<tr>
<td width="80" align="center">🐧<br><b>Linux</b></td>
<td>

```bash
curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh
```
Debian / Ubuntu / Mint / Pop / elementary. Adds the signed apt repo, installs `vpnrouter`, sets up passwordless VPN via POSIX capabilities. Updates: `sudo apt upgrade`.
</td>
</tr>
<tr>
<td align="center">🍎<br><b>macOS</b></td>
<td>

```bash
brew install --cask pavellizunov/vpnrouter/vpnrouter
```
Apple Silicon. Auto-strips Gatekeeper quarantine. First launch prompts once for sudoers setup, then passwordless. Updates: `brew upgrade --cask vpnrouter`.
</td>
</tr>
<tr>
<td align="center">🪟<br><b>Windows</b></td>
<td>

```powershell
iwr -useb https://vpn.ninitux.com/install.ps1 | iex
```
Windows 10/11 x64. Auto-elevates via UAC. Registers Start Menu + Add/Remove Programs. Updates: re-run the same command. Uninstall: Settings → Apps → VPNRouter.
</td>
</tr>
<tr>
<td align="center">🤖<br><b>Android</b></td>
<td>

```
Download VPNRouter-v{version}-android.apk from Releases
```
Android 6.0+ (API 23). Side-load via APK (no Play Store yet). Live-preview QR scanner, magic 1-step subscription paste, F-Droid-style permissions (only `CAMERA` + `INTERNET` + `VPN_SERVICE`). Self-update via in-app banner.
</td>
</tr>
</table>

Prefer manual install? See [**Manual download**](#manual-download) below for ZIPs / DMG / AppImage / deb / tar.gz.

---

## What it does

Routes **selected applications** through a VLESS+Reality proxy (via [sing-box](https://github.com/SagerNet/sing-box) TUN mode); everything else goes direct to your ISP. Not a full-tunnel VPN — it's a per-process router. Discord goes through the proxy, your bank site stays direct. No manual proxy settings per app.

### Cross-platform core

- **Split-tunnel routing** — pick the apps from a live process list; they go through your proxy, everything else stays direct.
- **VLESS+Reality + custom configs** — use the built-in VLESS setup or bring your own sing-box JSON (TUIC, Hysteria2, Shadowsocks). Per-process routing is injected either way.
- **Subscriptions** — paste one or more subscription URLs, servers auto-refresh into a unified pool.
- **Server testing** — one-click TCP+TLS probe on any server. Deep verification (real HTTP round-trip + 5 MB bandwidth) for your own servers and subscription pools.
- **Safe auto-update** — each release ships with a `.sha256` companion file; the in-app updater verifies hash before extracting, so a truncated download never installs silently.
- **Status dashboard + Arctic dark theme + RU/EN UI** — live VPN / Zapret / TgProxy badges in the header, custom Avalonia theme, fully translated interface.

### Platform specifics

- **Windows** — UAC elevation; optional Windows Service for boot-time autostart that survives user logoff.
- **macOS** — Apple Silicon native; one-time sudoers setup from the DMG gives passwordless TUN afterwards.
- **Linux** — POSIX capabilities (`cap_net_admin`) for passwordless TUN; systemd service bundled in the `.deb`.

### Windows-only add-ons *(optional)*

These are thin wrappers around upstream projects — they aren't part of the core router and don't work on macOS / Linux. Skip them unless you specifically need DPI bypass or Telegram-only routing.

- **DPI bypass (Zapret)** — integrates [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube). Downloaded on demand from the Tools tab. Useful when your ISP blocks a site by DPI and a full proxy isn't wanted.
- **Telegram proxy** — embedded MTProto proxy ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)) for Telegram-only bypass.

### Bonus: Free Configs tab

A public VLESS aggregator — ~25 000 configs from 14 open sources, pre-validated (TCP+TLS + GeoIP) server-side every 6 hours. Handy to try the app without your own VPN server; not a substitute for a paid or self-hosted endpoint.

## Feature matrix

**53 features** across 11 categories. Full reference with per-feature flow diagrams + complexity ratings + service chains lives in [`plans/feature-catalog-2026-05-17.md`](plans/feature-catalog-2026-05-17.md) (auto-generated audit). Summary:

| Category | Features | Complexity | Platforms |
|---|---:|---|---|
| **Core VPN** (Connect, hot-reload, multi-protocol, custom configs) | 10 | 4 HIGH · 4 MED · 2 LOW | Win · Mac · Linux · Android |
| **Subscriptions** (Add, refresh, test, aggregated pool) | 6 | 2 MED · 4 LOW | All |
| **Free Configs** (aggregator, deep verify, GeoIP) | 5 | 1 HIGH · 3 MED · 1 LOW | All |
| **DPI Bypass / Zapret** (Flowseal integration, strategies) | 5 | 2 HIGH · 2 MED · 1 LOW | Win only |
| **Apps routing** (Include/Exclude, scan_patterns, child detection) | 4 | 1 MED · 3 LOW | All |
| **Profiles** (GitHub > Local > Built-in, merge) | 3 | 1 MED · 2 LOW | All |
| **Custom rules** (IP/domain/regex, action priority) | 3 | 1 MED · 2 LOW | All |
| **Update** (UpdateChecker, channels, self-repair tiers) | 4 | 1 HIGH · 2 MED · 1 LOW | All |
| **UI/UX** (Simple/Advanced, theme, QR scan, paste-and-go) | 5 | 2 MED · 3 LOW | All |
| **Privacy & security** (Leak protection, F-A..F-E placeholder defense) | 4 | 1 HIGH · 3 MED | All |
| **Platform infra** (Service install, ETW, Firewall, Homebrew/APT) | 4 | 1 HIGH · 1 MED · 2 LOW | platform-specific |

**Complexity split**: 21 LOW (40%) · 22 MED (41%) · 10 HIGH (19%).

## Screenshots

*Main window — Manual / Subscribe / Network / Applications / Tools / Free tabs.*

*(Screenshots coming soon.)*

## Manual download

For the one-liner install on all three platforms, see the [**Install**](#install-one-liner-all-three-platforms) section above. Prefer to install by hand? Grab the latest build from [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest):

| File | Platform | What it is |
|---|---|---|
| `VPNRouter-v{version}-win.zip` | 🪟 Windows | Full installer (first install) |
| `VPNRouter-update-v{version}-win.zip` | 🪟 Windows | DLL-only update (if you're already on a recent version) |
| `VPNRouter-*-win.zip.sha256` | 🪟 Windows | SHA256 companion file — auto-updater verifies the download against this before extracting (v2.15.8+) |
| `VPNRouter-v{version}-mac.dmg` | 🍎 macOS | Drag-install DMG (Apple Silicon) with `InstallGuide.html` for one-time sudoers setup |
| `VPNRouter-v{version}-mac.zip` | 🍎 macOS | Raw `.app` bundle (for manual install) |
| `VPNRouter-v{version}-linux-amd64.deb` | 🐧 Linux | Debian/Ubuntu package (systemd service + desktop entry). Install: `sudo dpkg -i <file>.deb` |
| `VPNRouter-v{version}-linux-x86_64.AppImage` | 🐧 Linux | Portable single-file build. `chmod +x`, run, no install needed |
| `VPNRouter-v{version}-linux.tar.gz` | 🐧 Linux | Raw tarball (for manual install or packaging into other formats) |
| `VPNRouter-v{version}-android.apk` | 🤖 Android | Signed APK, API 23+, arm64/arm/x64/x86 universal. **Note**: temporarily absent from prerelease & stable releases since v2.32.2 — CI cannot resolve a preview .NET 10 SDK runtime pack (`Microsoft.NETCore.App.Runtime.Mono.linux-x64 = 10.0.8`) from any public NuGet feed (`NU1102`). The workflow stays in place and can be manually triggered via `gh workflow run "Build Android APK"` to probe upstream progress. APK will return to releases automatically once .NET 10 ships GA or the pack lands on nuget.org. Build from source meanwhile via `dotnet publish VPNRouter.Android/...` if you have internal feed access. Tracked in `MEMORY.md` "Wave 32b NU1102". |
| `*.sha256` companion files | All | SHA256 hash sidecars — auto-updater + CI integrity check verify before extracting. Every binary above ships with a `<file>.sha256` sidecar (Windows `*-win.zip` + `*-update-win.zip`, macOS `*-mac.dmg` + `*-mac.zip`, Linux `*.deb` + `*.AppImage` + `*.tar.gz`). Verify with `sha256sum -c <file>.sha256` on Linux or `Get-FileHash <file>` on Windows. |

Also served automatically every 6 hours:

| File | What it is |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Aggregated ~25 000 public VLESS configs + GeoIP metadata. Consumed by the in-app Free Configs tab. |

Run `VPNRouter.App.exe` as Administrator on Windows (required for TUN adapter + ETW process monitor + Firewall rules). On macOS, follow the in-DMG `InstallGuide.html` for the one-time sudoers entry that lets TUN come up without a password prompt each time. On Linux, `.deb` installs a systemd service that handles the root privileges; `AppImage` requires `sudo` on first launch for TUN/NET_ADMIN capabilities.

## Requirements

- **Windows 10/11 x64** — Administrator rights (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel is not currently packaged. First-run sudoers setup required (guided)
- **Linux x86_64** — kernel 5.6+ (TUN/wireguard), `glibc` 2.31+. Tested on Ubuntu 22.04 / 24.04 and Debian 12. `iptables` or `nftables` for firewall rules.
- **Android 6.0+** (API 23+) — arm64/arm/x64/x86 universal APK. Uses Android's `VpnService` API (no root required). Camera permission only requested when scanning a QR code.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — bundled in the desktop installer
- A VLESS+Reality server, or use the Free Configs tab for a public one

## Build from source

```bash
git clone https://github.com/PavelLizunov/VPNRouter.git
cd VPNRouter
dotnet build VPNRouter.sln
dotnet run --project VPNRouter.App
```

Release build + packaging:

```powershell
# Windows (PowerShell) — produces both full + update ZIPs plus their .sha256
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.32.0"
```

```bash
# macOS DMG — runs on any Mac with .NET 8 SDK
./build-mac.sh 2.32.0
```

```bash
# Linux — .deb + .AppImage + .tar.gz via the same GitHub Actions pipeline
# locally: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

All three platforms (Win ZIP, Mac DMG, Linux .deb/.AppImage/.tar.gz) are built automatically via GitHub Actions on every `v*` tag push — see `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/publish-apt.yml` (APT repo), and `.github/workflows/build-free-pool.yml` (rolling Free Configs pool).

## Architecture

```
VPNRouter.sln (~94k LOC C# across 233 files in 7 projects)
├── VPNRouter.Core                  — 32k LOC · 97 files — services, models, interfaces (cross-platform, zero UI deps)
├── VPNRouter.App                   — 17k LOC · 42 files — Avalonia desktop UI
├── VPNRouter.Android               — 21k LOC · 24 files — Mono.Android + Avalonia.Android
├── VPNRouter.CLI                   —  1k LOC · 12 files — Spectre.Console TUI
├── VPNRouter.Service               —  1k LOC · 3 files  — Windows BackgroundService wrapper
├── VPNRouter.Tools/PoolAggregator  — CI tool building the Free Configs pool.json
└── VPNRouter.Tests                 — 19k LOC · 55 files — 765 xUnit tests + headless Avalonia
```

### Layering

- **`VPNRouter.Core`** is the single source of truth. Zero `Avalonia.*`, `System.Windows.*`, or `Mono.Android.*` references. Platform-specific code gated behind `#if PLATFORM_WINDOWS` / `#if PLATFORM_ANDROID`.
- **Android** doesn't `ProjectReference` Core — it source-links via `<Compile Include="..\VPNRouter.Core\**\*.cs">` in the csproj (works around a multi-target restore loop; will revisit in .NET 9).
- **Free Configs `pool.json`** is built server-side every 6 hours by `VPNRouter.Tools/PoolAggregator` running in GitHub Actions, then served from a rolling `free-pool-latest` Release. Clients fetch + cache.

### Best-practice notes

- **Bilingual UI** — all strings live in `VPNRouter.Core/Localization/Strings.cs` (`Ru ? "..." : "..."`). App/Android use pass-through wrappers; never duplicate keys.
- **Async hygiene** — zero `async void` in Core; UI handlers use the standard `async void EventHandler` pattern; no `.Result` blocking calls outside `VpnEngine.cs:461` (tracked for refactor).
- **Cross-cutting**: every service takes `ILogger?` (Serilog) for diagnostic-grade tracing into `vpnrouter*.log`.
- **No telemetry**. No analytics, no error reporter, no ping-home. UpdateChecker only reads public GitHub Releases API.

### Key services

Core services live in `VPNRouter.Core/Services/` — `VpnEngine` (VPN lifecycle), `SingBoxManager` (sing-box process), `HealthMonitor` (auto-restart + debounce), `ProcessScanner` (process→name resolution), `ConfigGenerator` (sing-box 1.13 JSON), `FirewallManager` (Windows netsh), `EtwProcessMonitor` (real-time process events), `LeakProtection` (config invariant validator), `PlaceholderGuard` (v2.32.3 — known-bad credential filter), plus subsystems for Zapret, Telegram proxy, subscriptions, free configs.

See [`CLAUDE.md`](CLAUDE.md) for the deeper tour, [`plans/feature-catalog-2026-05-17.md`](plans/feature-catalog-2026-05-17.md) for the full feature-flow reference, and [`plans/v3.0-refactor-roadmap.md`](plans/v3.0-refactor-roadmap.md) for the v3.0 modernization plan.

## How it works (high level)

1. Load profile → resolve which process names go through the VPN
2. Generate a sing-box JSON config with the right TUN inbound, VLESS+Reality outbound, and `process_name`-based route rules
3. Start sing-box in TUN mode (creates a virtual adapter)
4. Windows routes all traffic through the adapter; sing-box then splits based on process name matching
5. ETW watches for new processes starting → hot-reload the config via Clash API (no reconnect)
6. On crash — firewall rules block listed processes until sing-box is back (leak protection)

## Privacy & trust

This is a VPN client — you should verify the code before trusting it.

- **No telemetry.** No analytics, no pings home, no bug reporter. Auto-updater only reads public GitHub Releases API.
- **No credential leaks.** Credentials (UUIDs, Reality keys) live in `%ProgramData%\VPNRouter\config.yaml` on disk, never sent anywhere except the sing-box process locally.
- **Reproducible.** Build from source with the commands above. Compare the binary hash with your own build to verify.
- **Open license.** GPL-3.0 — any fork that distributes a binary must also publish its source.

Found a security issue? Open an issue or email the author (see profile).

## Credits

Standing on the shoulders of giants:

- [sing-box](https://github.com/SagerNet/sing-box) — universal proxy platform (GPL-3.0)
- [Avalonia UI](https://avaloniaui.net/) — cross-platform XAML framework (MIT)
- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) — DPI bypass strategies (MIT)
- [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) — MTProto proxy core (MIT)
- [bol-van/zapret](https://github.com/bol-van/zapret) — the original DPI bypass engine (MIT)
- [Serilog](https://serilog.net/) · [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) · [YamlDotNet](https://github.com/aaubry/YamlDotNet)

Public VLESS config aggregators used by the Free Configs tab (14 sources):
[zieng2/wl](https://github.com/zieng2/wl) · [EtoNeYaProject](https://github.com/EtoNeYaProject/etoneyaproject.github.io) · [igareck/vpn-configs-for-russia](https://github.com/igareck/vpn-configs-for-russia) · [CidVpn](https://github.com/CidVpn/cid-vpn-config) · [ByeWhiteLists2](https://github.com/ByeWhiteLists/ByeWhiteLists2) · [nowmeow.pw](https://nowmeow.pw) · [sevcator/5ubscrpt10n](https://github.com/sevcator/5ubscrpt10n) · [ebrasha/free-v2ray-public-list](https://github.com/ebrasha/free-v2ray-public-list) · [barry-far/V2ray-config](https://github.com/barry-far/V2ray-config) · [kort0881/vpn-vless-configs-russia](https://github.com/kort0881/vpn-vless-configs-russia) · [Epodonios/v2ray-configs](https://github.com/Epodonios/v2ray-configs) · [MatinGhanbari/v2ray-configs](https://github.com/MatinGhanbari/v2ray-configs) · [V2RayRoot/V2RayConfig](https://github.com/V2RayRoot/V2RayConfig) · [etoneya.a9fm.site mirror](https://etoneya.a9fm.site)

GeoIP enrichment for the server-side pool aggregator: [ip-api.com](https://ip-api.com) (free tier, batch endpoint, no API key required).

## License

[GPL-3.0-or-later](LICENSE) © 2026 Pavel Lizunov

Forks that distribute binaries must publish their source under the same license.
