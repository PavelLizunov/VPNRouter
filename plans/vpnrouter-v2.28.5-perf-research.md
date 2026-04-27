# v2.28.5 — Free Configs perf research

User report (2026-04-28):
- Memory balloons during a search (parsing + TCP-test stage).
- Memory does **not** drop after the search completes — process stays
  fat indefinitely.
- CPU stays high after the search ends — something keeps spinning.
- Need: research, then a more memory-efficient algorithm. If memory
  bloat is unavoidable, notify the user at startup.

This is a v2.28.5 plan, separate from v2.28.4-rN (which is UX
polish, not perf).

---

## Phase 1 — measure before changing anything

Build a debug-instrumented run and capture before/after numbers.

1. **Baseline working set** — fresh launch, no search yet:
   ```
   tasklist /FI "IMAGENAME eq VPNRouter.App.exe" /FO LIST
   ```
   Record: PrivateMemory, WorkingSet, ManagedHeap (size 0/1/2/LOH).

2. **Mid-search snapshot** at the peak: trigger a default search
   (target=10, maxPing=400) and snapshot every 5 s.

3. **Post-search snapshot, immediate**: grab numbers right when the
   search ends and the displayed list is populated.

4. **Post-search snapshot, +60 s**: same numbers a minute later. If
   still high → leak or live-rooted graph; if drops → just GC2 lag.

5. **Force GC and re-snapshot**: call `GC.Collect()` from the
   Avalonia debug hook (or attach dotnet-counters) to verify whether
   memory is reclaimable but the runtime simply hasn't run gen-2.

Tools:
- `dotnet-counters monitor --process-id <pid> --counters
  System.Runtime,Microsoft.AspNetCore.Hosting`
- `dotnet-dump collect -p <pid>` then `dotnet-dump analyze` →
  `dumpheap -stat -live` to find what's actually rooted.
- WPA / PerfView for CPU sampling (Windows). On the VM that's the
  dev box; the user is running self-contained .NET 8 already, so
  attaching is straightforward.

Output: a row in the plan with concrete numbers. Don't move to
Phase 2 without these.

## Phase 2 — likely culprits (research)

Hypotheses to verify against the dump, in priority order:

### H1. `_allConfigs` holds full `FreeConfigEntry` objects forever

Each entry includes: full vless URI string, parsed host/port/uuid,
GeoIP CountryCode, last-test-result fields, status. Entries for
**all** discovered configs (not just Verified) used to live in
the cache until v2.28.4-r4 added `EnsureCacheLoaded` pruning to
Verified-only on session restart.

Mid-search though, the aggregator pulls 25 k entries into memory
in `FreeConfigAggregator.RefreshAsync` and tests them all. Even
with the goal-seeking early stop firing at ~20 candidates, the
candidate list pulled from the pool fetcher / sources can still
be the full 25 k — the early stop only short-circuits **testing**,
not **fetching/parsing**.

Fix candidates:
- Stream parse instead of materialising `List<FreeConfigEntry>` for
  the whole pool. Iterate, test, drop the ones we won't keep.
- After Refresh + Deep Verify completes, replace `_allConfigs` with
  the Verified subset (drop the ~24 990 untested entries).
- Cap pool size at fetch time (server-side `pool.json` already
  pre-filters — verify the in-app fetcher actually honours the cap).

### H2. ETW provider stays subscribed after process detection ends

`EtwProcessMonitor` runs on a dedicated background thread. After
the user stops the VPN, the ETW session may not be properly
disposed → the thread keeps draining events from the kernel.

Fix candidates:
- Verify `Dispose` is called from `MainWindowViewModel.OnClosing` or
  equivalent.
- Check that `TraceEventSession.StopOnDispose = true` and that the
  Etw provider is disabled before the manager is GC'd.

### H3. sing-box child process retains pipes

When the Deep Verifier spawns a temporary sing-box for each
candidate, the parent reads stdout/stderr through `Process.OutputDataReceived`.
If the handlers retain `StringBuilder` buffers across calls and
don't clear them, log lines pile up.

Fix candidates:
- After each Deep Verify probe, explicitly null the captured logs.
- Use a fixed-size circular buffer instead of unbounded
  `StringBuilder`.

### H4. Avalonia visual leak via ItemsControl recycling

The Free Configs list uses `<VirtualizingStackPanel>`, but if a
`DataTemplate` accidentally captures `this` (the page) or the
ViewModel via `$parent[UserControl]` in a binding closure, the
visual tree may keep stale rows alive.

Fix candidates:
- Audit `$parent[UserControl]` bindings in the row template.
- Confirm `FreeConfigItemViewModel` doesn't hold strong refs to
  the parent VM.

### H5. UpdateChecker / SubscriptionFetcher HttpClient pooling

If a static `HttpClient` is created per check and not pooled, each
check leaves a `SocketsHttpHandler` rooted with active connection
pools. Over a few hours this accumulates.

Fix candidates:
- Confirm `HttpClient` is a singleton in `UpdateChecker.cs` and
  `SubscriptionFetcher.cs`.

### CPU stays pegged after search

Most likely:
- Background timer in `HealthMonitor` polling every Xs → that's
  expected, but if the interval is too tight (e.g. 100 ms) it
  shows up as constant CPU.
- ETW event drain loop spinning hot.
- Deep Verifier `await Task.Delay` in a tight retry loop.

`dotnet-counters` will show which thread is hot. Check
`% Time in GC` — if high, it's just GC pressure from H1/H4.

## Phase 3 — fixes, ordered

Based on Phase 2 findings, apply the smallest fix that addresses
the largest issue first. Each fix is a separate -rN release with
fresh measurements:

1. **Trim `_allConfigs` to Verified after each search.** Cheapest
   fix; should shave the bulk of the heap. (~mins to implement,
   high impact for H1.)
2. **Audit and dispose ETW**. Medium effort, fixes CPU pegging if
   H2 is the culprit.
3. **Audit Process pipes** in DeepVerifier. Medium effort.
4. **Audit visual recycling** — only if H4 measurable.

After each fix: re-snapshot mid-search and post-search. Goal:
post-search WorkingSet returns to within ~10% of baseline within
30 s.

## Phase 4 — fallback if bloat is fundamental

If after Phase 3 the bloat is still unavoidable (e.g. .NET runtime
won't release LOH back to the OS without a full GC, which is by
design), add a startup notice:

- One-time toast on first launch after install: "Поиск конфигов
  использует ~XXX MB RAM на пике; после поиска часть остаётся
  занятой — это поведение .NET runtime, не утечка."
- Don't show repeatedly. Stored in `app.first_launch_perf_notice_shown`.

Implementation:
- Add `FirstLaunchPerfNoticeShown : bool` to `AppSettings.AppSection`.
- On Free Configs page first activation, check the flag, show a
  dismissible info banner once.

## Phase 5 — algorithm: smaller-batch fetch + test

Even if memory stays stable, reduce time-to-first-result by
processing the pool in batches:

```
foreach batch of 500 from pool:
    parse + dedupe → candidates[]
    TCP-test candidates[] (parallel, max 50 concurrent)
    take Ok subset
    Deep Verify those (sequential, per-config sing-box)
    if found >= target: stop
    else: drop the 500-batch from memory, fetch next 500
```

Vs current:
- Fetches the full 25 k pool into memory
- Tests all of them (or until goal-stop)
- Hands the entire Ok subset to Deep Verify

Smaller batches = more incremental memory pressure relief +
faster perceived progress (verified rows trickle in over time
instead of in a single wave at the end).

Implementation cost: refactor `FreeConfigAggregator.RefreshAsync`
+ chained DeepVerifyTopAsync into a single batched pipeline.
Risk: medium — touches Core, needs new tests.

Defer to v2.28.5 only if Phase 3 trimming alone doesn't satisfy
the user.

## External dependencies — known issues to audit

1. **Avalonia 11.x**: known `VirtualizingStackPanel` regressions
   around recycling — check release notes for current minor for
   any "memory" / "leak" tag.
2. **System.Diagnostics.Tracing.TraceEvent** (ETW lib): historically
   has had `Dispose` ordering quirks. Check upstream issues for
   "leak", "thread keeps running".
3. **Microsoft.Diagnostics.NETCore.Client** / dotnet-counters
   instrumentation — should not be in production build. Confirm
   it's `Debug`-only.
4. **Newtonsoft.Json**: deserialisation of large pool.json may
   buffer the whole stream — switch to `System.Text.Json` with
   `Utf8JsonReader` for streaming.

## Acceptance

- Baseline launch: WorkingSet ~120 MB (single-file self-contained).
- Mid-search peak: WorkingSet ≤ 250 MB on a default search.
- Post-search +60 s: WorkingSet ≤ 150 MB.
- CPU at idle (post-search): < 1 % on dev box.
- If unattainable: startup notice added (Phase 4) and memory
  numbers documented in README so users have realistic
  expectations.

## Dependencies / unknowns

- Whether Avalonia 11 retains visuals after `ItemsSource` changes
  even with virtualization — needs a small repro test before
  committing time to H4.
- Whether `Process.OutputDataReceived` is the actual leaker in the
  Deep Verifier or whether the leak is in sing-box's own log
  pipeline (sing-box is upstream binary, can't fix from here).

## Out of scope

- Switching VPN engine away from sing-box.
- Replacing Avalonia with a different toolkit.
- Moving aggregation to a server-side worker (already partially
  done via `pool.json` cron, but in-app fetch path stays).

## Roadmap link

After this plan executes, v2.28.5 cuts as a stable. v2.28.4 cuts
once the UX-polish stream finishes (probably -r6 or -r7).
