# Sample Rules — Import test files

Examples for `Network → Rules → Import`. Two tiers:

## Tier 1 — Smoke samples (12 rules each, all 3 actions)

| File | Format | Purpose |
|---|---|---|
| `example-rules.csv` | CSV with header `action,type,value,comment,enabled` | Spreadsheet-friendly, easy to edit |
| `example-rules.json` | VPNRouter native bare array | Lossless, default Export shape |
| `example-rules-singbox.json` | sing-box `route.rules` | NekoBox / Hiddify import |
| `example-rules-minimal.csv` | CSV (3 rules) | Quick smoke test |
| `example-rules.txt` | Plain-text Edit-mode | **Paste into Text mode**, NOT Import |

## Tier 2 — Real-world configs

Practical rule sets built from common usage. Each ~30 rules, drop-in
ready, all use only supported types (`domain` / `domain_suffix` /
`domain_keyword` / `ip_cidr` / `port` / `port_range` / `network` /
`process_name` / `geosite` / `geoip`).

| File | Rules | What it does |
|---|---|---|
| `realworld-russian-bypass.csv` | 30 direct rules | RU services bypass VPN: gov, banks, telecom, marketplaces. Use when you need RU sites to work via your real IP. |
| `realworld-adblock.csv` | 30 block rules | Top ad networks + tracking: Google, Facebook, Outbrain, Taboola, Criteo, Hotjar, Mixpanel, etc. |
| `realworld-streaming-proxy.csv` | 30 proxy rules | Geo-restricted streaming + AI services: YouTube, Netflix, Spotify, Discord, OpenAI, Claude, Gemini, X, Instagram, etc. |
| `realworld-combined.csv` | 64 rules | All three sets merged + LAN auto-direct preset. Drop-in full setup. |

## Sources

The real-world configs draw from common practice in:

* https://github.com/runetfreedom/russia-blocked-geosite — RU blocked
  geosite categories (we used a curated subset of services known to
  reject foreign IPs, not the full geosite list).
* https://github.com/Loyalsoldier/v2ray-rules-dat — community geosite
  / geoip lists.
* https://github.com/SagerNet/sing-geosite — sing-box official
  geosite source.
* https://easylist.to / AdGuard DNS filter — popular ad domains.
* General industry knowledge of streaming / AI service domains.

For broader coverage, use the `geosite` action type with sing-box
rule_set names directly:
* `block` + `geosite` + `ads` — full ads blocklist
* `direct` + `geosite` + `ru` — full RU bypass
* `direct` + `geoip` + `ru` — RU IP-based bypass

## Test flow

1. Open VPNRouter → Network → Rules.
2. (Optional) Bulk → ✕ Удалить все правила → Удалить → start clean.
3. Import → choose any sample.
4. Verify rules appear in Cards mode with correct action / type / value.
5. Optional: switch to Text mode (read-only grouped) to verify
   per-action grouping.
6. Optional: switch to Edit mode to see the canonical text format.
7. Apply VPN to verify runtime behavior (block / direct / proxy).

## Round-trip

Each format should round-trip cleanly:
* Import `example-rules.csv` → Export → CSV → diff (only ordering /
  comment encoding may differ).
* Same for JSON variants.

## sing-box import note

`example-rules-singbox.json` has multi-match rules (e.g.
`process_name: ["discord", "discord.exe"]`). VPNRouter expands
these into multiple entries (one rule per match field per value)
because our schema is one-match-per-rule. Comments on imported rules
will say `sing-box import #N`.

## Troubleshooting

If import shows errors:
* Check the file isn't UTF-16 / BOM-prefixed (use plain UTF-8).
* CSV: header row should start with `action,...`. Lines starting
  with `#` are skipped as comments.
* JSON: must be a bare array `[ ... ]` OR a `{ "rules": [...] }`
  wrapper (both accepted from r20+).
* sing-box JSON: must have a `rules` array at root, `.rules`, or
  `.route.rules`.
