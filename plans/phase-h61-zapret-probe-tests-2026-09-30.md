# H-61: characterization tests for the Flowseal strategy sweep

## Why

Fourth step of the Zapret test plan. `ZapretAutoStrategy.RunFlowsealProbeAsync` (355 lines) is what the "Включить обход
блокировок" button relies on: it starts Flowseal's own `utils\test zapret.ps1`, feeds it the two menu answers, parses its
console output while it runs (configuration headers `[n/m] name.bat`, `[HTTP]`/`[TLS1.2]`/`[TLS1.3]` status lines, `Best
config:` line, `[ERROR]`/`[WARN]` lines), keeps a pass count per strategy, kills the script as soon as one strategy passes
16 checks in a row, falls back to the best-scoring strategy when there is no `Best config:` line, honours cancellation
and writes a probe log. It was only tested on non-Windows. It is the largest remaining function of the Zapret feature and
must be pinned before it is split.

## What

`VPNRouter.Tests/ZapretFlowsealProbeTests.cs`, 9 tests, no production change. No seam was needed: the tests write a fake
`utils\test zapret.ps1` into a temporary zapret folder that prints Flowseal-style lines, so the real parsing, scoring,
early kill, cancellation and probe-log code runs end to end without starting winws. They run on Windows in an elevated
process (the method requires it) and skip elsewhere:

- explicit `Best config:` line: winner without `.bat`, tested and total counts, per-strategy pass counts (`UNSUPPORTED`
  counts as a pass, other statuses as a fail), the exact progress sequence;
- no `Best config:` line: the best-scoring strategy is promoted (ties broken by pass ratio);
- every check fails: no winner;
- sixteen passes in a row: early winner, the sleeping script is killed, the result is not delayed;
- only the last eight `[ERROR]`/`[WARN]` lines are kept, trimmed;
- the script receives `2` and `1` on standard input;
- cancellation while the script runs: `canceled`, no winner (the test cancels as soon as the first header is parsed, so it
  does not depend on PowerShell start-up time);
- missing script: `missing_script`;
- the probe log: path under the logs folder, header, timestamped output lines, closing outcome line.

Not covered on purpose: the ten-minute timeout (`sweep_timeout`) and the clean-up of orphan `winws.exe` processes, because
neither can be exercised without waiting ten minutes or starting the real driver.

## Verification

Ran green on `windows-worker` (elevated) before the PR, and in exact-head CI (the Windows job is elevated; Linux skips
these nine tests).

## Outcome

Merged after green exact-head CI.
