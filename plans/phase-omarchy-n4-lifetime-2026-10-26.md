# Round L: session lifetime

## Intent and invariants

Integrate the private user-service candidate onto 2cc9e7e7. Plugin reload and shell
restart must detach only the controller, including during connect. Keep Linux
routing isolation, runtime authorization and truthful crash/readiness behavior.
Logout stops the service; package replacement first stops its active sessions.
No changes to the owner's workstation or other projects on the shared VM.

## Contract

One service owns RouterSession and a private same-UID Unix socket; one controller
attaches over unchanged NDJSON v1. Fresh snapshots include live connection time
and TUN byte counters, nullable when unavailable. No automatic VPN reconnect after
a service crash or package upgrade. A bounded systemd restart restores the control
service, and the UI reads its actual disconnected state.

## Verification

Run the existing plugin Node/packaging/QML/lint/validation gates. Backend builds,
contracts and package install checks run in exact-SHA CI. Under the shared VM lock,
install the exact artifact, deploy this plugin, connect, rescan and restart shell;
compare engine PID, TUN, exit IP, server and elapsed time. Explicit disconnect must
remove TUN and VPNRouter rules. Record screenshots and limits in Round L evidence.

## Live discovery: native gVisor shutdown blocks TUN cleanup

On r40, service and core PID, exit IP, selected HY2 server and the live clock
survived both rescan and full shell restart. Panel Disconnect removed the core
and eventually the TUN but retained rules 8900-8910. Reproduced from a clean rule
baseline. The same packaged core run directly in a separate managed unit exits
after ten seconds with "sing-box did not close!"; the log identifies close of the
TUN inbound as blocked. Thus increasing Headless's five-second grace is not a
cause fix. Native Inbound.Close closes the gVisor stack before NativeTun.Close,
which removes routes/rules and closes the descriptor needed by its read loop.

Backport the Linux gVisor-only close order onto pinned f62085aa, preserving all
production protocol tags, using Go 1.26.8 in CI. The existing Python packager
builds this source and records provenance. Run the actual Go regression against
unpatched and patched source, then repeat the VM disconnect/crash/upgrade cases.
VM lock 1 was released at 02:28:18 after restoring selection/routing and removing
only the dedicated orphan rule block; no VPNRouter TUN or rules remained.
