# Standard desktop assistant and gateway

This document describes the candidate contract. The dated prerelease acceptance records identify the builds and routes verified in production.

The desktop owns the VPN engine, routing rules, settings, application selection and updater. Its assistant asks the gateway at `https://ninitux.com/api/v1/assistant/vpnrouter` for a typed proposal or explanation. The person at the desktop authorizes a local change or the disclosure of diagnostic evidence.

The eligible subscriber types a request directly on Simple Home and selects Send or presses Enter. The same panel previews a proposed change and asks for explicit confirmation. Undo is available when the desktop has a restorable change. The diagnostics disclosure groups on-device repair and online analysis. User-facing labels name the VPNRouter assistant and its actions so a compatible provider change can be made at the gateway.

| Entry point | Data sent | What the desktop does with the result |
| --- | --- | --- |
| Legacy `POST /plan`, schema 1 | Goal, language and whether an application target was resolved | Older clients understand a route-only proposal or a request for clarification. The endpoint remains available for those clients. |
| Games or Russian traffic outside the VPN | Handled on the device. The gateway receives neither the request nor the inventory. | Previews the exact change. Apply saves it for the next connection or reconnects the current VPN and verifies the generated route before saving. |
| Online request, `POST /step`, schema 2 | Goal, language, connection/UI state and an inventory of available control IDs with their names and types | Checks that the proposed action matches one locally recognized command, target and value. The user sees the effect and can decline. After confirmation the client rechecks state before applying. An ambiguous request may produce a suggestion. |
| Analyze last crash, `POST /step`, schema 2 | The button sends the preset goal, language, connection/UI state and four classified values describing severity, fault, component and signal | Displays a short explanation. The control inventory is empty. The separate disclosure preview belongs to the source diagnosis button. |
| Source diagnosis, `POST /source-diagnosis`, schema 1 | Language, installed version and Git commit, OS, architecture, plus one classified crash or manual connection failure | With separate consent, displays a hypothesis, its basis, confidence, a suggested check and source citations. |
| Repair VPN | The user starts a local repair trial | The desktop tests candidate settings and verifies the outcome. The model control inventory does not expose this button. |
| Recovery after repeated manual failures on Windows x64 | The local updater evaluates connection history, build identity and package provenance | Three distinct qualifying manual failures with a comparable configuration trigger local evaluation. The prior working version must be supported by local evidence or a user attestation tied to a previous install. A TUN ownership conflict can be diagnosed but does not count toward recovery. The updater owns target selection, verification, final installation consent and configuration backup. |

## Local routing requests

- “Пустить все игры вне ВПН” and “Хочу, чтобы игры шли мимо VPN” use the installed Steam library inventory on Windows. The preview shows process names, a switch to “All except selected” when needed and any saved exclusions that become active. Other stores require manual selection. An empty inventory leaves the settings untouched.
- “Сделать ру сайты вне ВПН” enables the existing regional bypass. Its domain source is geosite-tld-ru, and its address source is geoip-ru. With the VPN disconnected, the setting can be saved before the lists are downloaded. With the VPN connected, missing lists are fetched after confirmation and the accepted session must contain the direct rule.
- Both operations use the desktop's subscription, session and settings-revision checks. Custom configurations and enabled custom rules get an explanation. An eligible subscriber can type other requests in the same field for the online assistant.

## Identity and transport

The desktop selects the active eligible vpnctl subscription, or the sole eligible one. From its HTTPS address it derives a device or subscription credential for the fixed gateway. It sends `Authorization` and `X-Vpnctl-Identity` on that request. Redirects are disabled. The public proxy sets the source IP and forwards the exact POST route to vpnctld. The daemon resolves the current account and checks that it is enabled on every request.

The daemon calls its configured model provider with a separate server credential. In the deployed plan/step service the provider route used OpenCode Go with DeepSeek V4.1 Flash. Provider identity and configuration must be verified again when deploying a new gateway. Online request text is visible to the provider. Crash and source diagnosis send classified values. Raw crash reports, configuration files, application window text and the subscriber credential stay out of the diagnostic payload. Source diagnosis additionally discloses the selected public code to the provider. `Cache-Control: no-store` applies to the HTTP response. Provider-side retention depends on the provider agreement.

## How a build-specific diagnosis works

1. The desktop identifies the installed assembly revision. For a crash it reads the newest report and checks its recorded build identity. For an unsuccessful manual connection it retains the most recent supported observation and its time within the current process. Starting another manual attempt clears the observation.
2. The panel shows the event and local time. Sending requires explicit consent. The client discards a pending result when the selection or observation has changed. Changing subscription hides the previous observation. Reselecting it can reveal the observation again within the same process. Cancellation after transmission cannot recall data already delivered.
3. The gateway matches the exact version, commit, platform and architecture in its local release catalog. An unmapped build yields `unknown_build`. A mapped build without applicable evidence yields `insufficient_evidence`. Both complete before a provider call.
4. Release operations review the selected ranges for external disclosure, extract them from the verified release commit, and scan their text for secrets. The daemon loads the root-controlled catalog when its router starts. A request cannot choose an arbitrary file or URL. Activating a new catalog requires a restart. Evidence ranking prefers the observed signal, then the component, then the fault class. The provider receives the language, version, OS, process architecture and classified event, with up to 12 excerpts within 24 KiB of source and 32 KiB of serialized user-message data.
5. For a diagnosis the provider returns a single structured reply. The gateway matches each cited path and inclusive line range to an excerpt actually sent, then stamps it with the installed commit. The client validates the response before displaying it. A citation identifies the code considered. The suggested check helps the user test whether the hypothesis explains their incident.

Supported connection evidence is `startup_timeout`, `readiness_timeout`, `unexpected_stop` and `tun_busy`. The serialized request contains either this code or a crash object. A crash can be fatal or recoverable. The source request contains no free-form goal.

## Errors, capacity and operational verification

| Condition | Gateway or desktop behavior |
| --- | --- |
| Missing or invalid credentials, or a disabled account | HTTP 401 or 403. The panel reports an access problem. |
| Invalid schema or body over 2048 bytes | HTTP 400 or 413 before a model call. |
| Old gateway | An absent source endpoint can return HTTP 404. The desktop reports that the feature is unavailable. |
| Rate limit | HTTP 429. The desktop displays a rate-limit message. Plan and step share an account quota. Source diagnosis has a separate pool with six requests per account and 120 per client IP in 60 seconds. Each account can use one of two provider slots per daemon. A busy slot returns `diagnosis_busy` with a one-second retry hint before consuming account quota. The desktop requires a fresh user request. |
| Unreadable configured catalog or unavailable gateway | HTTP 503 when that resource is required. An unset catalog treats builds as unmapped. |
| Provider timeout or malformed reply | HTTP 502. A provider rate limit returns 429. The desktop reports the failure without changing the tunnel or settings. |

## Previous-version suggestion on Windows

On Standard Windows x64 the desktop records completed manual connection attempts against the running build and a local fingerprint of its connection settings. Three comparable technical failures can show a recovery card alongside the assistant on the Simple page. A successful connection resets the count. Provider rejection, lack of connectivity, user cancellation and identified local conflicts do not count.

The card uses local history to identify a verified working version for the same configuration. When the only evidence is a previous installation recorded by the updater, the user first confirms whether that version worked with the same settings. Declining or lacking compatible evidence leads to local diagnostics. Installation requires separate confirmation. The client checks the target against the signed recovery manifest, active configuration, release identity, archive size and digest, then backs up settings and hands the verified package to the Windows updater. Changing the selected configuration or recovering the connection during the download invalidates the approval.

The server receives neither the history nor the rollback choice. The model can explain a classified failure under the diagnostic contract. It has no install operation, target-version field or download URL. Version 2.50.5 can show its ordinary update banner after a downgrade. Installing an update from that banner requires a user action.

The wire format supports Windows, macOS and Linux on x64 or arm64. Each published platform and architecture needs an explicit catalog mapping. Android is outside this desktop contract.

At release, compare the catalog revision and platform pairs with packaged binaries. Through the public proxy, use an authorized device to exercise a known build, an unknown build and insufficient evidence. Check cited lines in Git. On the installed desktop exercise consent, refusal and stale evidence, and verify settings and tunnel state. Windows update and rollback require a separate packaged installation and relaunch test.

See the [command target-resolution contract](assistant-app-target-resolution.md) and the gateway's [wire specification](https://github.com/PavelLizunov/vpnctl/blob/main/docs/specs/vpnrouter-source-diagnosis.md).
