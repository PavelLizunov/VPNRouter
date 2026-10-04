# Features

Part of the [VPNRouter README](../../README.md).

## What it does

Routes application traffic through a proxy using [sing-box](https://github.com/SagerNet/sing-box) TUN mode. In the default split/include mode, selected applications use the proxy and the remaining traffic uses the direct route, subject to configured rules. Split/exclude mode keeps selected applications direct; full-tunnel mode routes traffic through the proxy apart from configured exceptions. Applications do not need individual proxy settings.

### Cross-platform core

- **Split-tunnel routing** — choose applications from the process list and select whether to include them in the proxy route or exclude them from it.
- **VLESS+Reality + custom configs** — use the built-in VLESS setup or bring your own sing-box JSON (TUIC, Hysteria2, Shadowsocks). Per-process routing is injected either way.
- **Subscriptions** — paste one or more subscription URLs, servers auto-refresh into a unified pool. Desktop add commands accept only absolute HTTP(S) URLs for subscriptions and user-provided free-config sources; private and loopback HTTP(S) addresses remain allowed. Capability-aware providers can publish a VLESS target that must dial through a paired entry server; missing chain metadata or entry availability fails closed with no direct target fallback.
- **DNS configuration** — generated DNS routes depend on routing mode, strict-DNS settings and custom rules. The built-in direct DoH resolver and synthesized fallback use Google `8.8.8.8`; configured LAN suffixes can use the OS resolver. Custom configurations pass through DNS/routing injection and validation, so inspect the generated configuration rather than assuming all original DNS settings remain unchanged.
- **Server testing** — one-click TCP+TLS probe on any server. Deep verification (real HTTP round-trip + 5 MB bandwidth) for your own servers and subscription pools.
- **Setup and diagnostics wizard (desktop)** — checks configuration, TUN, DNS and reachability, can reset MTU to the safe default `1420`, preserves the chosen routing mode, and offers undo plus redacted diagnostics export. Safe Mode remains a separate temporary start.
- **Safe rollback** — the desktop app shows up to three previous stable versions only when their `.sha256` companion is available, verifies the selected archive before installing, and asks for explicit confirmation. Before a downgrade it saves a copy of `config.yaml`.
- **Status dashboard + Arctic dark theme + RU/EN UI** — live VPN / Zapret / TgProxy badges in the header, custom Avalonia theme, fully translated interface.

### Platform specifics

- **Windows** — UAC elevation; optional Windows Service for boot-time autostart that survives user logoff.
- **macOS** — Apple Silicon native; one-time sudoers setup from the DMG gives passwordless TUN afterwards.
- **Linux** — POSIX capabilities (`cap_net_admin`, `cap_net_bind_service`) for passwordless TUN, applied by the `.deb` postinst (`setcap`); session autostart via the `.desktop` entry. No systemd service / boot-time daemon yet.

### Windows-only add-ons *(optional)*

These are thin wrappers around upstream projects — they aren't part of the core router and don't work on macOS / Linux. Skip them unless you specifically need DPI bypass or Telegram-only routing.

- **DPI bypass (Zapret)** — integrates [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube). Downloaded on demand from the Tools tab. Useful when your ISP blocks a site by DPI and a full proxy isn't wanted.
- **Telegram proxy** — embedded MTProto proxy ([Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)) for Telegram-only bypass.

### Bonus: Free Configs tab

The Free Configs tab collects public VLESS endpoints. A scheduled server-side job runs every six hours; pool size, availability and validation results vary. Public endpoints are operated by third parties: a successful connectivity test does not establish operator trust.
