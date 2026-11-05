# VPNRouter Business pilot

Business is an opt-in build of the same desktop app and Core. Build with
`VPNRouterEdition=Business` and `BusinessConnectionFile` pointing to an individual
private share-link list or supported subscription response outside the checkout.
There is no separate VPN engine or forked connection lifecycle.

The standard build keeps its current UI and settings. Business uses
`%ProgramData%\VPNRouterBusiness`, its own installer identity and autostart entry.
Only one VPNRouter tunnel may run at a time; the existing global TUN lock is shared.
Business has one Start/Stop action and two modes. Switch modes while disconnected.
The kebab menu contains the persisted Russian/English selector, diagnostics and
refresh actions. The main window keeps connection, server and routing mode only;
there is no Details panel. Diagnostic progress/failure/receipt stays in the menu.
The standard tray and health recovery remain in use. Tray diagnostics exports
the existing redacted diagnostic bundle locally; review it before sharing.

The Business kebab menu provides diagnostics actions.
Collect saves an allowlisted, masked log report locally. Send collects if needed
and uploads only after that explicit action; success appears only after the
receiver acknowledges delivery. A failed or cancelled upload keeps the archive,
and retry reuses it for receiver deduplication. A new Send after acknowledged
delivery collects fresh logs. Open report folder allows inspection/manual delivery.
The same reviewed PowerShell collector is embedded in the app; collection does
not change the tunnel. An individual upload identity is required in private build
metadata. Older static bundles without it allow collection and explain that
upload is not configured. The existing tray export remains available.
The optional report summary includes bounded client-reported computer and Windows
user names. The receiver records them separately from the authenticated employee
upload identity; these names are not proof of hardware identity.

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
on every outcome. It does not print endpoint/token/response details. The standalone scripts have no embedded endpoint or credential; the private app
build metadata configures its upload identity. These scripts do not deploy a receiver.
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
rule rotation remain separate work. Subscription entries can now update from the
immutable HTTPS reference described below; updating embedded rules still requires
a new individual installer.

## Employee instruction

1. Install the personal VPNRouter Business installer provided by your administrator.
2. Open VPNRouter Business and press Connect. Nothing needs importing or configuring.
3. If another resource needs VPN, disconnect, select the full mode and reconnect.
4. To stop, press Disconnect. Closing the window leaves it in the tray.
5. For a problem, press Send in the window's Diagnostics area. Collect and Open
   report folder allow inspection first. If the app cannot launch, use the
   independent personal diagnostics EXE.

The Business window exposes one choice per direct VLESS host in the current list. The
complete endpoint pool is retained for the engine's TCP/UDP sibling handling. For
an unavailable endpoint, disconnect, choose another VPN server and reconnect.
The selector is disabled during connection and while connected. Selection saves
immediately. A live refresh preserves the selected physical host even when its
label changes; otherwise it resolves the refreshed name or falls back to the
first usable VLESS host. Saved variants on that host resolve to its visible node
choice. All endpoint names must be nonempty and case-insensitively unique before
state changes. Future connected-service builds retain a source-bound cache from
the embedded HTTPS reference; legacy static builds use the embedded preset.
Routing rules continue to come from the administrator's bundle. This manual
recovery action does not establish automatic failover or the office outage cause.

The server picker shows a traffic-light port-reachability indicator and TCP
connection latency. Green means a response within 800 ms, yellow means a slow
or inconclusive response, red means the endpoint could not be reached, and grey
means not checked. These are port checks, not VPN authentication or proxied-traffic
tests; a red result does not establish that the remote machine itself is down.
Checks run on opening the visible window, every 30 seconds while disconnected,
and on Refresh. Connecting, connected, hidden and closed states cancel checks
and clear stale results. Measurements stay in memory and are not uploaded.

Both the in-app Diagnostics area and separate diagnostics EXE collect/upload only
on explicit employee action. There is no continuous background statistics upload.
The separate EXE remains available when the Business application cannot launch.

Autostart launches the UI in the tray for the Windows account that ran Setup;
it does not connect automatically. A different administrator account at the UAC
prompt requires the administrator to set up the employee's autostart separately.

The [Russian Windows acceptance checklist](business-pilot-test.ru.txt) accompanies
the personal installer. The sanitized [WINBRAT result](../plans/business-pilot-winbrat-2026-11-02.json)
and [executed orchestration script](../plans/business-pilot-live-verify-2026-11-02.ps1)
record the pilot scope; they do not establish customer UIS or long-term memory safety.

## Live subscription and country display

Future connected-service builds embed a fallback response and a private immutable
HTTPS subscription reference. The visible disconnected app refreshes at startup,
on Update list and hourly. Check remains the separate TCP port/latency action.
A complete validated list replaces the pool only while idle; the selected host,
split/full mode and all TCP/UDP variants are preserved. A missing choice falls
back to the first available physical host. Failures, empty/invalid responses and
cancellation retain the previous pool. Refresh never reconnects an active tunnel.

The source fingerprint and last valid response share the existing atomic settings
save, so restart can use a source-bound cache offline. Invalid or differently
bound cache falls back to the embedded response. Cache data is secret-bearing,
stays in the protected Business settings and is masked by structured diagnostics.
If a YAML config/backup cannot be parsed, diagnostic export omits its content
instead of guessing multiline credential boundaries.
Upload/subscription/node metadata service settings always come from the embedded bundle; a
downloaded response cannot replace their target or upload identity. HTTPS uses
normal certificate validation, no redirects, bounded size/time and no automatic
upload or report retry.

The existing private BusinessConnectionFile may be the API JSON wrapper with an
additional `business` object: `subscription_url` is the individual HTTPS source,
and `support` contains `endpoint` plus the individually issued upload-only
`token`. Optional `nodes_url` is a separate immutable HTTPS metadata reference
using an existing device/subscription identity. Invalid optional metadata disables
only metrics; it never prevents the VPN from starting. The `config` field remains
the base64 share-link fallback. Keep this
whole input outside Git, and pass it through the existing ephemeral encrypted
private CI path. The private delivery validation requires both services, while
legacy offline bundles remain readable. Neither an admin password nor a vpnctl
admin token belongs in this object.

Country flags are local assets or vector drawings, with known names translated
into the selected language. Configured node metadata supplies an explicit country
from vpnctld's local GeoIP data. A matched null country remains unknown. Without
a matched metadata row, recognized advertised country labels remain the fallback;
opaque provider names are never guessed. Unsupported country codes keep their code
and have no flag. Country is not proof of VPN egress location. No private endpoint
is sent to an external geo API.

## Server load from vpnctl

Configured builds read a grant-scoped node snapshot using the existing individual
device/subscription identity. The separate backend change is tracked in vpnctl
PR #311; source implementation does not prove production activation. Deployment
and exact-source live acceptance are recorded in the task outcome.

Each server row shows Unix one-minute load and occupied RAM. Unix load is not CPU
percent; CPU count/percent are not supplied. The tooltip describes the metrics and
sample age. Missing or inconsistent/future samples show no data; stale values are
explicitly marked. Server-derived age advances with monotonic time, independent
of the guest wall clock, and is not reset by hiding the window, failed requests,
language changes or list reconstruction.

Polling runs every five minutes while the Business window is visible, including
while connected; age labels refresh every thirty seconds. Refresh requests a
snapshot immediately. Requests are single-flight, HTTPS-only and bounded; hiding
or closing cancels work. Metadata never mutates VPN selection/settings or tunnel
state. Older bundles without nodes_url show no metrics. No admin credential or
continuous client statistics upload is added. Subscription-fetch counters still
cannot represent node resource load, VPN-online users or employee counts.

## Server-driven support replies

The app advertises support-analysis-v1 with the existing explicit report POST.
Any durable2xx acknowledgement remains received even if optional analysis fails.
A version1 reply may contain bounded plaintext message_ru/message_en; pending
results are polled only under the original trusted HTTPS report endpoint and
individual upload identity, for at most eight seconds. Missing/older404/invalid
results use a receipt-confirmed fallback. No arbitrary URL, executable module,
command or provider credential is accepted from a reply. The intake GET exposes
only that identity's sanitized analysis, never archives or employee metadata.
Server analysis/prompt/model/text can evolve behind this stable protocol without
replacing the client. Gemini itself is not configured by this compatibility
change; raw diagnostic archives are not sent to an external provider.
