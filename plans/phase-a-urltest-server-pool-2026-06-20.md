# Phase A — opt-in urltest auto-select over the subscription server pool

**Owner**: Claude session
**Branch**: `main` (project direct-commit; feature flag/opt-in, default off)
**Roadmap ref**: `plans/server-health-failover-backlog-2026-06-19.md` §A + review §C2/E4/F2
**Effort**: ~2-3 days
**Risk**: LOW-MEDIUM — reuses existing urltest generation; main new surface is the
server pool + a UI opt-in. Off by default = old behaviour preserved.
**Rollback**: `git revert` (additive + opt-in).

## Why

Today VPNRouter routes through exactly the one server the user picked. If that node
degrades (the German EOF storm), traffic strands until the user manually switches.
sing-box's `urltest` already picks the lowest-latency reachable child from a group —
we just don't feed it a pool. This is the first *user-facing* reliability win:
opt in, and a dead/slow node is bypassed automatically (latency-based; the
runtime-failure-rate failover is the later backlog B, fed by B0 telemetry).

## What (reuse-first — most of this exists)

- **Already there:** `ConfigGenerator.AddOutboundGroup` (ConfigGenerator.cs:~1191)
  emits a `urltest` wrapper over N child outbounds (generate_204, 3m, tolerance 150,
  `interrupt_exist_connections=false`). `LeakProtection` accepts a `urltest` tagged
  `proxy` (LeakProtection.cs:~284-345). So the generation + validation path is done.
- **The gap:** `GetActiveServers` (AppSettings.cs:~839-860) returns only the selected
  server + same-IP companions, so `AddOutboundGroup` gets a pool of 1 → a plain
  outbound, no urltest. Need to widen it to a real pool **when opt-in is on**.
- **New:** a UI opt-in toggle + the pool-selection rules + a localized label.

```diff
- servers = [ selected + same-IP companions ]            // always single-ish
+ if (AutoSelectBestServer)                               // opt-in
+     servers = BuildAutoSelectPool(subscription)         // logical bundle (see decisions)
```

## How

1. Add `AutoSelectBestServer` opt-in (persisted) + `BuildAutoSelectPool(...)` that
   returns a *logical bundle* (see Design decisions) rather than the whole mixed list.
2. Wire it into the server-resolution path so `AddOutboundGroup` receives the pool
   (urltest emitted) when on; unchanged when off.
3. UI: a toggle + label (RU/EN) where the user picks servers.
4. Tests: pool builder (bundling rules, opt-out=single), generator emits urltest for
   the pool + `sing-box check` green, LeakProtection passes, opt-out parity.

### Design decisions (need user steer — see below)
- **Pool scope** — which servers join the urltest group. Mixing countries/protocols
  changes exit IP per-connection → login/geo/fraud breakage (review §E4). Options in
  the question below.
- **UI placement** — where the opt-in lives.
- **Protocol mixing** — VLESS(TCP)/HY2(UDP)/NAIVE probe differently; same-protocol
  pools are cleaner. Default: bundle within one protocol.

### Verification gate
- [ ] Build clean; full non-GUI tests green + new pool/generator tests.
- [ ] `sing-box check` green on a generated multi-server urltest config.
- [ ] Opt-out → byte-identical to today's single-server config (parity test).
- [ ] LeakProtection passes urltest-tagged proxy.
- [ ] MCP/live: with opt-in + a dead node in the pool, traffic flows via a live node.

## Outcome
_TBD_
