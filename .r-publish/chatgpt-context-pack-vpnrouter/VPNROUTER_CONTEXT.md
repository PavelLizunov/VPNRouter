# VPNRouter Project Context

This file is the stable project brief. It should not be treated as a release note.

## Product

VPNRouter is a cross-platform VPN routing app. Its main job is to let users decide which traffic goes through VPN and which traffic stays direct.

Target users are people who need censorship bypass, app-level routing, and practical diagnostics without becoming networking experts.

Supported platforms:

- Windows
- macOS
- Linux
- Android

Desktop stack:

- .NET 8
- Avalonia UI
- sing-box TUN engine
- VLESS/Reality and subscription-based configs
- optional Windows helpers: True Split, Zapret/DPI bypass, Telegram proxy, Windows Service/autostart

## Main User Jobs

- Paste a server or subscription and connect.
- Pick split tunnel or full tunnel.
- Send selected apps through VPN.
- Send selected apps outside VPN.
- Keep local devices and LAN resources reachable.
- Diagnose why one app disconnects, lags, or cannot connect.
- Use helper tools for DPI bypass or Telegram when needed.

## Routing Model

VPNRouter has two app-routing concepts:

- "Через VPN": selected apps go through VPN.
- "Мимо VPN": selected apps bypass VPN.

These are separate lists, not two views of the same list. They usually contain different app families.

Typical "Через VPN" apps:

- Discord
- AI tools
- blocked global services
- privacy-sensitive browsers
- streaming apps that need VPN

Typical "Мимо VPN" apps:

- Russian desktop apps and services
- banks and local-government apps
- game launchers and games with anti-cheats
- local development tools
- remote LAN/admin tools
- local mirrors and package caches

Full tunnel means most traffic goes through VPN, but this must not break local networking.

## Local Network Invariant

Local networks must never be captured by the VPN TUN route, regardless of split/full mode.

Important local ranges:

- `10.0.0.0/8`
- `172.16.0.0/12`
- `192.168.0.0/16`
- `169.254.0.0/16`
- `127.0.0.0/8`
- `::1/128`
- `fe80::/10`
- `fc00::/7`

This should be an effective generated routing rule, not a fragile user setting.

## True Split

True Split is a Windows-only driver-level process split mechanism. It is different from ordinary sing-box process rules.

Important behavior:

- It may need elevated install/start actions.
- It can conflict with Mullvad/Amnezia-like split-tunnel drivers.
- It should fail safely and explain the conflict.
- It should not silently break normal connectivity.

## UI Map

Simple mode is the first screen for normal users:

- config/subscription input
- split/full selection
- connect/disconnect
- status and entry to advanced mode

Advanced mode has pages such as:

- Servers
- Subscription
- Settings / routing
- Applications
- Tools
- Public/free configs
- Windows-only helper pages: Zapret/DPI bypass, Telegram proxy

## UX Principles

- This is a practical network tool, not a marketing site.
- Users need clear state and safe next actions.
- Compact windows matter.
- Warnings should be short and specific.
- Do not hide the real mode behind vague labels.
- Avoid adding more buttons when one clear action is enough.

## Diagnostic Style

When debugging, separate these layers:

- app process routing
- DNS routing and DNS timeouts
- TUN routes and local network exclusions
- proxy transport
- MTU/path ceiling
- app-level protocol behavior
- external service blocking

Do not assume a successful ping proves an app will work. Games and voice apps can fail at UDP/session/WebSocket layers while generic connectivity looks fine.
