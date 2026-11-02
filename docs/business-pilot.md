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

Autostart launches the UI in the tray for the Windows account that ran Setup;
it does not connect automatically. A different administrator account at the UAC
prompt requires the administrator to set up the employee's autostart separately.
