# Omarchy independent session service candidate

## Intent and invariants

Implement option A of omarchy-session-lifetime.md on backend 5470a8bc and a
separate coordinated plugin branch. The user service owns one backend/session;
a client detach must not stop the engine or cancel an admitted operation.
Explicit disconnect stops the session; quit and service stop dispose it.
Stdio retains its existing EOF teardown behavior. No VPN connection is authorized.

## Interface

Linux --service listens at $XDG_RUNTIME_DIR/vpnrouter-headless/control.sock,
0600 inside a 0700 directory, checking same-UID peers. One controller is admitted;
others receive controller_busy. Locks protect the socket and data directory.
--attach relays stdin/stdout to that socket without owning the session. The
plugin wrapper starts the packaged unit and attaches when present, otherwise
executes the old stdio helper. Startup and reattach return a fresh snapshot;
shared admission prevents an old client's operation racing a new mutation.
No new dependency: .NET UnixDomainSocketEndPoint and Linux credentials/flock.
Verdict: Adopt - .NET sockets and systemd user services - platform APIs implement
transport/ownership without a new third-party relay or daemon framework.

## Verification

CI: existing Core/Headless suites plus socket mode, exclusivity, reattach,
pending operation EOF, explicit disconnect/quit, stale socket and lock checks;
Arch package build/install and installed offline smoke. Plugin: thirteen Node
suites, packaging in bwrap, qmllint, full theme matrix, plugin validation.
VM only: install exact CI package, deploy exact plugin, preserve a selected
server without connecting and unchanged service PID across shell restart, inspect reattached
snapshot. Check shared Suflyor work before restarting. A connected session,
egress and kernel cleanup across restart remain explicitly unverified.

## Outcome

Backend implementation: 2b1eb884; coordinated plugin: 3e455f9. CI test run
40181629309: Linux 3217 passed/77 skipped, Windows 3385/20, Headless 42/0;
all six jobs green. Arch run 40181561680 green; artifact 12594237946,
0.1.0.r34.g2b1eb88-1. Local Arch packaging: 126 checks passed.

On omarchy-test only, the exact package was installed and the plugin deployed.
The service PID 143400 and start timestamp survived migration and a second
shell restart (shell 149118 -> 163258, relay 149383 -> 163516). The plugin showed
the same selected server and disconnected state. A second controller received
controller_busy. Terminating only the relay preserved the same service PID and
fresh snapshot/revision; the plugin recovered after the temporary controller
released the socket. No connect/disconnect or VPN traffic checks ran.

Plugin checks: 13 Node suites, 40 packaging cases in bwrap, 97 native offscreen
cases, qmllint exit 0 (1107 warnings, no errors), plugin validate exit 0; plugin
CI 40181810915 green. Detailed evidence is in the lead's external D3 report.
No files deleted. Direct self-review only; independent Claude lead review and
owner acceptance remain pending. No release, tag or merge performed.
