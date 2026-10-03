# N-3a: failover that picks a reachable server, messages in the app language, quieter logs

## Why (evidence)

- `AutoFailoverEngine` took the first untried server in list order. A dead one costs a full restart attempt (about 10 s) of the three allowed.
- Its user messages (and the "checking UDP servers" status) were Russian-only text in Core, shown to English users.
- The WINBRAT logs of the live runs (2026-10-04): every normal stop wrote a 50-line `sing-box crash tail` at WARN (connection targets included), every connect wrote 88 `Skipping rule for X.exe` warnings that hide the real ones.

## What

1. Failover probes the first eight untried candidates in parallel (`TcpTlsProbe.ProbeServerAsync`, 4 s total budget, physical NIC binding already in place from N-1a) and takes the fastest that answers; if none is confirmed it prefers one the probe cannot judge over a known-dead one, else the first in list order as before. Tests keep the old behaviour when no probe is wired.
2. `Strings.Failover*` (ru/en) for every failover message and the UDP check status.
3. No crash tail for an expected exit during an intentional stop or restart; per-app firewall skip lines are debug with one summary line at connect and at kill-switch enable.

## Verification

New unit tests (probe choice, ties, all dead, undecided, throwing probe, probe cap, no probe, budget, rejection before probing, both languages); existing failover tests made language-neutral; windows-worker run of AutoFailover/NightFailover/Firewall/SingBoxManager/StartupPipeline/VpnEngine suites.

## Rollback

Revert the PR.

## Outcome

Pending.
