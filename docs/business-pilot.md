# VPNRouter Business pilot

Business is an opt-in build of the same desktop app and Core. Build with
`VPNRouterEdition=Business` and `BusinessConnectionFile` pointing to an individual
private share-link list or supported subscription response outside the checkout.
There is no separate VPN engine or forked connection lifecycle.

The standard build keeps its current UI and settings. Business uses
`%ProgramData%\VPNRouterBusiness`, its own installer identity and autostart entry.
Only one VPNRouter tunnel may run at a time; the existing global TUN lock is shared.
Business has one Start/Stop action and two modes. Switch modes while disconnected.
The standard tray and health recovery remain in use. Tray diagnostics exports
the existing redacted diagnostic bundle locally; review it before sharing.

The employee support path is a separate personal
`VPNRouterBusiness-Diagnostics.exe`: open it and press Send diagnostics. It
embeds the collector, sender and individually configured upload identity. The
employee does not extract scripts, select ZIPs or enter server settings, and
the already distributed Business app does not need replacement. The configured
EXE is a private artifact; the upload-only identity is revocable at the receiver.
The [support intake runbook](../packaging/business-support/README.md) owns the
build, private delivery, receiver limits and verification contract. The script
archive described below remains an administrator fallback.

If the application cannot start, the independent Windows PowerShell 5.1
collector in `packaging/windows/support/` needs neither the app nor a .NET SDK.
Extract the separate `VPNRouterBusiness-Support.zip` and double-click
`Collect-VPNRouterBusiness-Diagnostics.cmd`. Future Business installers include
the same helper in the Start menu. The previously delivered pilot needs only
the separate support archive, not an application reinstall. Collection is local
and does not start/stop VPN or change system network settings. Missing/unreadable
inputs are recorded in `summary.json`; an empty report is not a successful health
check. Run as the employee with elevation if their log ACL requires it.

The fallback includes at most five app logs, two core logs, one launch log,
five crash reports and the exact legacy launch-error file, reading at most
1 MiB each. The legacy fallback covers the first pilot's failed elevation log
under `%ProgramData%\VPNRouter\logs`; no other consumer logs are read.
It does not copy configuration,
subscriptions, binaries or arbitrary files. Linked inputs are refused. Automatic
masking is best effort; review reports before private delivery because logs can
still identify domains, IPs and applications. This smaller fallback does not
replace the richer tray export when the app works.

`Send-VPNRouterBusiness-Diagnostics.ps1` can explicitly upload a reviewed ZIP to
the owner's HTTPS endpoint: a raw `application/zip` POST, optional individual
Bearer token from a private file, and `X-VPNRouter-Report-Format:
vpnrouter-business-support/1`. The receiver should return 2xx only after saving
the report. The sender validates TLS normally, refuses redirects, limits the
file to 24 MiB and the request to 30 seconds, does not retry, and retains the ZIP
on every outcome. It does not print endpoint/token/response details. No endpoint
or credential is embedded by default, and no receiver is deployed by these tools.
See the [employee and administrator instructions](../packaging/windows/support/README.ru.txt).

## Routing policy

`packaging/windows/business-rules.json` is the supplied Main profile converted to
native `CustomRule` objects, all enabled. The owner explicitly resolved `in-mag.ru`
as Direct. Six process names retain their original casing. Native `domain`,
`domain_suffix`, `ip_cidr` and `process_name` rules work with the existing generator.
The attachment contained no IP/subnet entries.

- Default mode: selected processes/resources use Proxy, other traffic is Direct.
- Full mode: final outbound is Proxy; Direct exceptions remain active.
- Both modes enable the existing Russian bypass. Following the owner's later
  requirement, the existing `toggles_first` priority puts Russian GeoIP/geosite
  bypass before explicit Proxy rules (including `pinterest.ru`). Direct exceptions
  precede application routing. Packaged geo files are required for Business;
  missing data blocks generation instead of silently disabling the exception.
- Business DNS mirrors domain/process routing. Direct exceptions and Russian
  resources use direct DNS; proxy resources use VPN DNS. Windows DNS process
  attribution alone is insufficient, so domain rules are explicit.

## UIS acceptance

The [official UIS notice](https://www.uiscom.ru/info/) names `uiscom.ru`,
`uiscom.co`, `comagic.ru`, `app.callbackhunter.com`, and the alternate SIP host
`195.211.120.35`. The preset routes these service domains and that IP directly.
The published SIP host is also excluded from TUN at the OS-route level.

These are not a complete list of RTP/STUN/TURN endpoints. Obtain the customer's
actual SIP/media servers and supported CIDRs from UIS. Add confirmed media CIDRs
to `Tun.RouteExcludeAddress` before distributing a revised individual preset.
Include mode does not engage the exclude-mode true-split driver: ordinary Direct
traffic can still traverse the TUN user-space stack, affecting UDP/NAT behavior.
DNS and destination exceptions matter even with a process exception. Browser
softphones share the browser process, so excluding an entire browser is unsafe.

On the customer PC compare VPN off, split on and full on: registration, inbound
and outbound calls, audio in both directions for several minutes, hold/transfer,
and reconnect after a network interruption. Record DNS answers, SIP/media remote
IPs, physical/TUN routes and UDP endpoints locally. Export the redacted diagnostic
bundle from the tray. Do not capture or publish SIP passwords, call audio, raw
packets, live configuration or a live desktop. UIS is unverified until these
customer tests pass.

## Private build and delivery

Run `tools/build-business.ps1` only in exact-SHA CI or an authorized preflighted
worker. It calls the existing `build.ps1`, bundles Russian geo files and builds
`packaging/windows/business.iss` with Inno Setup. The result is an unsigned pilot
installer, not a public release. Source version remains `AppVersion.Version`.

The packaging job in `build-ui-mcp.yml` is explicitly dispatched with
`business_pilot=true`. It reads individual config and an ephemeral delivery key
from repository secrets; uploads only a symmetrically encrypted installer. Never
upload the build folder, plaintext installer, config, test data or logs containing
endpoint details. Remove task-specific CI secrets after private retrieval.

Distribute each employee's installer privately. Embedded credentials are readable
by that employee and are not a protection against extraction. Never give this
same identity to all 20 employees or publish it as a public release. Revoke the
employee's identity on the VPN server, including active sessions. Rebuilding the
Business variant from updated common source applies shared fixes while retaining
the presets. Consumer self-update/web repair is disabled in the pilot so it
cannot replace Business with a consumer binary. Automatic fleet management and
config rotation are deferred by owner direction; replace the individual installer
to update the embedded connection or rules.

## Employee instruction

1. Install the personal VPNRouter Business installer provided by your administrator.
2. Open VPNRouter Business and press Connect. Nothing needs importing or configuring.
3. If another resource needs VPN, disconnect, select the full mode and reconnect.
4. To stop, press Disconnect. Closing the window leaves it in the tray.
5. For a problem, use Save diagnostics in the tray and contact your administrator.

The Business window also exposes one choice per bundled direct VLESS host. The
complete endpoint pool is retained for the engine's TCP/UDP sibling handling. For
an unavailable endpoint, disconnect, choose another VPN server and reconnect.
The selector is disabled during connection and while connected. Selection is
saved immediately and retained on restart when its name still resolves within
the refreshed embedded preset; a removed choice falls back to its usable VLESS
default. Saved variants on the same host resolve to its visible node choice.
Bundled endpoint names must be nonempty and case-insensitively unique; an
ambiguous preset is refused before changing settings. Embedded credentials and
routing still come from the administrator's
bundle. This adds a manual recovery action, not proof of automatic failover or
of the cause of the November 3 office outage.

Autostart launches the UI in the tray for the Windows account that ran Setup;
it does not connect automatically. A different administrator account at the UAC
prompt requires the administrator to set up the employee's autostart separately.

The [Russian Windows acceptance checklist](business-pilot-test.ru.txt) accompanies
the personal installer. The sanitized [WINBRAT result](../plans/business-pilot-winbrat-2026-11-02.json)
and [executed orchestration script](../plans/business-pilot-live-verify-2026-11-02.ps1)
record the pilot scope; they do not establish customer UIS or long-term memory safety.
