<p align="center">
  <img src="VPNRouter.App/Assets/penguin_logo.png" width="96" alt="VPNRouter logo"/>
</p>

<h1 align="center">VPNRouter</h1>
<p align="center"><b>Virtual Penguin Network</b> — process-based split-tunnel VPN router for Windows, macOS and Linux.</p>

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
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey" alt="Platform"/>
</p>

---

## What it does

Routes **selected applications** through a VLESS+Reality proxy (via [sing-box](https://github.com/SagerNet/sing-box) TUN mode), everything else goes direct to your ISP. Not a full-tunnel VPN — it's a per-process router. Discord goes through the proxy, your bank site stays direct. No manual proxy settings per app.

Add-ons on top of the core router:

- **Free Configs tab** — aggregates **25 000+ public VLESS configs** from 14 open sources, TCP+TLS-validates each server, and verifies real connectivity through a temporary sing-box with an HTTP round-trip. Server-side aggregator (GitHub Actions cron) pre-computes GeoIP every 6 hours. Includes presets (Gaming / Streaming / Chat / Best effort) with latency + bandwidth goals, skip-RU option, per-user subscription sources, and a security-warning dialog on first connect.
- **Servers & Subscriptions testing** (v2.15+) — same TCP+TLS probe and deep verification (spawn temporary sing-box, HTTP trace via SOCKS, 5 MB bandwidth test) now available on your own VLESS servers and subscription pool — not only on the Free Configs tab.
- **Runtime status dashboard** (v2.15+) — three coloured badges in the header show live state of VPN / Zapret / TgProxy, updated every 2 s via process + port probing. Click any badge → jump to the tab that controls it.
- **Resilient autostart** (v2.15+) — Windows Service declares boot dependencies on `Tcpip/Dnscache/Dhcp` and uses exponential backoff (5/10/20/40 s) when launching VPN / Zapret / TgProxy. Transient cold-boot failures no longer leave components stopped.
- **Checksum-verified updates** (v2.15+) — auto-updater downloads each ZIP's `.sha256` companion and aborts on hash mismatch, so a truncated or corrupted download can't silently break the install.
- **Arctic design system** (v2.16+) — semantic-token palette (surfaces, text, borders, state colors, spacing/radii on a 4 px / 11 px base grid), bespoke dark theme that auto-swaps via Avalonia `ThemeDictionaries`, and RGB-inverted penguin logo for dark mode. See `plans/vpnrouter-v2.16-arctic-theme.md`.
- **Service-App coordination hardening** (v2.27+) — installing the Windows service from the UI while VPN is running no longer drops the connection (TunLock check in the service's orphan-sing-box sweep). Advanced autostart panel redesigned into two semantic sections — "At Windows startup (before sign-in)" and "At user sign-in" — grouped by *when* the autostart fires rather than by which Windows mechanism powers it. Status line shows `● Running — PID 1234` prominently, with an indeterminate progress bar during `sc create`/`sc start`. See `plans/vpnrouter-v2.27-service-ux.md`.
- **Core stability: upstream sing-box 1.13.10 + TUN auto-detect** (v2.27.2) — all three platforms now bundle the official upstream sing-box 1.13.10 (was custom rebuild of 1.13.7), picking up the `process_name`-matching fix that regressed in 1.13.9 along with 5 other point-release bug fixes. Runtime re-apply auto-detects structural changes on TUN layer (interface name, IPv4 subnet, MTU, auto/strict route, IPv6 toggle, exclude list) and escalates to a full process restart — Clash API hot-reload can't re-lay kernel-level adapter state, so these used to silently "succeed" while the live adapter kept old values. Covered by 12 new xUnit regression tests and live-verified `tools/live-test-r1.ps1` harness. See `plans/vpnrouter-core-stability-audit.md`.
- **DPI bypass (Zapret)** — integrated [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) for platforms blocked by DPI without needing a proxy.
- **Telegram proxy** — embedded MTProto proxy ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)) for Telegram-only bypass.
- **Custom sing-box configs** — bring your own JSON (TUIC, Hysteria2, Shadowsocks), keep per-process routing.
- **Subscriptions** — multiple VLESS subscription URLs with auto-refresh, unified server pool.
- **Windows Service mode** — runs at boot, survives user logoff.
- **macOS support** — full Avalonia UI + sing-box TUN on Apple Silicon. Automated DMG builds via GitHub Actions.
- **Bilingual UI** — full RU/EN translation, switchable at runtime.

## Screenshots

*Main window — Manual / Subscribe / Network / Applications / Tools / Free tabs.*

*(Screenshots coming soon.)*

## Download

Grab the latest build from [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest):

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

Also served automatically every 6 hours:

| File | What it is |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Aggregated ~25 000 public VLESS configs + GeoIP metadata. Consumed by the in-app Free Configs tab. |

Run `VPNRouter.App.exe` as Administrator on Windows (required for TUN adapter + ETW process monitor + Firewall rules). On macOS, follow the in-DMG `InstallGuide.html` for the one-time sudoers entry that lets TUN come up without a password prompt each time. On Linux, `.deb` installs a systemd service that handles the root privileges; `AppImage` requires `sudo` on first launch for TUN/NET_ADMIN capabilities.

## Requirements

- **Windows 10/11 x64** — Administrator rights (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel is not currently packaged. First-run sudoers setup required (guided)
- **Linux x86_64** — kernel 5.6+ (TUN/wireguard), `glibc` 2.31+. Tested on Ubuntu 22.04 / 24.04 and Debian 12. `iptables` or `nftables` for firewall rules.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — bundled in the installer
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
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.27.2"
```

```bash
# macOS DMG — runs on any Mac with .NET 8 SDK
./build-mac.sh 2.27.2
```

```bash
# Linux — .deb + .AppImage + .tar.gz via the same GitHub Actions pipeline
# locally: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

All three platforms (Win ZIP, Mac DMG, Linux .deb/.AppImage/.tar.gz) are built automatically via GitHub Actions on every `v*` tag push — see `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/publish-apt.yml` (APT repo), and `.github/workflows/build-free-pool.yml` (rolling Free Configs pool).

## Architecture

```
VPNRouter.sln
├── VPNRouter.Core                  — services, models, interfaces (cross-platform)
├── VPNRouter.App                   — Avalonia UI (cross-platform desktop)
├── VPNRouter.CLI                   — CLI tool (Spectre.Console)
├── VPNRouter.Service               — Windows Service wrapper
├── VPNRouter.Tools/PoolAggregator  — CI tool that builds the Free Configs pool.json
└── VPNRouter.Tests                 — xUnit
```

Core services live in `VPNRouter.Core/Services/` — `VpnEngine`, `SingBoxManager`, `HealthMonitor`, `ProcessScanner`, `ConfigGenerator`, `FirewallManager`, `EtwProcessMonitor`, `LeakProtection`, plus subsystems for Zapret, Telegram proxy, subscriptions, free configs, etc. See [`CLAUDE.md`](CLAUDE.md) for the deeper tour.

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
