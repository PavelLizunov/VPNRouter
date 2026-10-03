# N-2: the live functional harness (night pool item 2)

## Why

The owner asked for denser MCP coverage: not only clicks, but every tab and inner tab, and the real functions - connecting to configs, timing, and so on. The headless UI probe cannot connect a tunnel; the real app on the Windows worker can.

## What

`tools/live/live-harness.ps1` drives the running desktop app through UI Automation in the interactive session (started by `tools/live/live-run.ps1` as a scheduled task) and writes `report.json`, `steps.log` and screenshots under `C:\android-build\live\<run>`.
Scenarios: `tabs` (every main tab, every inner tab / section / segment, Zapret and Telegram segments, a screenshot each), `cycles` (N connect / disconnect cycles with timings, the app's own phase timeline from its log, the public IP before / during / after, sing-box alive checks),
`servers` (connect to every subscription server in turn, timed, IP checked), `ping` (Test all and Deep verify, disconnected and connected, with per-row results), `modes` (switch Selected apps / All traffic while connected, the window must never fall back to Connect), `dump` (every visible control of a tab, for selectors).
After every scenario: warnings / errors the app logged since the start (noise filtered), Windows crash events, "app still running".

Findings of the first runs (r26 on WINBRAT): a connect takes 10-12 s: 2.2 s fetching the subscription again and probing all 13 servers (N-1a removes both), 5 s resolving process names with one `where.exe` per name (N-1b), 2-3 s for sing-box and the TUN adapter; a stop takes 5-6 s: 2.6 s waiting for the TUN adapter removal, 1 s deleting the DNS lockdown rules with netsh.

## Not covered

MCP wrappers (`live_*` tools of the UI MCP server, next), Android, hover with a real cursor (the headless probe does that).

## Verification

Runs on WINBRAT against the installed candidate; each run's report is the evidence.

## Outcome

Pending.
