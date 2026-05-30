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

- **MVP**: collect → redact → ZIP to Desktop → open folder + a one-liner "review,
  then attach this to your support message." No backend, no upload, no consent
  flow beyond "here's what's inside."
- **+**: a pre-export preview ("this bundle contains X, credentials removed —
  open before sharing?"), the geo-health + reachability summary baked in.
- **Future**: one-click upload to a paste/support endpoint returning a short code
  (needs a backend, an explicit consent checkbox, and a retention/expiry policy —
  bigger surface; defer).

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

**Decision: park it here.** Revisit when support volume justifies the redaction
work, or bundle with the next support-UX pass. The surito-B case is the concrete
motivator; if it recurs, this jumps priority.
