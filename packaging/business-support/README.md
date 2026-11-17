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
ingress. The public surface is POST `/api/reports`, authenticated GET
`/api/reports/{report_id}/analysis` and GET `/health`. There is no public download,
file listing, identity provisioning or administration API.
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

Clients can opt in with `X-VPNRouter-Capabilities: support-analysis-v1` on POST.
Successful legacy POST responses remain exactly `stored` and `report_id`.
Opted-in responses additionally contain `analysis`, for example
`{"version":1,"state":"unavailable"}`. Its states are `unavailable`, `pending`
and `ready`; optional `message_ru` and `message_en` contain plain text. Each
message is nonempty and limited to 300 Unicode characters and 300 UTF-16 code
units, with no control, format or surrogate characters, markup, URL or shell
command syntax. Only these fields are exposed. `ready` requires a valid message;
absent or invalid analysis becomes `unavailable` without changing `stored:true`.
The receiver provides compatibility only and does not invoke an analyzer.

GET accepts the same currently enabled Bearer upload identity through the trusted
ingress. The receipt ID is exactly 64 lowercase hexadecimal characters. It
returns only `stored`, `report_id` and `analysis` for an existing archive whose
receipt matches that identity and ID. Unknown and foreign receipts both return
the same generic 404; private employee, identity and device metadata never leave
the receiver. GET accepts existing receipt JSON up to 64 KiB. Duplicate
opted-in POSTs may return an analysis already stored there. A separate trusted
server analyzer may atomically update the optional `analysis` object in that
receipt, preserving all original fields and its retention timestamp. It must
not add public URLs, commands, extra state files or change upload authorization.

## Deployment and operations

Use the provided systemd unit, a dedicated `vpnrouter-support` account and a
private `/var/lib/vpnrouter-business-support` state directory. Keep the receiver
on the existing hosting network; its `--trusted-proxy` must identify the actual
ingress. Expose only intake, the scoped analysis route and health with existing
HTTPS handling and a request-body ceiling; the Caddy template must be updated
explicitly before analysis polling is enabled. Substitute the selected hostname
and upstream
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

## Private Corp diagnostics inbox and worker

The optional `serve --inbox-config <private-json>` enables only the private
`/internal/corp-diagnostics/reports` list, detail and retry endpoints described in
[the runtime contract](../../docs/business-diagnostics-runtime-contract.md).
Keep the existing public ingress allowlist: **never forward `/internal/`**.
The private config has `schema: 1`, `corp_identity_sha256` and
`service_token_file`. The token file holds a separate 32-512 character service
Bearer capability. An upload credential cannot read the inbox. The configured
identity must still be enabled in `--keys` on every request. List pagination
orders receive time and ID descending; detail and retry never expose foreign
identities or archive URLs. `device_label` is a client-reported computer name.

Run the stdlib worker using the existing Python runtime, as the same dedicated
account and against the same reports directory:

```text
python3 analyzer.py run-once --reports <reports-directory> --config <private-json>
python3 analyzer.py serve --reports <reports-directory> --config <private-json>
```

Its private configuration uses the exact fields in the runtime contract:
`schema`, `corp_identity_sha256`, `keys_file`, `model_name`, `model_api_url`,
`model_api_key_file`, `wire_api` (`responses` or `chat_completions`) and
`prompt_file`. Put the system instruction from
[the analyzer contract](../../docs/business-diagnostics-analyzer.md#system-instruction)
in the UTF-8 prompt file. All config, prompt, context, registry and key files
must resolve outside the reports directory. The worker refuses overlapping
paths, URL credentials, query strings, fragments, redirects and public HTTP.
`allow_private_http: true` permits HTTP only to literal private or loopback IPs.
HTTPS uses normal certificate validation. There is no alternate-model fallback.
Responses requests set `store: false`; provider tool/function output is rejected.

Optional `company_prompt_file` selects a separate private UTF-8 generic prompt
for completed, enabled company registrations from the upload-key registry.
New companies receive no legacy operator facts. The configured legacy identity
retains its existing prompt, context and revision; without this optional field,
processing remains legacy-only. Enrollment rereads registrations on every call
and rotates a durable cursor after each processed report under a fixed
nonblocking lock, so legacy backlog cannot starve another company.

Optional timeout, lease, attempts, polling and retry defaults are 90, 300, 3, 15
and 30 seconds respectively. Timeout is at most 120 seconds; leases must be
130-3600 seconds so they outlast the request deadline. Attempts are bounded to
1-10. Poll/retry intervals are 1-3600 seconds. A missing or invalid provider is
reported as unavailable; transient network/429/server failures retry with a
bounded attempt counter. Safe stdout contains state and processed count only.
No provider text, request bodies, exceptions, secrets or paths are logged. A single
disposable subprocess bounds the entire provider operation, including DNS, and is
terminated and reaped at the deadline.

The optional operator context JSON contains a `trusted_operator_facts` object.
Only `feed_version`, `feed_source_sha`, `installed_version`,
`installed_source_sha`, `artifact_sha256`, `private_build_version`,
`private_build_source_sha`, `current_attempt_started_utc` and
`current_attempt_ended_utc` are admitted. Versions use bounded identifier
characters, hashes use lowercase hex, and attempt times require explicit UTC
offsets. Uploaded fields never supply operator facts. Without a verified attempt
window, recent log events have unknown time scope. Events older than 24 hours
relative to receive or analysis time are historical. Current runtime or tunnel conclusions
require cited, explicitly timestamped event context inside the verified window,
a recent receive time and a collector clock within an hour of the receiver.
This conservative gate leaves uncertain clock/session cases as insufficient data.

The worker verifies immutable archive SHA/size, reads only allowlisted entries
through an open descriptor and never extracts them. It independently masks
secrets, URLs, IPs, device/user names and private paths/configuration. A shared
per-packet alias map preserves relationships between masked addresses and paths
without storing their original values in the receipt. Short log
files retain their full bounded context. Long files retain startup/tail/error
neighborhoods with original line numbers, omitted ranges and truncation notices.
Unsafe or oversized packets become unavailable. The model sees only this packet
and the private trusted system instruction, with no tools or operational authority.

Validated rich results and provenance live under `diagnosis` in the original
receipt, at most 24 KiB per result and 64 KiB per receipt. Public publication
additionally passes `receiver.public_analysis`. Invalid results preserve any
previous valid publication. Directory-inode locks serialize receipt mutations,
upload finalization and retention sweeps; no mutation lock spans a provider call.
One fixed `.analyzer.lock` file prevents parallel worker runs. Generation/run-ID
checks reject stale completions, leases recover interrupted jobs, and expired or
deleted reports are never recreated. Retry preserves the original retention
mtime and prior result. Receipt replacement sets the original timestamp before
atomic rename; growth and temporary receipt bytes count against the same quota.
Receipt growth additionally reserves headroom for all four maximum-size streaming
uploads, whose in-memory reservations are private to the receiver process.
Upload finalization rechecks complete archive plus new metadata capacity under
the shared lock. This favors intake over optional analysis near the storage cap.
An identical input/prompt/model/context revision is idempotent until explicit
retry. The fixed lock file contains no report data.

Synthetic CI verification:

```text
python3 -m unittest discover -s packaging/business-support -p 'test_*.py' -v
```

Production activation still requires independent review, exact-source green CI,
private service configuration and authenticated upload-to-inbox-to-analysis
verification. Repository changes alone do not configure or activate a provider.
