# Omarchy headless protocol v1 - implementation contract

Status: approved-scope implementation contract, not a shipped API.
Owner: coordinator. Workers must not silently change this document.

## Distribution and ownership

- Backend project: `VPNRouter.Headless`, net10.0 executable named
  `VPNRouter.Headless`, referencing `VPNRouter.Core` without Avalonia.
- Plugin repository: `PavelLizunov/omarchy-vpnrouter`; entry files at root;
  manifest ID `io.github.pavellizunov.vpnrouter`, version `0.1.0` (development),
  license GPL-3.0 to match VPNRouter, kinds bar-widget and service.
- Existing Linux settings remain at AppPaths.DataDir. Backend must not modify
  them at startup. Explicit user operations are the only persistence trigger.
- One service-owned foreground helper; panel closure preserves it. EOF/SIGTERM
  cancels work and stops owned engine. No detached daemon or second shell.
- Invocation: `VPNRouter.Headless --stdio`; optional `--data-dir ABSOLUTE`
  for isolated test configuration. No fixture mode may initiate networking.
- QML launcher path: plugin-relative `bin/vpnrouter-headless` wrapper, which
  executes installed `/usr/lib/vpnrouter-headless/VPNRouter.Headless` or a
  task-packaged plugin-relative backend (setup documents exactly which).
  Missing backend exits nonzero; never downloads or elevates at runtime.

## Framing

UTF-8 newline-delimited JSON, no stdout logs or terminal sequences. Every frame
has `v:1`; requests: `{v:1,id:"1",method:"snapshot",params:{}}`.
Response: `{v:1,id:"1",result:{...}}` OR
`{v:1,id:"1",error:{code:"invalid_request",message:"Safe description"}}`.
Event: `{v:1,event:"state",data:{...snapshot}}` or
`{v:1,event:"progress",data:{id:"1",stage:"test",completed:2,total:10}}`.
Request IDs: nonempty ASCII letters/digits/hyphen, maximum 64 characters.
Unknown fields/methods, duplicate keys, wrong types, non-v1 frames are errors.
Input maximum 256 KiB BEFORE accumulation; max JSON depth 32. Output maximum
256 KiB, collections paginated at 100 rows, no unchecked serialization of
Core settings. At most one ordinary operation, plus cancel/disconnect control;
busy is an error, not an unbounded queue. The transport additionally caps
asynchronous dispatch/response-waiter tasks at 5; a completed handler awaiting
response enqueue still counts against that cap. Full output pauses input
admission, including unread controls, until drain or the stall deadline.
Output queue max 8 frames; status
may coalesce; accepted responses drain while the transport remains usable.
Temporary stdout backpressure pauses admission; a write-plus-flush stalled for
3 seconds shuts down the session. Broken stdout also shuts down. A dead peer
cannot be guaranteed delivery; never replay mutations after transport failure.
Admission reserves dispatcher slots synchronously; admitted handler calls run on
workers so synchronous handler work does not occupy the input reader. Slots and
operation tokens remain owned until those calls settle. This does not guarantee
thread-pool availability or interrupt uncooperative synchronous cancellation callbacks.
EOF cancels pending response enqueues as well as operations: clients requiring a
response must keep the input pipe open until receiving it.
Errors never echo input, exception messages, subscription addresses or secrets.

## Snapshot

Result of snapshot and state event data:
```
{
  "state": "disconnected|connecting|connected|disconnecting|error|unavailable",
  "revision": "opaque-config-content-revision",
  "backendVersion": "Core AppVersion",
  "activeServer": "display label or empty",
  "routingMode": "split|full",
  "routingAppsMode": "include|exclude",
  "configMode": "generated|subscribe|custom",
  "busy": false,
  "errorCode": null,
  "capabilities": {"connect": true, "killSwitch": false, "dnsLockdown": false}
}
```
Capabilities reflect verified availability, not optimistic platform detection.
Connected only follows typed Core Connected with a positive guard captured at
that event and retained for subsequent state reads. Process start/task return
is not readiness; a replacement process cannot reuse a previous generation's
guard. Retry from error must pass the same environment capability checks.
Unknown state disables destructive controls. No secrets in snapshots.

## Methods and result shapes

All mutations include `revision` matching last snapshot; mismatch => conflict.
Return a fresh snapshot after a successful mutation unless otherwise specified.
Long methods emit progress; `cancel {id}` cancels only that request; disconnect
is an urgent cancellation/control operation. Requests are not shell commands.

- `snapshot {}`; `connect {revision}`; `disconnect {}`; `cancel {id}`.
- `servers.list {offset:0,limit:100}` -> `{items:[{id,name,protocol,selected,latencyMs:null}],total}`.
- `servers.import {revision,text}` (URI lines or WireGuard configuration).
- `servers.select {revision,id}`; `servers.remove {revision,id}`.
- `servers.test {id}` -> `{id,reachable,latencyMs:null}`; `servers.verify {id}`
  -> verified outcome with bounded progress, no credentials.
- `subscriptions.list {offset,limit}` -> `{items:[{id,name,enabled,serverCount}],total}`;
  URLs are write-only in UI to avoid status/event disclosure.
- `subscriptions.add {revision,name,url}`; `subscriptions.remove {revision,id}`;
  `subscriptions.refresh {revision,id}` (id omitted means all);
  `subscriptions.enable {revision,id,enabled}`.
- `free.list {offset,limit}` -> `{items:[{id,name,protocol,status,latencyMs:null}],total}`;
  `free.refresh {}`; `free.test {id}`; `free.verify {id}`;
  `free.apply {revision,id}` selects a verified entry without auto-connect.
- `apps.list {}` -> `{include:[names],exclude:[names],running:[names]}`;
  `apps.set {revision,mode:"include|exclude",names:[names]}`.
- `routing.set {revision,routingMode:"split|full",routingAppsMode:"include|exclude"}`.
- `profiles.list {}` -> `{items:[{id,name,selected}]}`;
  `profiles.select {revision,ids:[profileNames]}`; `profiles.refresh {}`.
- `rules.get {}` -> `{text,priority:"toggles_first|custom_first"}`;
  `rules.set {revision,text,priority}`; Core DSL/parser validates rules.
  `rules.import {revision,text,format:"json|csv|singbox"}`;
  `rules.export {format}` -> `{text}` (non-secret rule data).
- `custom.list {}` -> `{items:[{id,name,selected}]}`;
  `custom.import {revision,name,text}` (bounded JSON, private managed storage);
  `custom.select {revision,id}`; `custom.remove {revision,id}`.
- `settings.get {}` -> `{mtu,ipv6Enabled,strictRoute,strictDns,dnsMode,
  dnsModeOverride:null|vpn_only|smart|direct,dnsModeSemantics,bypassRussianTraffic,blockAds,dnsLeakLockdown,routeExcludeAddress:[cidrs]}`;
  `settings.set {revision,values:{subset of above}}` rejects unknown settings,
  invalid types/ranges and unsupported protection flags; preserves other fields.
- `diagnostics.check {}` -> `{items:[{name,status,message}]}` redacted/bounded;
  `diagnostics.export {}` -> `{path}` to a private managed diagnostics folder.

### DNS settings semantics

`settings.set.values.dnsMode` accepts `vpn_only`, `smart`, `direct`, or null.
Null clears the nullable YAML `app.dns_mode_override`; omission preserves it.
All values are validated before applying a detached settings candidate. There
is no DNS-specific profile sidecar write. Custom-config mode rejects this field;
QML omits it when saving other custom-mode settings.

`settings.get.dnsModeOverride` returns the nullable persisted override. QML uses
it to preserve profile-default selection across refresh and unrelated saves.
Older responses without the field fall back to `dnsMode` for compatibility.
`settings.get.dnsMode` returns the configured override or resolved
profile mode for generated configs, not a nullable override marker. StrictDns
and full-tunnel protections still take precedence in generated Core output.
Custom configs return `custom` unless strict/full forces `vpn_only`.
`dnsModeSemantics` is currently a nullable explanatory English string consumed
by explicit RU/EN mappings, not a stable machine-code enum or full effective-DNS
DTO. Do not infer that it is a selectable value. A typed effective-DNS field
remains a consumer-contract follow-up; do not label the inherited/configured
mode as complete runtime policy.

Sending `dnsLeakLockdown:true` is refused when unavailable. The QML save payload
omits unavailable protection fields so unrelated changes can succeed without
silently clearing an existing protection preference. Such a preference still
prevents connecting if its requested protection cannot be provided.

Opaque row IDs must be stable across list pagination and reject stale selection
rather than selecting another row after reorder. Never use credentials as IDs.
Tests need deterministic isolated data; no live URLs or real user config.

## Backend implementation seams

Namespace `VPNRouter.Headless`. Transport entry point calls
`RouterBackend.ExecuteAsync(string method, JsonElement parameters, CancellationToken)`
returning `Task<object>`; error type `RouterException(string code)` (fixed safe
messages may be mapped centrally). RouterBackend implements IAsyncDisposable.
Backend receives `IRouterSession` and optional dataDir in its constructor.
Backend event `Action<object>? StateChanged`, `Action<object>? ProgressChanged`.
Transport supplies request ID context to progress where needed, never raw params.

Session interface owned by lifecycle worker:
```
public interface IRouterSession : IAsyncDisposable {
  string State { get; }
  string? ErrorCode { get; }
  bool CanConnect { get; }
  bool SupportsKillSwitch { get; }
  bool SupportsDnsLockdown { get; }
  event Action? Changed;
  Task ConnectAsync(AppSettings settings, CancellationToken ct);
  Task ApplyAsync(AppSettings settings, CancellationToken ct);
  Task DisconnectAsync(CancellationToken ct);
}
```
`RouterSession` is the production implementation; default constructible.
`RouterBackend` constructor `(IRouterSession session, string? dataDir = null)`.
Backend must not run engine/network in constructor or snapshot/list calls.
Factory seam changes to Core require coordinator integration and source review.
Testing may use an explicit fake IRouterSession, never a production fallback.

## Verification and non-goals

No feature stub is accepted as parity. If a safe Core seam is missing, report
it explicitly and leave overall completion pending; do not silently disable
required functions and call the port complete. Runtime installation/connection
still requires separate authority. User allowed an isolated per-user SDK on
omarchy-test and isolated builds/tests/QML checks, but not live plugin activation,
shell restart or VPN routing. Exact-SHA test inputs only.
