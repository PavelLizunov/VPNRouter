# "Send diagnostics" button — design note (NOT scheduled, just thinking)

**Status**: idea only (2026-05-30, after surito's ping + RU-bypass cases needed
manual PowerShell file collection twice in one session). Do NOT implement yet.

## Why

Every user-bug diagnosis this project does (surito ×2, brat, stas, PinkuDani…)
starts with "send me config.yaml + current.json + the logs". Today that means
handing the user a PowerShell snippet. Most users won't run a script → we get
partial info → slow back-and-forth (surito's RU-bypass is stuck on exactly this:
"по B нету пока логах"). A one-click **Export diagnostics** button turns a
30-minute support thread into a 10-second attachment. This is the single highest
-leverage support-UX improvement available.

It productizes what the `diagnose-config` skill already does by hand.

## What to collect (the bundle)

| Item | Source | Why |
|---|---|---|
| `config.yaml` **(redacted)** | `%ProgramData%\VPNRouter\config.yaml` | mode, full/split, BypassRu, DnsLeakLockdown |
| `current.json` **(redacted)** | `…\config\current.json` | what sing-box actually loaded — rule_sets, route/DNS rules, outbounds |
| `vpnrouter*.log` (tail ~500) | `…\logs\` | startup, Apply, `[GeoData]` refresh success/fail |
| `singbox.log` (tail) | `…\logs\` | per-connection routing (only useful if log level debug) |
| geo `.srs` sizes + dates | `…\geo\*.srs` | **the surito-B smoking gun** (stale broad vs tld-ru ~150B) |
| reachability probe | live | can the user reach `raw.githubusercontent.com` (geo-refresh source)? RU often can't |
| env summary | app | version, OS build, arch, connected-state, admin, channel |

## The hard part — REDACTION (cardinal risk)

`current.json` + `config.yaml` contain **secrets**: subscription URL token,
VLESS `uuid`, Shadowsocks/Hy2/TUIC `password`, Reality `short_id`. Exporting them
raw = the user leaks their own VPN credentials into a Discord attachment. The
export MUST sanitize, and **fail safe** (if a field's sensitivity is unknown,
redact it):

- Redact: `uuid`, `password`, `short_id`, the subscription-URL token segment,
  any `*_key`/`*secret*`/`token` field.
- Keep (needed for diagnosis, not secret): `server`/host, ports, `server_name`,
  Reality `public_key` (public by definition), rule_set paths/tags, route+DNS
  rules, outbound *types*, log level, strategy.
- A redaction unit test is mandatory: assert the output contains **none** of the
  known-secret values from a fixture config. A bug here is a credential leak, so
  this gets the same rigor as the leak-path.

## Output options (tiered)

**DESTINATION DECIDED (2026-05-30, user): Variant 0 — we host NOTHING.** The
button collects → redacts → writes a ZIP to Desktop → opens the folder. The user
attaches it wherever they already get support (Discord / Telegram / GitHub issue).
Rationale: zero new infra, zero data-processor / retention liability, zero
privacy exposure beyond what the user explicitly chooses to share. "Куда
принимаем логи" → туда, где уже идёт саппорт; ничего не хостим.

- **MVP (the decision)**: collect → redact → ZIP to Desktop → open folder + a
  one-liner "review, then attach this to your support message."
- **+ later**: a pre-export preview ("this bundle contains X, credentials removed
  — open before sharing?"), the geo-health + reachability summary baked in.
- **Deferred upload path (only if support volume justifies)**: one-click upload
  via a **Cloudflare Worker + R2** drop (RU-reachable, free tier, no server to
  run, TTL-expiry you control, front via a `ninitux.com` subdomain) → returns a
  short code the user shares. NOT a Discord webhook / TG bot token in the client
  (abusable/leaks) and NOT a public paste service (privacy + often RU-blocked).
  Requires explicit consent + auto-expiry. Park until needed.

## Placement

Settings → a small "Поддержка / Diagnostics" section, next to the existing
"Open log" affordance. Button: «Собрать диагностику» / "Export diagnostics".

## Scope / effort / risk

- MVP effort: moderate. File collection is trivial (we already do it in
  `post-ship-install-launch.ps1` + the diagnose-config helper); the YAML/JSON
  redaction pass + the redaction tests are the real work.
- Risk: **credential leak via incomplete redaction** — the one thing that must
  not ship broken. Belt-and-suspenders: redact by allowlist of safe keys rather
  than denylist of known-secret keys, so an unknown/new secret field defaults to
  redacted.
- Reuse: the redaction logic could also harden any future telemetry/crash
  reporting.

## Open questions for later

- ZIP vs single text blob to clipboard (clipboard is even lower-friction but
  loses the binary `.srs` sizes — could include them as a text line).
- Do we ever want auto-attach to the in-app update/feedback flow?
- Cross-platform: macOS/Linux paths differ (`~/.config/vpnrouter`), and the
  button should work there too (the collector is platform-aware already in
  AppPaths).

---

**Decision (2026-05-30): destination SETTLED = Variant 0 (local ZIP, no backend).**
Implementation timing still parked — revisit when support volume justifies the
redaction work, or bundle with the next support-UX pass. When built, it's a
self-contained App feature (collector + redactor + ZIP + Settings button); no
infra ticket. The surito-B case is the concrete motivator; if it recurs, this
jumps priority.
