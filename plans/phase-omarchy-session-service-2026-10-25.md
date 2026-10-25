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
inert server and unchanged service PID across shell restart, inspect reattached
snapshot. Check shared Suflyor work before restarting. A connected session,
egress and kernel cleanup across restart remain explicitly unverified.

## Outcome

Pending implementation and CI/live evidence; no release or merge authorized.
