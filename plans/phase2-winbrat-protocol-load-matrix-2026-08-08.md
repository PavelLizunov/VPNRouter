# Phase 2 — WINBRAT protocol load matrix

**Owner**: Codex  
**Branch**: `codex/winbrat-loadtest`  
**Parent**: `plans/phase2-winbrat-load-test-mvp-2026-08-08.md`  
**Risk**: MEDIUM — remote UI selection and a fixed executable run on the dedicated test VM  
**Blast radius**: WINBRAT verifier/coordinator and focused tooling tests only; no product/runtime code

## Why

The completed soak proved one unknown selected configuration stayed connected,
while the 30-minute GameUdp baseline ran outside WINBRAT. Neither result compares
the protocol families relevant to the reported game disconnects. The useful next
step is a repeatable matrix that selects a server by the UI's non-secret use-case
chip, proves Full Tunnel plus TUN traffic, and runs the same bounded workload for
each family without reading or returning subscription URLs, hosts, ports or keys.

## What

- Extend the fixed WINBRAT UIA helper with one narrow operation: select a
  subscription row by an allowlisted visible use-case category and ordinal.
- Build the existing fixed GameUdp payload, approve its exact source-built hash,
  deploy and run only that executable through the verified WINBRAT session.
- Require VPNRouter connected, route scope `Tunnel`, Full Tunnel UI state and a
  correlated `VPNRouter-TUN` byte increase. Otherwise return `BLOCKED`.
- Snapshot and restore the original routing mode and selected row through the UI;
  cleanup must leave zero core processes and no TUN.
- Run the first comparison on the subscription families already proven present:
  Games/voice (HY2 family) and Low ping (AWG family), then Daily (VLESS family).

## How

1. Add an allowlisted `ProtocolUseCase` plus bounded ordinal to
   `tools/brat-verify.ps1`. Search only the `SubList` UIA list, scroll through
   virtualized rows, select the ancestor `ListBoxItem`, and return no row name.
2. Add a fixed payload deploy/run path. It accepts no target, executable, rate,
   packet size or duration from the caller; the existing source constants remain
   the authority.
3. Record only category, ordinal, route/full-tunnel booleans, TUN byte-correlation
   boolean and the existing aggregate GameUdp metrics.
4. Add focused source-contract tests for the allowlists, no-secret evidence schema,
   cleanup and absence of generic remote execution.
5. Build/test, run the exact DeepSeek-in-Qwen read-only review, then execute one
   bounded live shakedown before starting 30-minute family runs.

## Verification gate

- [ ] Gate 1 — `dotnet build VPNRouter.sln -c Release`: 0 errors.
- [ ] Gate 2 — focused load/stability tooling tests and full test suite green.
- [ ] Gate 3 — this brief, Outcome and `plans/OPEN-DEFECTS.md` current.
- [ ] Gate 4 — Ponytail minimality and security review of fixed remote execution.
- [ ] Gate 5 — WINBRAT identity, safe row selection, one shakedown, cleanup PASS.
- [ ] Gate 6 — N/A; no product characterization surface changes.

## Acceptance

- No subscription URL, host, port, key, row name, PID, route or raw log is copied
  from WINBRAT or written to evidence.
- A run cannot start unless the fixed payload hash is approved, VPNRouter is
  connected, the fixed endpoint route is `Tunnel`, Full Tunnel is visible, and
  cleanup restoration is armed.
- Each selected category runs the identical 20 pps, 256-byte GameUdp profile with
  its fixed 50 pps burst. A 30-minute result is six independent five-minute runs.
- Any crash, restart, disconnect, lost attribution, corruption, unknown reply,
  unclassified error or failed cleanup makes the family result `FAIL`.
- Loss/RTT comparisons remain measurements until repeated baselines exist; they do
  not trigger MTU, failover or release changes by themselves.

## Outcome

**Status**: IN PROGRESS

