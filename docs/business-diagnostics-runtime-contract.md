# Corp diagnostics runtime v1

Companion implementation contract for [the analyzer instruction](business-diagnostics-analyzer.md).
Use synthetic fixtures in tests. This contract grants no model tools or runtime
activation by itself. Preserve existing support POST/GET compatibility.

## Receiver and worker

Receiver keeps existing CLI flags and adds optional `--inbox-config PATH`.
Without it, internal endpoints are unavailable. The private JSON configuration
has `schema:1`, `corp_identity_sha256` (64 lowercase hex) and
`service_token_file` (32-512 ASCII bearer characters, private file outside reports).
The configured identity must remain enabled in the existing key registry.

Private endpoints, never forwarded by the public support ingress:

- GET `/internal/corp-diagnostics/reports?limit=50&cursor=ID`.
- GET `/internal/corp-diagnostics/reports/ID`.
- POST `/internal/corp-diagnostics/reports/ID/retry`, empty JSON body.

All require `Authorization: Bearer SERVICE_TOKEN`. No upload identity or browser
cookie grants this access. Force configured Corp identity on every lookup; an
unknown/foreign/missing-archive ID gives the same404. IDs are64 lowercase hex;
limit is1..100. Sort received time descending, then ID; cursor denotes the last
own report in that order. Invalid/foreign cursors are uniformly400.

List shape: `{"schema":1,"reports":[SUMMARY],"next_cursor":null}`.
Detail: `{"schema":1,"report":SUMMARY,"analysis":RESULT_OR_NULL,"provenance":OBJECT_OR_NULL}`.
Retry:202 `{"schema":1,"state":"pending"}`; active nonexpired job:409.
Retries preserve prior valid result, original metadata, archive/hash and retention
mtime; increment generation so an old completion cannot overwrite a newer run.

SUMMARY has report_id, received_utc, bytes, device_label (client-reported,
bounded plain text or null), analysis_state, verdict, summary_ru, model and
updated_utc. analysis_state is received/pending/running/ready/failed/unavailable;
verdict is nullable problem_found/no_problem_evidenced/insufficient_data.
No raw logs, local paths, identity hashes, credentials or archive download URLs.
Unknown/extra receipt fields must not accidentally appear in responses.

Implement `analyzer.py` as stdlib-only CLI:
`analyzer.py run-once --reports DIR --config FILE` and
`analyzer.py serve --reports DIR --config FILE`.
One run processes at most one eligible receipt; stdout is a small safe JSON
summary with state and counts, no model text/secrets/paths. Config fields:

- schema1, corp_identity_sha256, keys_file (existing upload registry).
- model_name, model_api_url, model_api_key_file, wire_api (`responses` or
  `chat_completions`), prompt_file, optional operator_context_file.
- timeout_seconds (default90, max120), lease_seconds (default300),
  max_attempts (default3), poll_seconds (default15), retry_seconds (default30).
- thinking_mode (optional, chat_completions only): enabled or disabled. Omission
  preserves provider defaults. It is part of job identity. For the owner-selected
  deepseek-v4.1-flash diagnostic profile use disabled to reserve bounded output
  for the structured result; evidence/result guards remain unchanged.
- allow_private_http (defaultfalse): only literal private/loopback addresses may
  use HTTP; otherwise HTTPS. No userinfo, query, fragment or redirects.

All secret files are outside the reports directory and never become model input.
No SDK, toolchain or additional Python dependencies. Provider response adapters
accept bounded plain text JSON only; never execute tool/function calls. Responses
requests use store=false. Bound input/output, connect/read time and retries.
Record requested/returned model separately when available; never silently fall
back to another model. A missing provider remains visible as unavailable.

Use durable per-receipt `diagnosis` metadata (job state, generation, attempts,
lease, next attempt, input/prompt/model digest key, validated result, provenance).
Keep rich result at most24KiB and complete receipt at most64KiB. Existing receiver
quota includes receipt growth. Do not add unbounded files that escape quota.
Serialize receipt mutations under a cooperative lock shared by receiver retry
and worker. Never hold that mutation lock across a model call. One worker-process
lock prevents duplicate parallel runs; leases recover interrupted jobs. Duplicate
input/prompt/model revisions are idempotent. Transient failures retry boundedly;
invalid archives/output do not become ready. Do not recreate deleted or expired
reports. Preserve original mtime and all original receipt fields, including an
existing public analysis when no new publication is authorized/valid.

Validate immutable ZIP SHA/size, allowlisted bounded entries, no symlinks or path
extraction. Prepare sanitized packet with exact file/line evidence and provenance.
Preserve complete short app/crash headers/stack context; bound long excerpts.
Keep only verified operator context; uploaded text cannot add trusted fields.
Never send keys, URLs with credentials, device/user names, raw private config or
raw IPs. Historical evidence alone cannot establish a current runtime failure.
Validate the result contract, evidence references, output size and current-scope
semantics before publication. Public short output must additionally pass the
existing receiver.public_analysis oracle, or become unavailable. Model input,
output and exceptions never trigger actions or enter operational logs.

## Corp portal bridge

Backend uses private deployment configuration:
`VPNCTLD_CORP_DIAGNOSTICS_BASE_URL` and
`VPNCTLD_CORP_DIAGNOSTICS_TOKEN_FILE`. Missing/invalid configuration exposes a
clear unavailable state; other Corp functions keep working. No credentials or
private upstream URL are accepted from browser input. Redirects disabled; bound
connection/request time and response to256KiB. Forward only service Bearer, never
browser cookies, forwarding headers or arbitrary browser Authorization.

Browser pages/assets:

- GET `/diagnostics`: normal Corp IP plus password access; login when needed.
- GET `/assets/corp-diagnostics.js`: static secret-free asset.
- GET `/api/corp/reports` and `/api/corp/reports/ID`: authenticated scoped data.
- POST `/api/corp/reports/ID/retry`: existing same-origin and session CSRF checks.

Reuse host isolation, trusted real-IP resolution, enabled binding, current exit
fingerprints, session generations and security headers. Reject a missing/stale
session or unauthorized IP before any upstream request. Preserve existing reader,
rule editor/history/restore and operator isolation. Upstream failures give safe
messages, never exception details or source credentials. Unknown/foreign report
IDs are indistinguishable. Response bodies expose only the defined DTO fields.

Public Rust bridge construction for independent tests:
`corp_portal::diagnostics::Client::new(base_url: &str, bearer: &str) -> Result<Client, ConfigError>`;
`AppState` has optional `corp_diagnostics: Option<Arc<Client>>` initialized None
by existing test-state constructors. Production loads the private environment
configuration once at state construction. Client Debug must redact the bearer.
HTTP is allowed only for literal private/loopback deployment/test addresses;
public HTTPS is allowed. Never put the token in URLs or logs.

UI shows report receive time, clearly labeled client-reported device, processing
state, short conclusion, confirmed/probable/unknown findings and evidence, model
provenance and a retry button when appropriate. Use Russian plain language,
textContent/escaped markup, narrow320/420px layout and readable failure/loading/
empty/insufficient-data states. No raw archive download or AI-generated commands.
Rules and Diagnostics navigation must remain usable with keyboard and narrow view.

## Requested Windows delivery

The owner explicitly requests a new version number and mkvt24.ru direct routing.
Prepare private Corp2.50.9 plus the credential-free shared artifact from the same
accepted SHA; do not overwrite2.50.8 assets. Update feed only after source/CI,
exact hashes and actual upgrade validation. Initial trust migration still uses
the complete private installer; do not silently treat an upload-only support key
as a policy trust/bootstrap authority. Preserve every provisioning extension,
subscription/support identity and signed policy journal/floor.

Add mkvt24.ru direct suffix to the embedded preset and authoritative signed Corp
policy, keeping all existing rules/comments. Both route and DNS must be direct,
including subdomains, in split/full. A remote accepted policy remains a complete
replacement; changing only the embedded preset is insufficient. No node/grant,
reader credential, signer or administrator-password rotation is required.
