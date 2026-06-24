---
name: diagnose-config
description: Diagnose VPNRouter config / connectivity issues by reading the user's config.yaml + current.json + recent vpnrouter*.log. Trace ConfigMode → Vless.Servers → outbounds → flow. Catches silent leaks, ConfigMode mismatches, missing proxy outbounds.
when: User reports VPN issue ("не подключается", "роутится не туда", "ping mismatch"), shares config.yaml / current.json / log files, or sends server-side error like "flow mismatch: expected xtls-rprx-vision but got none".
---

# Diagnose VPNRouter config / connectivity

Methodical 3-file walkthrough used to debug v2.28.1 silent leak (line-by-line
in `plans/session-night-shift-2026-04-25.md`).

## Three files always

User shares (or we ask for):
1. `%ProgramData%\VPNRouter\config.yaml` — UI/state in YAML
2. `%ProgramData%\VPNRouter\config\current.json` — what sing-box actually loaded
3. `%ProgramData%\VPNRouter\logs\vpnrouter.log` (tail ~300 lines)

User helper:
```powershell
$dst = "$env:USERPROFILE\Desktop\vpnrouter-debug"
mkdir $dst -Force | Out-Null
Copy-Item "C:\ProgramData\VPNRouter\config.yaml" "$dst\"
Copy-Item "C:\ProgramData\VPNRouter\config\current.json" "$dst\"
Get-ChildItem "C:\ProgramData\VPNRouter\logs\vpnrouter*.log" |
  Sort-Object LastWriteTime -Desc | Select -First 1 |
  ForEach-Object { Get-Content $_ -Tail 300 } | Out-File "$dst\vpnrouter-tail.log"
explorer $dst
```

Log файл UTF-16 LE — для grep'ов: `iconv -f UTF-16LE -t UTF-8 file.log | grep ...`.

## Diagnostic order

### 1. config.yaml — что в state

Ищи:
- `app.config_mode`: `subscribe` / `generated` / `custom` — это **режим клиента**.
- `app.subscriptions[]`: подписки, каждая с `enabled` + `servers[]` (ground truth для subscribe mode).
- `app.active_subscription_server`: какой сервер выбран в Subscribe.
- `app.custom_config` / `custom_configs[]` / `active_custom_config`: для custom mode.
- `vless.servers[]`: **обычно пуст в subscribe mode** (заполняется in-memory только).
- `vless.active_server`: для VLESS manual mode.
- `app.routing_mode`: `split` / `full`.
- `update.channel`: `stable` / `experimental`.

**Red flags:**
- `config_mode: custom` + `custom_config: ''` + `custom_configs: []` → broken state, v2.28.2-r2 fixed UI guard но старые YAML могут иметь.
- `config_mode: subscribe` + `subscriptions: []` → broken; bug в SettingsMigrator.
- `vless.servers[]` непустой в subscribe mode → возможно legacy migration left-over.

### 2. current.json — что sing-box реально загрузил

Ключевое:
- `outbounds[]`: должно быть `proxy` (vless или urltest), `direct`, `dns-direct`. **Если `proxy` отсутствует** — silent leak (v2.28.1 bug). Hard guard в v2.28.2 throws теперь.
- `outbounds[].flow`: `xtls-rprx-vision` обязателен на каждом vless outbound для Reality.
- `outbounds[].tls.reality.public_key` / `short_id` / `server_name`: должны совпадать с config.yaml subscription servers.
- `route.rules[].outbound`: каждый "proxy" / "proxy-udp" / "direct" должен EXIST в outbounds.
- `route.final`: `direct` для split tunnel, `proxy` для full tunnel.
- `dns.servers[].detour`: указывает на outbound tag (`proxy` для vpn-dns, `dns-direct` для local-dns).

**Red flags:**
- Route rule с `outbound: "proxy"` но в outbounds[] нет `proxy` → **silent leak**.
- `outbounds: [{type:"direct"},{type:"direct"}]` only — без vless → broken; v2.28.2 hard guard throws.
- `tls.reality.public_key: ""` → handshake failure.
- `dns.servers[].address` field present (legacy 1.11 format) — должно быть конвертировано в type-based; `StripUnsupportedFeatures` мигрирует custom configs.

### 3. log tail — startup + last Apply

Ищи timeline:
```
[VpnEngine] Loading profiles...
[VpnEngine] Profile: <name> (<n> rules)
[VpnEngine] Scanning processes...
[VlessServersResolver] Aggregated <N> server(s) from <M> active subscription(s)   ← v2.28.2+
[ProcessScanner] Resolved <N> process names for profile '<name>'
[SingBoxManager] Starting sing-box with config: ...current.json
[SingBoxManager] sing-box started (PID <n>)
[VpnEngine] TUN ready after <ms>ms
[VpnEngine] Connected (PID <n>)
```

**Red flags в логе:**
- `[SingBoxManager] sing-box crashed (exit code: -1)` сразу после Start → проверить `singbox.log` (отдельный файл).
- `[VpnEngine] Apply: validation failed, skipping reload` (v2.28.2+) → rejected броken JSON (good).
- `[VpnEngine] Full-tunnel mode — ignoring ActiveProfile` → check routing_mode.
- `[ProcessScanner] Resolved 0 process names for profile 'FullTunnel'` → expected for full tunnel.
- `Subscription returned 0 servers, keeping cached` → провайдер вернул пусто (v2.28.1+ retains cache).

## Common patterns

### "VPN connected but Discord goes direct"
Likely one of:
- ConfigMode='subscribe' + Vless.Servers пуст → silent leak (fixed v2.28.2). Если на старой версии — обновить.
- Process_name case mismatch — sing-box matches `Discord.exe`, не `discord.exe`. Process_name array в current.json должен иметь exact filesystem casing.
- RU bypass over-matching — geo-IP routing puts Discord IPs into "RU" bucket → direct. v2.27.1-r2 fix dropped geoip-ru, kept geosite-ru.

### "flow mismatch: expected xtls-rprx-vision, but got none" на сервере
- `outbounds[].flow` field missing/empty в current.json → check VlessUriParser → check ConfigGenerator.BuildVlessOutbound line 405.
- Or proxy outbound отсутствует, но sing-box urltest probes к серверу с raw TCP (v2.28.1 silent leak).

### "Apply Selected подключает к подписке а не выбранному free config"
v2.28.3-r4 fixed: `ApplyFreeConfigAsync` теперь делает `IsSubscribeMode = false`. Если на старой версии — обновить.

### "Старые верифицированные конфиги пропадают после Refresh"
v2.28.3-r5 fixed: `FreeConfigAggregator.PreservePreviousValidation` сохраняет verified+recent-Ok cache entries.

### "ConfigMode='custom' без custom_config — VPN не запускается"
v2.28.2-r2 fix: SaveSettings guard. Manual recovery: открыть config.yaml, line 9 → `config_mode: subscribe` (или generated, в зависимости от того что есть).

## Close the loop — pin the repro as a test (mandatory)

Audit item 14 (2026-06-25). A user diag that reproduces a bug MUST become a
failing test BEFORE the fix ships (METHODOLOGY §6.1/6.2 — pin the contract in the
same commit). Once root cause is isolated:

1. Extract the minimal repro from the diag fixture (config.yaml / current.json /
   the proving log line) into a characterization or contract test under
   `VPNRouter.Tests/` — e.g. a `ConfigSanityCheck` / `LeakProtection` / parser
   case, or a pure-function pin like `VpnEngineProbeFailoverGateTests`.
2. Confirm it goes RED on the current code (proves it captures the bug).
3. The fix commit turns it GREEN. Make sure it runs in the right pre-commit
   Gate 2 path-scope (see `.githooks/pre-commit`), not only in CI.
4. If the bug is real-but-deferred, append it to `plans/OPEN-DEFECTS.md` so the
   cut-stable gate (`tools/check-open-p0.ps1`) blocks on it.

Без этого user-reported P0 фиксятся по log-string-match и молча регрессят — это и
случилось с auto-failover (regression-тест появился только ПОСЛЕ второго
инцидента, diag 20260624-235243).

## Tools

- `singbox.exe check -c file.json` — validate sing-box JSON schema.
- `gh release view v2.28.X --repo PavelLizunov/VPNRouter --json assets` — какая версия что несёт.
- `git log --oneline | head -20` — последние правки.

## NOT to do

- Не предлагать "Reset Settings" как первый шаг — потеряет user state.
- Не редактировать config.yaml вручную если это live state — сначала Stop VPN.
- Не игнорировать `current.json` — это **ground truth** что sing-box реально делает; YAML может расходиться.
