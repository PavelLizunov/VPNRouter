VPNRouter diagnostics bundle
============================

This archive was generated locally on your machine. Nothing was uploaded.
Credentials have been removed: VLESS UUIDs, passwords, Reality short IDs,
subscription tokens and unknown fields are replaced with "***". Only
non-secret values (server host, ports, routing rules, log lines) are kept.

PLEASE OPEN AND REVIEW THIS ARCHIVE before attaching it to a support
message, so you are comfortable with what it contains. Then attach it
wherever you already get support (Discord / Telegram / GitHub issue).

Contents:
  summary.txt            - version, OS, channel, connected state, health check
  config.redacted.yaml   - your settings (secrets removed)
  current.redacted.json  - the config sing-box actually loaded (secrets removed)
  state.redacted.json    - runtime state (PID etc.)
  vpnrouter*.log          - app logs, last few days (scrubbed)
  singbox-tail.log        - sing-box log, current (scrubbed)
  singbox-old-tail.log    - sing-box log, previous rotation if present (scrubbed)
  slipstream-tail.log     - DNS-tunnel transport log, if dns-tunnel was used (scrubbed)
  slipstream-prev-tail.log- DNS-tunnel transport log, previous session if present (scrubbed)
  geo-manifest.txt        - geo rule file sizes & dates (not the files)