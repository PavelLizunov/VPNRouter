# N-2b: live tools in the UI exploration MCP

## Why

The owner asked for denser MCP coverage: not only clicks, but every tab and inner tab and the real functions (connecting to configs, timing). The headless MCP renders and clicks a view model; it cannot connect. The live harness (N-2, `tools/live/`) drives the real app on WINBRAT; this wires it into the same MCP so an agent calls one tool and gets timings, log findings and screenshots back.

## What

`VPNRouter.Tools/UiMcp/LiveTools.cs`, three tools next to the `ui_*` ones:

- `live_run(scenario, count, max_servers, tab, timeout_sec, max_images)`: copies `tools/live/*.ps1` to the worker (`scp`), runs `live-run.ps1` (ssh, scheduled task in the interactive session), then returns steps.log, done line, log findings, crash events, the file list and the requested screenshots. Scenarios: tabs, cycles, servers, ping, modes, dump.
- `live_runs(limit)`, `live_report(run, files)`: read earlier runs.

Host from `VPNROUTER_LIVE_HOST` (default the Windows worker), scripts folder from `VPNROUTER_LIVE_SCRIPTS` or found upward from the working directory. Arguments are validated (scenario/tab enums, bounded numbers, run and file names by pattern) before anything reaches ssh; ssh/scp run with BatchMode and the pinned host keys, no host-key override. The tools run off the UI thread (they wait for minutes and never touch Avalonia).

## Verification

CI builds the binary (`build-ui-mcp.yml`); then `mcpcall.py` against it: tools/list shows the nine tools, invalid arguments are rejected, `live_runs`, `live_run dump`, `live_run cycles` against the worker return evidence and images.

## Rollback

Revert the PR; the `ui_*` tools are untouched.

## Outcome

Merged as #520. Verified with the CI-built binary: argument validation, `live_runs`, `live_report`, `live_run` for `dump`, `tabs`, `cycles`, `servers`, `ping`, `modes`, with images. Scripts folder from `VPNROUTER_LIVE_SCRIPTS` or found from the working directory; host from `VPNROUTER_LIVE_HOST`.
