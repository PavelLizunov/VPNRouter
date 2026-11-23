# Privacy

VPNRouter is a VPN/proxy client. It has **no telemetry**: no analytics, no
crash/error reporting service, no "ping home". This document states exactly what
data exists and where it goes.

## Stored locally

Under `%ProgramData%\VPNRouter\` on Windows (or the platform equivalent —
`~/.config/vpnrouter` on Linux, `~/Library/Application Support/VPNRouter` on
macOS, and the app sandbox on Android):

- `config.yaml` — your settings, including connection **credentials**
  (subscription URLs/tokens, VLESS UUIDs, Reality public keys / short IDs,
  custom-config secrets). Plaintext, on your machine.
- `config/current.json` — the generated sing-box config actually loaded.
- `logs/` — diagnostic logs (`vpnrouter*.log`, `singbox.log`, `update.log`).
  May contain server hostnames and config fragments.
- `cache/` — subscription / free-config caches and the Zapret probe cache.
- `state.json` — runtime state (PID, last status).

These files stay on your device. The requests below send the fields needed
for each feature.

## Network connections VPNRouter makes

- **Your VPN/proxy servers** — sing-box connects to the servers you configured
  (subscription, manual VLESS, or a free config you selected). This is the
  app's purpose.
- **Update check** — reads the **public** GitHub Releases API to detect a newer
  version; downloads come from GitHub release assets. No account or identifier
  is sent beyond a normal HTTPS request.
- **Subscriptions** — if you add a subscription URL, the server list is fetched
  from that URL (the provider you chose).
- **DeepSeek setup assistant** — when you submit a request, VPNRouter
  authenticates to the official vpnctl endpoint with your saved subscription.
  vpnctl forwards the request text, interface language, connection state and
  identifiers for available window controls to the configured model. The
  subscription credential goes only to vpnctl. VPNRouter keeps field
  contents, screenshots, the app list and routing settings on your device.
  When you choose crash analysis, VPNRouter reads the most recent saved
  crash report and sends the model four diagnostic fields: whether the
  error was fatal, its category, the affected component and a recognized
  log signal. The crash report and log text stay on your device.
- **Free Configs** — if you use that tab, a pre-aggregated `pool.json` is
  fetched from this project's GitHub releases, and candidate servers may be
  probed (TCP/TLS) to test reachability. If `pool.json` is unavailable, the app
  fetches the public source lists directly and may send the candidate servers'
  IP addresses to `ip-api.com` (plain HTTP batch endpoint) to look up their
  countries; that service sees your IP address.
- **Zapret / Telegram proxy** — if you enable these, their binaries are
  downloaded on demand from their upstream GitHub releases.

## What VPNRouter does NOT do

- No analytics or usage tracking.
- No automatic crash uploads.
- Your browsing and routed-app list stay local to the VPN client. The app
  list is used to generate the local sing-box config.

## Your responsibility

Credentials live in `config.yaml` in plaintext — treat that file as sensitive.
VPNRouter restricts its Windows data directory to the current user,
Administrators and SYSTEM. On Linux and macOS, the data/config directories use
owner-only permissions (`0700`) and secret-bearing generated configuration
files use owner read/write permissions (`0600`) when the filesystem supports
POSIX modes. These permissions protect against other local users; they do not
protect against malware or an administrator running as your account.

If you share logs or a diagnostics bundle for support, redact tokens / UUIDs /
keys first.
