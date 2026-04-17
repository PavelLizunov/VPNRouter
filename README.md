<p align="center">
  <img src="VPNRouter.App/Assets/penguin_logo.png" width="96" alt="VPNRouter logo"/>
</p>

<h1 align="center">VPNRouter</h1>
<p align="center"><b>Virtual Penguin Network</b> — process-based split-tunnel VPN router for Windows (+macOS).</p>

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
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS-lightgrey" alt="Platform"/>
</p>

---

## What it does

Routes **selected applications** through a VLESS+Reality proxy (via [sing-box](https://github.com/SagerNet/sing-box) TUN mode), everything else goes direct to your ISP. Not a full-tunnel VPN — it's a per-process router. Discord goes through the proxy, your bank site stays direct. No manual proxy settings per app.

Add-ons on top of the core router:

- **Free Configs tab** — aggregates public VLESS configs from 6 open sources, TCP-pings each server, sorts by latency. Click to connect.
- **DPI bypass (Zapret)** — integrated [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) for platforms blocked by DPI without needing a proxy.
- **Telegram proxy** — embedded MTProto proxy ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)) for Telegram-only bypass.
- **Custom sing-box configs** — bring your own JSON (TUIC, Hysteria2, Shadowsocks), keep per-process routing.
- **Subscriptions** — multiple VLESS subscription URLs with auto-refresh, unified server pool.
- **Windows Service mode** — runs at boot, survives user logoff.

## Screenshots

*Main window — Manual / Subscribe / Network / Applications / Tools / Free tabs.*

*(Screenshots coming soon.)*

## Download

Grab the latest build from [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest):

| File | What it is |
|---|---|
| `VPNRouter-v{version}-win.zip` | Full installer (first install) |
| `VPNRouter-update-v{version}-win.zip` | DLL-only update (if you're already on a recent version) |

Run `VPNRouter.App.exe` as Administrator (required for TUN adapter + ETW process monitor + Windows Firewall rules).

## Requirements

- Windows 10/11 x64 (macOS support is partial — TUN works, some features are Windows-only)
- Administrator rights (TUN, firewall, ETW)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — bundled in the installer ZIP
- A VLESS+Reality server (or use the Free Configs tab for a public one)

## Build from source

```bash
git clone https://github.com/PavelLizunov/VPNRouter.git
cd VPNRouter
dotnet build VPNRouter.sln
dotnet run --project VPNRouter.App
```

Release build + packaging (Windows only, Administrator PowerShell):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.13.2"
```

## Architecture

```
VPNRouter.sln
├── VPNRouter.Core      — services, models, interfaces (cross-platform)
├── VPNRouter.App       — Avalonia UI (cross-platform desktop)
├── VPNRouter.CLI       — CLI tool (Spectre.Console)
├── VPNRouter.Service   — Windows Service wrapper
└── VPNRouter.Tests     — xUnit
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

Public VLESS config aggregators used by the Free Configs tab:
[zieng2/wl](https://github.com/zieng2/wl) · [EtoNeYaProject](https://github.com/EtoNeYaProject/etoneyaproject.github.io) · [igareck/vpn-configs-for-russia](https://github.com/igareck/vpn-configs-for-russia) · [CidVpn](https://github.com/CidVpn/cid-vpn-config) · [ByeWhiteLists2](https://github.com/ByeWhiteLists/ByeWhiteLists2) · [nowmeow.pw](https://nowmeow.pw)

## License

[GPL-3.0-or-later](LICENSE) © 2026 Pavel Lizunov

Forks that distribute binaries must publish their source under the same license.
