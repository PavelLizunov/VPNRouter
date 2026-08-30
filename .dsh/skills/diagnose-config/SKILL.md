---
name: diagnose-config
description: Diagnose VPNRouter config / connectivity issues by reading the user's config.yaml + current.json + recent vpnrouter*.log. Trace ConfigMode -> Vless.Servers -> outbounds -> flow. Catches silent leaks, ConfigMode mismatches, missing proxy outbounds.
whenToUse: Diagnose failed VPN connections, subscription or custom-config problems, and flow mismatch errors.
---

# Diagnose VPNRouter config / connectivity

Methodical 3-file walkthrough used to debug config and connectivity issues.

## Three evidence layers

Prefer the application's **redacted diagnostics export**. Raw `config.yaml`, `current.json`, and logs can contain subscription URLs, UUIDs, Reality keys, server addresses, and tokens; never ask the user to paste or upload those files unredacted.

When the files are available inside an explicitly authorized local/worker workspace, inspect them in place and report only sanitized fields:

1. `%ProgramData%\VPNRouter\config.yaml` — UI/state in YAML.
2. `%ProgramData%\VPNRouter\config\current.json` — the configuration sing-box actually loaded.
3. `%ProgramData%\VPNRouter\logs\vpnrouter*.log` — bounded recent lifecycle window.

Redact subscription URLs, UUIDs, keys, short IDs, tokens, endpoint host/IP values, user paths, and unrelated log history before content crosses the trusted machine boundary. Current Serilog files are UTF-8 unless an inspected file proves otherwise; do not assume UTF-16.

## Diagnostic order

### 1. config.yaml — state check

Search for:
- `app.config_mode`: `subscribe` / `generated` / `custom` — client routing mode.
- `app.subscriptions[]`: subscriptions, each with `enabled` + `servers[]` (ground truth for subscribe mode).
- `app.active_subscription_server`: selected server in Subscribe mode.
- `app.custom_config` / `custom_configs[]` / `active_custom_config`: for custom mode.
- `vless.servers[]`: in-memory / legacy server list.
- `vless.active_server`: active server for VLESS manual mode.
- `app.routing_mode`: `split` / `full`.
- `update.channel`: `stable` / `experimental`.

**Red flags:**
- `config_mode: custom` + `custom_config: ''` + `custom_configs: []` → broken state.
- `config_mode: subscribe` + `subscriptions: []` → broken state.
- `vless.servers[]` populated in subscribe mode → legacy migration leftover.

### 2. current.json — sing-box runtime configuration

Key components:
- `outbounds[]`: MUST include `proxy` (vless/urltest), `direct`, `dns-direct`. If `proxy` is missing → silent leak.
- `outbounds[].flow`: `xtls-rprx-vision` required on each vless outbound for Reality.
- `outbounds[].tls.reality.public_key` / `short_id` / `server_name`: must match config.yaml subscription server details.
- `route.rules[].outbound`: every target outbound tag (`proxy`, `proxy-udp`, `direct`) MUST exist in `outbounds`.
- `route.final`: `direct` for split tunnel, `proxy` for full tunnel.
- `dns.servers[].detour`: points to outbound tag (`proxy` for vpn-dns, `dns-direct` for local-dns).

**Red flags:**
- Route rule with `outbound: "proxy"` but no `proxy` tag in `outbounds[]` → **silent leak**.
- `outbounds: [{type:"direct"},{type:"direct"}]` without vless outbound.
- `tls.reality.public_key: ""` → handshake failure.
- `dns.servers[].address` field present (legacy format) → must be type-based.

### 3. Log tail — startup timeline & last Apply

Look for:
```
[VpnEngine] Loading profiles...
[VpnEngine] Profile: <name> (<n> rules)
[VpnEngine] Scanning processes...
[VlessServersResolver] Aggregated <N> server(s) from <M> active subscription(s)
[ProcessScanner] Resolved <N> process names for profile '<name>'
[SingBoxManager] Starting sing-box with config: ...current.json
[SingBoxManager] sing-box started (PID <n>)
[VpnEngine] TUN ready after <ms>ms
[VpnEngine] Connected (PID <n>)
```

**Red flags in logs:**
- `[SingBoxManager] sing-box crashed (exit code: -1)` immediately after start → inspect `singbox.log`.
- `[VpnEngine] Apply: validation failed, skipping reload` → rejected invalid JSON.
- `[VpnEngine] Full-tunnel mode — ignoring ActiveProfile` → check routing_mode.
- `Subscription returned 0 servers, keeping cached` → empty provider response.

## Common patterns

### "VPN connected but traffic goes direct"
- ConfigMode='subscribe' + Vless.Servers empty → missing outbound.
- Process name casing mismatch — process name array in current.json must match exact filesystem casing.
- Over-broad IP routing rules redirecting traffic to direct.

### "flow mismatch: expected xtls-rprx-vision, but got none" on server
- `outbounds[].flow` field missing or empty in current.json → check VlessUriParser / ConfigGenerator.
- Missing proxy outbound causing sing-box to attempt raw TCP probes.

### "ConfigMode='custom' without custom_config — VPN does not start"
- Prefer changing mode through the UI after preserving a backup. If recovery requires a manual edit, stop VPNRouter first, change only `config_mode` to a valid populated mode (`subscribe` or `generated`), and retain the original file for rollback.

## Tools

- `sing-box.exe check -c file.json` — validate sing-box JSON schema on Windows when the binary is present.
- `gh release view v2.28.X --repo PavelLizunov/VPNRouter --json assets` — inspect release assets.
- `git log --oneline | head -20` — check recent commits.

## NOT to do

- Do not suggest "Reset Settings" as a first step — wipes user state.
- Do not edit `config.yaml` manually while VPN is running — stop VPN first.
- Do not ignore `current.json` — it is the ground truth of what sing-box actually runs.
