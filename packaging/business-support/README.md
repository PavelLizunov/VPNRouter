# Business support intake and one-file collector

The employee receives their personal `VPNRouterBusiness-Diagnostics.exe`, opens
it and presses Send diagnostics. Collection and upload happen inside the tool;
there is no extraction, PowerShell command, ZIP selection or URL entry. The
previously delivered VPNRouter Business application does not need replacement.

`packaging/windows/support/SupportApp.cs` is a small Windows Forms wrapper built
with the existing Windows .NET Framework compiler. It embeds the reviewed
PowerShell 5.1 collector/sender and private settings. It does not load VPNRouter
or .NET 10. Windows and the employee must permit the executable and the standard
Windows PowerShell process; this build does not bypass Windows security tools.

## Build and delivery

Run `build-support.ps1 -SettingsFile <private-file> -OutputDir <private-output>`
on prepared Windows CI only, after compiler/capacity preflight. Settings contain
an absolute HTTPS `endpoint` and an individually issued upload-only `token`.
Keep both the settings and configured EXE private. Embedded credentials can be
read by that employee and are not a defense against extraction. Do not publish
the configured executable as a public release or share one identity across staff.

The `business_support=true` manual job in `build-ui-mcp.yml` consumes ephemeral
`VPNROUTER_BUSINESS_SUPPORT_SETTINGS` and `VPNROUTER_BUSINESS_SUPPORT_DELIVERY_KEY`
repository secrets and uploads only the encrypted EXE, its SHA256 and source SHA.
Remove these task-specific CI secrets after retrieval. Public CI uses only fake
settings to compile the executable, verify embedded source, render its isolated
window and test success/failure/cancel state with a fake workflow. It does not
claim real process launch or network delivery from those UI fixtures.

## Receiver contract

`receiver.py` uses the existing Linux Python runtime behind the existing HTTPS
ingress. The public surface is POST `/api/reports` plus GET `/health`. There is
no public download, file listing, identity provisioning or administration API.
An individual Bearer upload identity is required. The private registry stores
only identity hashes and employee labels; it is loaded on each request so
disabling/removing a record revokes further uploads without a service restart.

POST accepts a raw `application/zip` body with a fixed Content-Length, at most
24 MiB. Only the exact allowlisted collector entries and supported ZIP methods
are accepted; reports are never extracted or executed. The central-directory,
entry count, per-entry and expanded-size checks bound archive parsing. Uploads
have an absolute 30-second deadline, four concurrent slots and ten attempts per
identity/hour. Storage is limited to 512 MiB, including owned temporary/metadata
files; reports expire after seven days with a periodic one-minute sweep. Orphaned
temporary uploads are recovered at startup. These bounds are deployment defaults,
not evidence of an abuse/load test.

A success response is sent after validation and durable file writes. Private
metadata links each receipt to the employee label/hash, receive time and archive
SHA without storing the token. Identical payloads from different identities have
separate receipts. Network failure can occur after persistence but before the
client receives acknowledgement; retries reuse the ZIP and are deduplicated.
The client retains collected reports privately after failure.
An optional bounded summary.device object supplies computer_name and user_name.
The receiver allowlists these into separately labeled client_reported_device
receipt metadata. They never replace authenticated employee identity, receipt
paths or authorization; older reports without the fields remain valid.

## Deployment and operations

Use the provided systemd unit, a dedicated `vpnrouter-support` account and a
private `/var/lib/vpnrouter-business-support` state directory. Keep the receiver
on the existing hosting network; its `--trusted-proxy` must identify the actual
ingress. The Caddy template exposes only intake/health with its existing HTTPS
handling and a request-body ceiling. Substitute the selected hostname/upstream
from verified Homelab configuration, validate Caddy and reload that service.
Preserve all unrelated landing, VPN subscription, DNS and mail routes.

Issue an upload identity with the receiver's `issue` CLI as the service account,
using different private paths for `--keys` and `--output`; keep output outside
the reports directory. It serializes registry updates and refuses an existing
client-settings file. Transfer settings programmatically into the private build
without printing them. Retrieve reports only through existing owner management
access, never a public static-file route. Reports are untrusted client data and
automatic secret masking is best effort; do not publish them or raw logs.

Production verification must check authenticated real HTTPS delivery of a
synthetic report, rejected unauthorized/oversized uploads, absence of public
report retrieval, service/storage permissions and unchanged existing site routes.
Exercise the real one-button executable on fixed WINBRAT with repository identity
guards; fake UI workflows do not replace that test. No live desktop capture.
