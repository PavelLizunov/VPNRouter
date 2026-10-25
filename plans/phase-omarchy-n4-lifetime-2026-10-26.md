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

## Live root cause and dependency integration

The route cleanup failure on r40 was not a demonstrated gVisor close-order bug.
Round N independently identified the pinned runtime's interactive native resolved
helpers inheriting its TUN fd. NativeTun also waits for `resolvectl revert` before
route cleanup. The generator did not disable this second DNS owner. Round N's
7fadf156 and eb31d7c2 supply managed DNS steering/authorization and explicit
`dns_mode: disabled`, preserving the explicit DNS hijack route. They are included
as required dependencies of the lifetime acceptance, with their original tests.
The speculative source-build change 1d258e61 was reverted by 45c50fb; the trusted
runtime archive, binary hash and license checks remain unchanged. Its failed Arch
CI was a binary-hash catalog mismatch, not an accepted cleanup fix. No production
runtime fork, release or tag was created. Repeat all live lifecycle scenarios on
the combined exact-SHA artifact before accepting cleanup.
