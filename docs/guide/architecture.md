# Architecture and how it works

Part of the [VPNRouter README](../../README.md).

## Architecture

```
VPNRouter.sln
├── VPNRouter.Core                  — services, models and platform adapters
├── VPNRouter.App                   — Avalonia desktop UI
├── VPNRouter.Android               — Android app (built separately)
├── VPNRouter.CLI                   — command-line tools
├── VPNRouter.Service               — Windows service
├── VPNRouter.Tools/PoolAggregator  — Free Configs pool generator
└── VPNRouter.Tests                 — xUnit and headless Avalonia tests
```

### Layering

- **`VPNRouter.Core`** is the single source of truth. Zero `Avalonia.*`, `System.Windows.*`, or `Mono.Android.*` references. Platform-specific code gated behind `#if PLATFORM_WINDOWS` / `#if PLATFORM_ANDROID`.
- **Android** doesn't `ProjectReference` Core — it source-links via `<Compile Include="..\VPNRouter.Core\**\*.cs">` in the csproj (keeps Android restore separate from the desktop net10.0 graph).
- **Free Configs `pool.json`** is built server-side every 6 hours by `VPNRouter.Tools/PoolAggregator` running in GitHub Actions, then served from a rolling `free-pool-latest` Release. Clients fetch + cache.

### Best-practice notes

- **Bilingual UI** — all strings live in `VPNRouter.Core/Localization/Strings.cs` (`Ru ? "..." : "..."`). App/Android use pass-through wrappers; never duplicate keys.
- **Async hygiene** — zero `async void` in Core; UI handlers use the standard `async void EventHandler` pattern; async/await throughout Core with no blocking `.Result` calls on asynchronous paths.
- **Diagnostics & logging** — services log through Serilog (`ILogger?` injection or ambient logger) for diagnostic-grade tracing into `vpnrouter*.log`.
- **Diagnostics** — local logs and crash reports support troubleshooting; see [Privacy & trust](privacy-and-trust.md#privacy--trust) before sharing them.

### Key services

Core services live in `VPNRouter.Core/Services/` — `VpnEngine` (VPN lifecycle), `SingBoxManager` (sing-box process), `HealthMonitor` (auto-restart + debounce), `ProcessScanner` (process→name resolution), `ConfigGenerator` (sing-box JSON), `FirewallManager` (Windows netsh), `EtwProcessMonitor` (real-time process events), `LeakProtection` (config invariant validator), `PlaceholderDefense` (known-bad credential filter), plus subsystems for Zapret, Telegram proxy, subscriptions, free configs.

> **Note**: Desktop builds (Windows, macOS, Linux) default to official `PavelLizunov/sing-box-vpnctl` `v1.14.0-vpnctl.5` release artifacts. Android intentionally retains legacy tooling sing-box 1.13.10 (`libbox.aar`, Android 6.0+ / API 23+) per owner decision rather than migrating all platforms to vpnctl.

See [`CURRENT_STATE.md`](../../CURRENT_STATE.md) for the current platform/build matrix.
Historical plans, including the feature catalog and the v3.0 roadmap, were
moved out of the tree; read them with `git show 6491be4c:plans/<file>`.

## How it works (high level)

1. Load profile → resolve which process names go through the VPN
2. Generate a sing-box JSON config with the right TUN inbound, VLESS+Reality outbound, and `process_name`-based route rules
3. Start sing-box in TUN mode (creates a virtual adapter)
4. Traffic enters the virtual adapter; sing-box then splits based on process name matching
5. On Windows, ETW watches for new processes starting (process scanning on macOS/Linux) → hot-reload the config via Clash API (no reconnect)
6. On failure, enabled firewall protection depends on platform, routing mode and privileges. Linux/macOS kill switches support full-tunnel mode only and remain disarmed in split mode; do not rely on per-process crash blocking there.
