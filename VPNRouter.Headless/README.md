# VPNRouter.Headless

`VPNRouter.Headless` is the CLI daemon and standard I/O NDJSON protocol adapter for the Omarchy Quattro shell plugin (`io.github.pavellizunov.vpnrouter`). It connects native QML widgets and services to `VPNRouter.Core` and the underlying sing-box engine without requiring Avalonia desktop dependencies.

## Development status

The Arch package is built and installed in CI; see [packaging](../packaging/arch/README.md).
Linux firewall kill-switch and DNS-lockdown capabilities remain unavailable.
On Linux with systemd-resolved, generated full-tunnel, exclude-app and strict-DNS
sessions steer the system resolver through the TUN. The Arch package grants the
active local session only DNS/domain/revert operations on `VPNRouter-TUN`, without
password prompts. This is resolver routing, not firewall DNS lockdown; custom
configs, manually named TUN links and hosts without resolved need their own DNS
integration. Failed steering is logged and does not stop the VPN.
Production uses only `/usr/lib/vpnrouter-headless/runtime/sing-box`, trusting package
root ownership, non-writable file/ancestor permissions and effective/permitted
CAP_NET_ADMIN. It does not select a user-data binary or pin its SHA.
Snapshot includes `unavailableReason` when runtime validation fails; HealthCheck
reports the same reason. A trusted installation permits normal-user TUN startup.
The Linux lock coordinates cooperating processes of the same UID, not all users.

Verification covers isolated protocol/lifecycle/storage tests and selected Core
regressions. Exact-snapshot outcomes and remaining live acceptance gaps are in
[the integration record](../plans/phase-omarchy-integration-2026-10-22.md) and
[the defect ledger](../plans/OPEN-DEFECTS.md).

The plugin starts Headless as a child over stdio. Closing or restarting the shell
stops the active VPN; traffic can then go directly because Linux has no kill-switch.
Headless does not restore the previous connection automatically. See
[session lifetime options](../plans/omarchy-session-lifetime.md).

## Usage

```bash
# Run over standard input and output
VPNRouter.Headless --stdio

# Run with an isolated test configuration data directory
VPNRouter.Headless --stdio --data-dir /path/to/isolated/datadir
```

### CLI Arguments

- `--stdio`: Launches the protocol loop over `stdin` and `stdout`. Required.
- `--data-dir <ABSOLUTE_PATH>`: Optional directory path for isolated configurations and tests. Must be an absolute path. The CLI sets Core AppPaths before creating the backend. Offline profile reads use only this directory's cache, without falling back to the process-global data tree; bundled profiles remain available. Explicit local profile sources still refer to their configured paths. This option does not isolate TUN/network resources or grant permission to connect.

## Protocol Specification (v1)

Communication is conducted via newline-delimited UTF-8 JSON frames over standard input and output. Standard output is reserved strictly for protocol frames; diagnostic logs or arbitrary text are never emitted to stdout.

### Request Format

```json
{"v":1,"id":"req-1","method":"snapshot","params":{}}
```

- `v`: Protocol version. Must be integer `1`.
- `id`: Unique request identifier. Nonempty ASCII alphanumeric or hyphen characters (`[a-zA-Z0-9-]`), maximum 64 characters.
- `method`: Name of the method to execute.
- `params`: JSON object holding method arguments.

### Response Format

Success:
```json
{"v":1,"id":"req-1","result":{"state":"disconnected","revision":"r1",...}}
```

Error:
```json
{"v":1,"id":"req-1","error":{"code":"invalid_request","message":"Safe description"}}
```

### Asynchronous Events

State Event:
```json
{"v":1,"event":"state","data":{"state":"connected","revision":"r2",...}}
```

Progress Event:
```json
{"v":1,"event":"progress","data":{"id":"req-1","stage":"testing","completed":3,"total":10}}
```

## Subscription refresh failures

`subscriptions.refresh` returns an error with code `subscription_refresh_failed`
and a fixed safe message if a fetch fails or yields no usable servers. Cached
servers, configuration revision and the active connection remain unchanged.
Refreshing multiple subscriptions commits only when all requested refreshes
succeed. Cancellation returns `cancelled` instead. URLs, HTTP bodies and raw
exception text are not returned to the client.

## Offline application and server metadata

Snapshot and state events include `unavailableReasonCode` (`runtime-missing`,
`runtime-capabilities`, `runtime-untrusted`, `sandbox`, `ownership`, `other`, or
null). `unavailableReason` retains the English diagnostic text. A connected or
busy session alone does not imply an availability failure.

`apps.list` retains `include`, `exclude` and `running`, and adds `runningApps`:

```json
{"name":"firefox","displayName":"Firefox","icon":"firefox","desktopId":"firefox.desktop","hasWindow":false}
```

On Linux, discovery reads current-UID `/proc` entries, skips empty command lines,
and uses the executable symlink basename (falling back to `comm` when unreadable).
Desktop Name/Icon/Exec/StartupWMClass metadata comes from
`~/.local/share/applications` and `XDG_DATA_DIRS` (default
`/usr/local/share:/usr/share`). Hidden/NoDisplay entries suppress metadata;
user entries override system entries with the same desktop file ID. Matching
prefers a case-insensitive desktop ID, reverse-DNS ID last segment, or
StartupWMClass equal to the executable basename. Otherwise only a unique Exec
basename supplies metadata; ambiguous matches return the process name and null
icon/desktopId. Simple env prefixes are unwrapped; shell, flatpak, gtk-launch,
uwsm and omarchy-launch-* wrappers are ignored for Exec matching. Icon theme
names are returned without reading image files; icon paths are omitted. Desktop matches sort first, followed
by process-only entries. Results are deduplicated and limited to 200 entries.
The desktop index has a 30-second TTL; discovery has bounded file counts, reads
and a 150 ms work budget (best-effort under filesystem/scheduler delays).
`hasWindow` is false because window ownership is unknown. Pass each exact `name`
to `apps.set`; case and Linux executable spaces are preserved. Windows/macOS
retain their existing `running` discovery and return an empty `runningApps` list.

`servers.list.items` adds `source` (`manual` or `subscription`), `subscriptionId`
(string or null), and `removable` (boolean). Subscription entries and synthesized
legacy entries are not individually removable. `servers.remove` returns
`not_removable` for these IDs without changing settings or revision; manage the
subscription instead. Unknown IDs still return `not_found`.

On first launch, `diagnostics.check` reports absent configuration/directories as
`info` with "Not configured yet: add a subscription or a server". Existing
invalid configurations and runtime/ownership failures retain their error/warning
severity. These metadata endpoints require no VPN connection or network request.

## Security and Framing Invariants

1. **Size and Depth Bounds**:
   - Input frames are bounded to 256 KiB before accumulation. Excess bytes are discarded without buffering.
   - Output frames are capped at 256 KiB.
   - Maximum JSON nesting depth is 32.
2. **Duplicate Key Detection**:
   - Duplicate keys in the root frame or nested parameters are rejected immediately.
3. **Safe Error Sanitization**:
   - Error messages are fixed, sanitized descriptions. They never echo input payloads, inner exception text, stack traces, subscription URLs, or credentials.
4. **Concurrency and Urgent Control**:
   - At most one ordinary operation may run concurrently. Additional ordinary requests receive an immediate `busy` error response.
   - `cancel` and `disconnect` bypass the ordinary execution slot once admitted; invalid control parameters are rejected before cancellation effects. They cannot bypass blocked transport input.
   - Handler calls run on workers after synchronous slot reservation. A blocking handler does not occupy the reader, but retains its slot until it settles; this is not forced interruption of synchronous work.
   - Mutations reject cancellation observed before invoking their mutation action. Later cancellation does not interrupt synchronous persistence; a failed connected-session apply uses guarded compensation.
5. **Output Queue and Backpressure**:
   - Output queue is capped at 8 frames; asynchronous dispatch/response-waiter tasks are capped at 5.
   - State events coalesce so rapid status changes do not overflow the queue.
   - Responses drain while the transport remains usable. Temporary backpressure pauses input admission. EOF cancels pending response enqueues; keep stdin open until receiving required responses.
   - A write-plus-flush stalled for three seconds, or a broken output pipe, triggers bounded teardown. Delivery to a dead peer is not guaranteed; clients must not replay mutations automatically.
6. **Graceful Shutdown**:
   - EOF on standard input, SIGINT, or SIGTERM cancels active operations and starts engine teardown. Handler disposal has a 20-second deadline, allowing the session's three-second gate wait and 15-second bounded stop to finish. Output drain runs concurrently. An unresponsive engine can outlive these deadlines.
