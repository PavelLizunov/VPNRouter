# H-11: ignore a stale warm-up result

## Why

Audit finding FAILOVER-WARMUP-RACE (2026-09-29): the warm-up probe runs under
the session token, which a failover restart does not cancel. `OnConnected` was
guarded by generation, session and PID, but the pipeline then armed the DNS
lockdown and published the "Connected" status without the same guard, so a
stale probe from an earlier start could arm the lockdown for a tunnel that was
not yet serving.

## What

- New `IStartupHost.IsCurrentStart(pid)` holds the guard that `OnConnected`
  already used (`OnConnected` now calls it).
- `ScheduleWarmupProbe` returns without status, `OnConnected` or lockdown when
  the start is stale, and skips the final status when the probe ran out of
  attempts for a stale start.
- The two existing test hosts implement the member; one Windows-only
  end-to-end test forces a generation change before the probe completes and
  asserts no lockdown is armed.

## Verification

Exact-head CI: Windows job runs `VpnEngineDnsLockdownLifecycleTests`.

## Outcome

Merged as #347 on 2026-09-29; exact-head CI green.
