# Bug triage — Alena: full-tunnel routes Adobe → Photoshop relicense → "used firewall" → VPN traffic died

**Date:** 2026-06-05
**Reporter:** Alena (subscription tags `~alena_ruslan` / `~pyrojokk`), Windows 11 26100, v2.41.0 stable
**Diag:** `Z:\VPNRouter-diagnostics-20260605-223449.zip` (captured 22:34, Connected=False)
**Status:** triaged, NOT fixed — needs one clarifying question + product decision

## Триггер (user words)
"у меня фотошоп начал просить лицензию. я решила воспользоваться фаерволом и у
меня перестал переливаться трафик через впн."

## Симптом
1. Photoshop suddenly demanded re-activation / license.
2. She "used a firewall" (to block Adobe phoning home — classic crack-keep trick).
3. After that, traffic stopped flowing through the VPN.

## Что показывает диагностика (факты, не догадки)
- `routing_mode: full` (full-tunnel) + `routing_apps_mode: include` with a 50-app
  include list (browsers, claude, telegram, …). **Full-tunnel IGNORES the include
  list** — log: `[StartupPipeline] Full-tunnel mode — ignoring ActiveProfile … skipping process scan`, profile `FullTunnel (0 rules)`. So EVERYTHING tunnels, incl. Photoshop/Adobe.
- Adobe traffic confirmed routed THROUGH the tunnel:
  `dns: exchanged … cc-api-data.adobe.io`, `crs.cr.adobe.com`, `www.adobe.com`
  via `outbound/vless[proxy]`. → Adobe licensing hit a German exit IP → cracked
  Photoshop got flagged / phoned home and demanded re-activation. **This is the
  real root of her Photoshop problem.**
- VPNRouter's OWN firewall layers were NOT actively blocking at capture:
  - `dns_leak_lockdown: false` (logs: "DNS leak lockdown disabled — rules deleted" on every connect).
  - `block_on_vpn_fail`: `[Firewall] ENABLED 0 block rules (VPN down — leak protection active)` → `_managedRules.Count==0` because full-tunnel/FullTunnel profile has 0 per-process block rules. Kill-switch effectively inert.
  - `[Firewall] All VPNRouter firewall rules deleted` at 22:02:55 — nothing lingering at 22:34.
  → **The "firewall" that killed her VPN traffic was most likely EXTERNAL** (Windows
  Defender Firewall or a third-party tool she used to block Adobe), which also
  caught sing-box / the TUN. VPNRouter did not self-inflict a block per these logs.
- Reliability side-notes (real, but not the headline):
  - `21:22:13 [ERR] [SingBoxManager] sing-box crashed (exit code: -1)` — a GENUINE
    mid-session crash (HealthMonitor restarted 1/5). No Go panic/FATAL in
    singbox-tail near it → likely externally killed (AV/firewall?) or transient.
    Traffic "stopped flowing" for the restart window.
  - `Remove-NetAdapter : command not found` (CommandNotFoundException) on EVERY
    connect/disconnect — her PowerShell lacks the NetAdapter module. netsh fallback
    handles it, but it's noisy and risks stale TUN adapters on such boxes.

## Root cause (honest)
- **Primary (her actual pain):** full-tunnel routes region-sensitive apps (Adobe
  Creative Cloud licensing) through a foreign exit IP → relicense prompt. Her
  curated include list — which would have kept Photoshop direct in SPLIT mode —
  is silently ignored under full-tunnel.
- **Secondary (the "VPN died"):** not reproduced as a VPNRouter-caused block.
  Most plausibly an external firewall rule she added to stop Adobe, which also
  blocked sing-box's outbound to the proxy / disrupted the TUN. Needs confirmation.

## Open question to relay to Alena (before any fix)
1. Какой именно "фаервол"? (а) тумблер в VPNRouter [Сеть → DNS leak lockdown /
   kill-switch], (б) Windows Defender Firewall, (в) сторонняя программа?
2. Что именно блокировала — Photoshop.exe / Adobe-домены / "всё подряд"?
3. Сейчас VPN вообще подключается (статус "Подключено")? Интернет есть когда VPN выкл?

## Fix strategy (actionable VPNRouter items — product decision needed)
- **A. Full-tunnel + include-list = silent no-op → UX trap (P1).** When
  `routing_mode=full` AND a non-empty `routing_apps_include/exclude` exists, the UI
  must say so loudly (banner/disable list / offer "switch to split to honor your
  app list"). She built a 50-app list that does nothing.
- **B. "Keep this app on my real IP" / exclude region-sensitive apps (P1, the real
  fix for her).** Make excluding Adobe/Photoshop (and banks, gov, Steam) from the
  tunnel one click, so licensing isn't routed through a foreign IP and she never
  needs an external firewall. Possibly a curated "always-direct" preset (Adobe
  licensing domains) mirroring `bypass_russian_traffic`.
- **C. sing-box exit -1 mid-session crash (P2 reliability).** Understand/observe
  the genuine crash; HealthMonitor recovered, but worth a watch.
- **D. Remove-NetAdapter cmdlet-missing noise (P3).** Detect absence once, fall
  back to netsh silently, stop logging the multi-line CommandNotFoundException
  on every connect/disconnect.

## Acceptance (when we act)
- [ ] Clarified which firewall she used + whether VPN reconnects.
- [ ] Full-tunnel + app-list contradiction surfaced in UI.
- [ ] One-click "exclude app from VPN" reachable for region-sensitive apps.
- [ ] (opt) Adobe-licensing always-direct preset.

## Оценка
A: ~S (UI warn). B: ~M (exclude UX already half-exists, needs discoverability +
maybe preset). C/D: ~S each, low priority. Not ship-blocking; backlog.
