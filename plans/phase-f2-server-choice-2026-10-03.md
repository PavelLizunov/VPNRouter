# F-2: the server Connect picks is not a surprise (tester report on r20, item 3)

## Why

Tester log 01:04:21: the Connect pre-flight (`SmartConnect`) probed all 13 servers through the live tunnel (answers in under 5 ms, `Implausible`), kept only the three Hysteria2 servers that the UDP probe reports as `Ok` with
"udp open (no reply)" (a QUIC server never answers a blind datagram, so that proves nothing), and switched the selected server to `Iceland New HY2`. The Simple screen never said which server was chosen or why.

## What

1. A silent UDP port is reachable but not verified (`ServerProbeResult.IsVerified`, `ServerLiveness.Verified`); `PickBest` prefers verified servers and falls back to unverified ones only when nothing else is alive. A server the user
   selected is kept as long as it is reachable, verified or not.
2. When the pre-flight replaces the selected server, the home screen says so ("'X' was not responding; picked the fastest working server").
3. With F-1 the pre-flight no longer runs while the tunnel is up, so probes cannot be polluted by the tunnel itself from the Connect button.

## Not covered

"Test all" while connected still probes through the tunnel (shows dead servers); a probe bound to the physical interface is a follow-up. Intent-specific picks (gaming/privacy) are unchanged.

## Verification

`ServerHealthProbeVerifiedTests` (including the tester's polluted session), existing ServerHealth suites on windows-worker at the exact SHA, CI.

## Rollback

Revert the PR.

## Outcome

Pending.
