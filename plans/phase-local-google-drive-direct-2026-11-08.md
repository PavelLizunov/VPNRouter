# Local Google Drive direct routing

## Intent and scope
- Configure the installed Omarchy VPNRouter session to bypass VPN for Google Drive downloads.
- Reuse persistent custom rules and the service protocol; no product source changes.
- Keep full-tunnel mode, the active server, existing rules and Russian bypass.
- Shared `googleusercontent.com` download hosts also serve other Google content.
- Do not bypass all of `google.com` or `googleapis.com`.

## Evidence before changes
- Installed backend: `vpnrouter-headless 0.1.0.r59.gf6b39db-1`.
- User service active, plugin attached, sing-box running through TUN.
- Saved mode `full`; runtime final `proxy`; custom rules empty.
- Direct Wi-Fi HTTPS probes: Drive HTTP 302, download endpoint HTTP 400, valid TLS.
- Official host reference: https://knowledge.workspace.google.com/admin/drive/drive-and-sites-firewall-and-proxy-settings

## Procedure and verification
- Temporarily detach the plugin using its normal enable/disable lifecycle.
- Read current revision and rules; submit additive `rules.set` with revision checking.
- Re-enable the plugin after applying; verify service and runtime configuration.
- Probe Drive and download hosts through the normal TUN and correlate outbound logs.
- Verify an unrelated destination still uses `proxy`.
- HTTP endpoint probes do not prove a signed-in private file download or its throughput.

## Outcome
- Applied one persistent direct `domain_suffix` rule via revision-checked `rules.set`.
- Hosts: `drive.google.com`, `drive.usercontent.google.com`,
  `drive-data-export.usercontent.google.com`, `drive-data-export-eu.usercontent.google.com`,
  `googledrive.com`, `googleusercontent.com`, `docs.google.com`, `drive.googleapis.com`.
- Service stayed active with PID 2376; sing-box restarted from PID 8931 to 25125.
- Session returned `connected`, no error, same active server and full routing mode.
- Plugin re-enabled and its `--attach` relay restored. No plugin source changes.
- Saved custom rule and generated runtime rule match; final remains `proxy`.
- Russian bypass remains present. DNS final remains `vpn-dns`.
- Packaged `sing-box check -c .../current.json` exited 0.
- Normal TUN HTTPS probes, valid TLS: Drive 302, download endpoint 400, Docs 302.
- Each curl source port was matched to its sing-box connection ID and `direct` outbound:
  Drive 1068939092, download 3968152094, Docs 1566430867.
- YouTube robots.txt returned 200 through `proxy`, connection ID 1980098447.
- Initial domain-only log correlation failed because outbound lines log IPs;
  corrected verification correlates source port, connection ID and destination IP.
- No private signed-in download, throughput benchmark or browser connection reuse test.
