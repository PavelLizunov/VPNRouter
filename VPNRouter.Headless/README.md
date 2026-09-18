# VPNRouter.Headless

`VPNRouter.Headless` is the CLI daemon and standard I/O NDJSON protocol adapter for the Omarchy Quattro shell plugin (`io.github.pavellizunov.vpnrouter`). It connects native QML widgets and services to `VPNRouter.Core` and the underlying sing-box engine without requiring Avalonia desktop dependencies.

## Development status

This is a draft integration, not an installable or accepted Linux VPN release.
Linux firewall kill-switch and DNS-lockdown capabilities remain unavailable.
Connection eligibility currently checks the data-directory sing-box path and
cooperative ownership; it does not establish binary integrity or TUN privileges.
The existing Core privilege-launch behavior is unchanged. Runtime binary
selection/distribution and privileged protection integration remain unresolved.
The Linux lock coordinates cooperating processes of the same UID, not all users.

Verification covers isolated protocol/lifecycle/storage tests and selected Core
regressions, not live shell interaction or VPN dataplane acceptance. See the
[verification report](../plans/omarchy-integrated-candidate-2026-09-18.md).
Do not interpret a passing build or CI run as permission to install or connect.

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
   - Reception of EOF on standard input, SIGINT, or SIGTERM terminates active operations, stops the engine, and disposes resources cleanly.
