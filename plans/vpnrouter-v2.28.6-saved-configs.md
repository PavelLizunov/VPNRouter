# v2.28.6 — Free Configs «Сохранённые» tab + per-row Recheck

## Trigger

User feedback on v2.28.5-r6 (2026-04-28):

> "после нового стара старые найденные конфиги пропадают, это ок?
> может сделам доп владку в конфигах с найденными? и очевидно что они
> не будут вечно рабочими и в найденный нужно возможность
> перепроверки"

Currently the Free Configs page has one ephemeral list — every new
search wipes it and re-fills from cache + new Verifieds. Users
perceive this as "losing" their working set even though the
PreservePreviousValidation merge re-tests cached Verifieds first.

## Confirmed direction (Variant A — 2-tab)

User confirmed Variant A from the design discussion:

```
Free Configs page:
├── ▶ Поиск          — search flow, ephemeral session results
└── ★ Сохранённые    — persistent list of all-time-verified configs
```

User-confirmed sub-decisions:

- **Q2 — Recheck mode**: always full deep-verify (sing-box + HTTPS).
  No TCP+TLS-only fast option.
- **Q3 — Stale threshold**: Verified <24h ago = "fresh"; everything
  older shown dim with "Verified N days ago" timestamp.
- **Q4 — Failed re-verify**: keep entry in saved list, mark as
  "Failed last check"; preserve last-good ping/speed numbers so the
  user can still see "this used to work at 15 ms / 50 Mbps".
- **Q5 — Dedupe key**: `FreeConfigEntry.Id`
  (SHA1 of `host:port:uuid`).
- **Q1 — Storage** (delegated to me): keep single
  `%ProgramData%\VPNRouter\cache\free_configs.json` as the saved-list
  source of truth. The "Поиск" tab holds session-results in memory
  only; on each Verified found, it merges into the saved cache
  immediately. No second file. Advantage: one source of truth, no
  consistency bugs.
- **Q6 — "Поиск" tab content** (delegated to me): shows results of
  the current session — every Verified that passed in this run,
  whether newly discovered or re-verified-from-saved. Persists until
  the user starts another search (which clears and rebuilds the
  in-memory list). Distinct from the all-time saved list shown in
  tab 2.

## UX layout

### Tab strip

A horizontal `<TabStrip>` at the top of the right pane, two items:

| Tab | Header | Subhead | Default |
|---|---|---|---|
| Поиск | ▶ Поиск | "Search for new working configs" | yes |
| Сохранённые | ★ Сохранённые ({N}) | "Configs you've found before" | no |

The "Сохранённые" header carries the count badge so the user knows
at a glance whether they have saved entries to come back to. If 0,
strip the badge.

### Tab "Поиск" — same as today

The current FreeConfigsPage layout post-v2.28.5-r6 is the "Поиск"
tab without changes:

- Green card with title + description + Settings expander +
  Start/Stop button
- List below: session results (in-memory)
- Bottom Apply bar with Connect button

The list shows everything verified during this session. After a
search completes, the user can scroll the results and connect to
any of them. Closing the app or starting a new search clears the
list.

### Tab "Сохранённые" — new

```
┌─ Сохранённые (12) ────────────────────────────────────────┐
│  These are configs you've found in past searches.         │
│                                                            │
│  [↻ Recheck all stale (8)]  [✕ Clear all]                 │
│                                                            │
│  Country  Endpoint               Latency  Speed  Status   │
│  🇩🇪 DE    de1.example.com:443    15 ms   45 Mbps  fresh  │
│  🇳🇱 NL    nl3.proxy.org:443      28 ms   30 Mbps  fresh  │
│  🇫🇮 FI    fi-vless.host:443      42 ms   12 Mbps  2d ago │
│  🇷🇺 RU    ru.dead.host:443       —       —       failed  │
│  ...                                                       │
│                                                            │
│  Per-row buttons (right side): [↻] [Connect] [✕]          │
└────────────────────────────────────────────────────────────┘
```

Sort order:
1. Verified <24h ago (fresh, full opacity)
2. Verified 1–7 days ago (ageing, dim 75%)
3. Verified >7 days ago (stale, dim 50%)
4. Last known Verified, currently failed (50% opacity, italic)
5. Auto-evict at >30 days no-verify

A "Status" column replaces "Transport" in this tab; shows freshness
("fresh" / "2d ago" / "failed") instead of TCP/UDP transport detail.

### Per-row actions on Сохранённые

- **↻ Recheck**: full deep-verify on this single config. Row shows
  spinner + "Checking..." while in flight. On completion: status
  updated, row re-sorted to new position.
- **Connect**: same as Apply Selected — applies this config as the
  active server.
- **✕ Delete**: removes from saved list (and cache file). No
  confirmation — the user will see it in the previous tab if they
  searched recently, and re-discovered in next search if upstream
  pool still has it.

### Bulk actions on Сохранённые

- **↻ Recheck all stale**: counts entries with `LastTestedAt > 24h
  ago`, runs full deep-verify on each (5-permit semaphore — same
  as the search flow). Status banner during run; cancellable.
- **✕ Clear all**: confirm dialog → wipes the entire saved list.
  Cache file becomes empty `{ Configs: [] }`.

Disabled while either tab's search/recheck is running (single-
operation IsBusy).

## Storage layout

```
%ProgramData%\VPNRouter\cache\free_configs.json  (existing)
{
  "LastAggregatedAt": "...",
  "Configs": [
    {
      "Id": "...",
      "Status": "Verified",
      "LatencyMs": 15,
      "MeasuredBandwidthMbps": 45,
      "LastTestedAt": "2026-04-28T10:00:00Z",
      "LastVerifyFailedAt": null,
      ...
    }
  ]
}
```

New field on `FreeConfigEntry`:

- `LastVerifyFailedAt: DateTime?` — set when a re-verify fails on
  an entry that was previously Verified. Preserves the last-good
  values (LatencyMs, MeasuredBandwidthMbps, Sni etc.) so the row
  can show "10 ms · 50 Mbps" with the "failed" badge on top.

The `Status` enum stays as-is. We don't introduce a new "FailedAfterVerified"
status — instead, when a re-verify fails on a previously-Verified
entry, we set `LastVerifyFailedAt = DateTime.UtcNow` but **leave
`Status = Verified`** so the historical numbers persist. Display
logic checks `LastVerifyFailedAt > LastTestedAt` to decide the
"failed last check" badge.

Auto-evict policy on cache load (`EnsureCacheLoaded`):

```csharp
var now = DateTime.UtcNow;
_savedConfigs = file.Configs
    .Where(c =>
        c.Status == FreeConfigStatus.Verified &&
        c.LastTestedAt.HasValue &&
        (now - c.LastTestedAt.Value).TotalDays <= 30)
    .ToList();
```

## ViewModel changes

`FreeConfigsPageViewModel`:

- Split `_allConfigs` into two lists:
  - `_savedConfigs: ObservableCollection<FreeConfigEntry>` — all-time
    verified, the source for tab 2
  - `_searchSessionResults: ObservableCollection<FreeConfigEntry>` —
    current search session, source for tab 1

- New properties:
  - `SelectedFreeTabIndex: int` (0 = Поиск, 1 = Сохранённые)
  - `IsSearchTab` / `IsSavedTab` getters
  - `SavedConfigsCount` for the tab badge

- `RefreshAsync` (search flow): writes Verified entries to **both**
  collections inside `VerifyOneAndAppendAsync`. Currently writes
  only to `_allConfigs`; the change is one extra `Add` call into
  `_savedConfigs` (with dedupe by Id).

- New commands:
  - `RecheckOneCommand(FreeConfigItemViewModel)` — single-row
    re-verify
  - `RecheckAllStaleCommand` — bulk
  - `RemoveFromSavedCommand(FreeConfigItemViewModel)` — single-row
    delete
  - `ClearAllSavedCommand` — bulk delete

- `EnsureCacheLoaded` updated: populates `_savedConfigs` from cache
  with the 30-day eviction filter.

`FreeConfigItemViewModel`:

- New `FreshnessLabel` getter: "fresh" / "2d ago" / "failed" /
  "stale (>7d)" — derived from `LastTestedAt` and
  `LastVerifyFailedAt`.
- New `OpacityValue` getter for visual dim: 1.0 fresh / 0.75 ageing /
  0.5 stale-or-failed.
- New `IsRecheckRunning` flag (bound by VM during single-row
  recheck) for the in-row spinner.

## XAML changes

`FreeConfigsPage.axaml`:

- Wrap current Grid in a top-level `<TabControl>` with two
  `<TabItem>`s.
- Tab "Поиск": move the existing green card + list + apply bar
  inside.
- Tab "Сохранённые": new layout with bulk-action button row + list
  with the freshness column + per-row action buttons.

The Apply bar at the bottom can be shared across both tabs (it's
generic Connect-the-selected behaviour). The selected item state
needs to track which tab it's on.

## Storage migration

No schema break. Existing `free_configs.json` already stores the
right shape; `LastVerifyFailedAt: null` defaults are JSON-friendly.
The 30-day eviction is applied on load — older cached entries
silently disappear, which is the desired behaviour anyway.

## Tests

New `FreeConfigItemViewModelFreshnessTests` class:

1. `Verified <24h → "fresh", opacity 1.0`
2. `Verified 25h ago → "1d ago", opacity 0.75`
3. `Verified 2 days ago → "2d ago", opacity 0.75`
4. `Verified 8 days ago → "stale (>7d)", opacity 0.5`
5. `LastVerifyFailedAt > LastTestedAt → "failed", opacity 0.5,
   keeps last-good Latency/Bandwidth`
6. `LastTestedAt + 30 days threshold → eligible for auto-evict`

`FreeConfigKeepPolicyTests` updated:

- Add a test that `ShouldKeepInLiveCache` returns true even for an
  entry with `LastVerifyFailedAt > LastTestedAt` (we want to keep
  it for the saved-list, just visually distinguish).
- This is a behaviour change from r2's strict "Verified-only" —
  now the policy is more like "Verified-once, possibly currently-
  failed". Need to think about whether the search-tab list should
  also keep these (probably no — search should be ephemeral and
  show only currently-Verified in its session).

`FreeConfigsRecheckTests`:

- Mock-based test that single-row recheck produces the right state
  transitions (Verified → Probing → Verified, or → Failed-with-
  preserved-numbers).

## Acceptance

- After a search, tab "Поиск" shows the session results; tab
  "Сохранённые" shows accumulated history (count badge +N).
- Clicking ↻ on a single row in Сохранённые runs a deep-verify;
  row visibly updates within 5–10 s.
- "Recheck all stale" with 8 stale entries finishes in ~30–40 s
  (5-permit semaphore × 5 s each).
- Closing and reopening the app: Сохранённые tab still populated
  from cache. Entries verified more than 30 days ago are gone.
- Failed re-verify keeps the row visible with "failed" badge and
  preserved last-good ping/speed.

## Implementation phases

1. **Phase 1 — Schema + ViewModel split** (~150 LOC)
   - Add `LastVerifyFailedAt` to `FreeConfigEntry`
   - Split `_allConfigs` → `_savedConfigs` + `_searchSessionResults`
   - Add `SelectedFreeTabIndex` + getters
   - Eviction policy in `EnsureCacheLoaded`
   - Tests for new fields

2. **Phase 2 — Сохранённые tab UI** (~250 LOC)
   - TabControl wrap in XAML
   - Сохранённые tab layout: header + bulk buttons + list with
     freshness column
   - Per-row action buttons (↻ Recheck, Connect, ✕ Delete)
   - Visual dim by opacity binding

3. **Phase 3 — Recheck commands** (~200 LOC)
   - `RecheckOneCommand` single-row
   - `RecheckAllStaleCommand` bulk with 5-permit semaphore
   - Failed-re-verify state preservation
   - In-row spinner during single recheck
   - Cancellation

4. **Phase 4 — Wire search flow into saved-list merge** (~50 LOC)
   - `VerifyOneAndAppendAsync` writes to both lists
   - Dedupe by Id when adding to `_savedConfigs`
   - Update auto-save on session-end

5. **Phase 5 — Tests + doc** (~100 LOC)
   - Freshness label / opacity tests
   - Recheck state-transition tests
   - Bulk-recheck integration test
   - Update README test flow

Total: ~750 LOC across Phases 1–5. Should land as v2.28.6-r1
through ~r3 prereleases as feedback comes in.

## Risks / unknowns

- **List growth**: if user runs ~10 searches/day, each finding ~10
  Verified, that's ~100 entries/day. The 30-day eviction caps at
  ~3000 entries — large but manageable for VirtualizingStackPanel.
  Worth measuring in r1 if memory profile slips.
- **Recheck UX during long bulk runs**: 30+ stale entries × 5 s
  each = 2.5 min bulk recheck. Need clear cancel + per-entry
  status updates (similar to v2.28.5-r6's per-probe status text).
- **Failed-re-verify visual loop**: if a config repeatedly fails
  every recheck, user might keep seeing it. Auto-delete after
  N consecutive failures? Defer to v2 if user complains.

## Out of scope

- Sync saved list across devices (no cloud component)
- Sharing a saved list (export/import config bundles — separate
  feature)
- Saved-list categorisation / labels / favourites (just verified
  flat list for v1)

## Cross-references

- `plans/vpnrouter-v2.28.5-perf-research.md` — perf baseline; saved
  list shouldn't regress mid-search peak (still gated by batched
  flow).
- `plans/vpnrouter-free-configs.md` (if exists) — original feature
  doc; this plan extends it.
- `MEMORY.md` — bump in-flight prerelease line when this lands as
  v2.28.6-r1.
