# Headless session lifetime across shell restarts

## Observed boundary

At c1bafab1 the plugin Service.qml starts the helper with --stdio; its wrapper
execs Headless. Program owns RouterSession. ProtocolServer treats stdin EOF as
shutdown, disposes RouterBackendHandler -> RouterBackend -> RouterSession, and
stops the owned engine. SIGTERM uses the same cancellation path; SIGKILL cannot.
The owner/lead journal report at 09:59:58 MSK on 2026-10-25 records an active TUN
and 56 routes disappearing after shell exit. This note does not reproduce it.
Arch installs no service. Linux reports kill-switch/DNS lockdown unavailable.

## Options (implementation estimates, including tests)

| Option | Estimated files / changed lines | Behavior and risks |
| --- | --- | --- |
| A. systemd user service + Unix socket | 10-16 / 500-900 across backend, packaging and plugin | Service owns one RouterSession; UI attaches after shell restart. Detaching a socket must not dispose the session. Separate protocol-client lifetime from session ownership; serialize mutations globally, reject a second controller initially, and return a fresh snapshot on reconnect. |
| B. persist connection intent + reconnect | 6-10 / 180-300 across backend and tests | New Headless reconnects after a gap. Requires private atomic persistence, stable server/config identity, explicit disconnect clearing intent before stop, cancellation/failure semantics, stale-config handling and ownership refusal. Two clients must not replay the same intent or race a manual disconnect. |
| C. warning in UI/docs | 2-3 / 15-30 | Explain that shell exit stops VPN and traffic can go direct. Cheap and useful immediately; does not preserve connection or cover crashes. |

A uses a socket in the private XDG_RUNTIME_DIR, mode 0600, with same-UID peer
validation. Socket cleanup must not unlink another daemon's listener; enforce
one daemon per data directory in addition to the existing same-UID TUN lease.
Configure service stop deadlines above backend stop budgets and test systemd
cgroup termination. Shell independence does not imply survival of logout,
service crash or explicit service stop; lingering is a separate policy choice.
Do not bind service lifetime to the shell's graphical-session stop target.

Neither A nor B adds a kill-switch. B necessarily permits a direct-traffic window
on this Linux implementation. A avoids the shell-induced window while alive.
Normal owned stop sends SIGTERM first so sing-box can remove auto_route rules;
forced SIGKILL can leave rules even though the nonpersistent TUN disappears.
No option should delete arbitrary host rules or start a second routing engine
when ownership/cleanup cannot be confirmed.

## Recommendation and acceptance

Choose A as a separate coordinated backend/plugin change; ship C's limitation
text now. B is not an <=80-line fix with adequate semantics and tests. Persisting
intent alone introduces state with no consumer and does not fix this incident,
so no preparatory runtime state is added here.

Acceptance for A: connect on an authorized host, detach/restart/crash only the UI,
prove the same engine PID/TUN and traffic survive, attach and inspect a fresh
snapshot, reject a second controller, then explicitly stop and compare owned
ip rules/routes before/after. Also test daemon crash and hung-stop recovery.
This round covers EOF wiring with a fake engine and reads existing Core tests;
it cannot establish kernel route cleanup. Arch CI uses offline smoke; the
optional TUN smoke has auto_route=false and does not test EOF while connected.
