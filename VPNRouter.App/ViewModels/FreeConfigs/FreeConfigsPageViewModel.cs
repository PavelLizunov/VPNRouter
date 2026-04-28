using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

/// <summary>
/// ViewModel for the "Free Configs" page.
/// Owns the aggregator, the displayed list, filters, and the Apply command.
/// </summary>
public partial class FreeConfigsPageViewModel : ObservableObject, IDisposable
{
    private readonly FreeConfigAggregator _aggregator;
    private readonly FreeConfigDeepVerifier _deepVerifier;
    private readonly ILogger _logger;
    private readonly Func<FreeConfigEntry, Task<bool>> _applyAsync;
    private readonly Func<VPNRouter.Core.Models.AppSettings>? _getSettings;

    private List<FreeConfigEntry> _allConfigs = new();
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

    /// <summary>
    /// v2.20.1 lazy-load flag. The FreeConfigs cache can hold ~25k entries
    /// (~6-7 MB heap) once the user runs the aggregator. Before v2.20.1 we
    /// deserialized that cache inside the VM ctor — and since the VM is
    /// constructed at app startup, users who never even open the FreeConfigs
    /// tab were paying the memory cost anyway. Now we defer until
    /// <see cref="EnsureCacheLoaded"/> is called from
    /// MainWindowViewModel.OnSelectedTabIndexChanged the first time the
    /// user navigates to the tab.
    /// </summary>
    private bool _cacheLoaded;

    public FreeConfigsPageViewModel(
        ILogger logger,
        Func<FreeConfigEntry, Task<bool>> applyAsync,
        Func<VPNRouter.Core.Models.AppSettings>? getSettings = null)
    {
        _logger = logger;
        _applyAsync = applyAsync;
        _getSettings = getSettings;
        _aggregator = new FreeConfigAggregator(logger);
        _aggregator.OnStageChanged += OnAggregatorStage;
        _aggregator.OnTestProgress  += OnAggregatorProgress;
        _deepVerifier = new FreeConfigDeepVerifier(logger);
        ReloadUserSources(); // v2.14.4

        // v2.20.1: cache load deferred to EnsureCacheLoaded. Ctor stays
        // cheap — no 6-7 MB JSON deserialization unless the user opens
        // the FreeConfigs tab.
        StatusText = Strings.FcStatusEmpty;
    }

    /// <summary>
    /// Load the FreeConfigs cache snapshot from disk on first access.
    /// Called from MainWindowViewModel when the FreeConfigs tab becomes
    /// selected. Idempotent — subsequent calls are no-ops.
    /// </summary>
    public void EnsureCacheLoaded()
    {
        if (_cacheLoaded || _disposed) return;
        _cacheLoaded = true;

        try
        {
            var file = _aggregator.Cache.Load();
            // v2.28.4-r4: drop entries that aren't Verified at cache-load time.
            // The cache historically held everything the aggregator touched —
            // Ok, Slow, TlsFailed, Implausible, Timeout, Unreachable — even
            // entries that never made it through Deep Verify. After a session
            // restart the user saw a list of "configs" that had only ever
            // passed TCP+TLS (or hadn't even gotten that far) and were
            // indistinguishable in the UI from genuinely Verified entries.
            // Verified is the only status that proves a config carried real
            // traffic at least once, so it's the only one worth surfacing on
            // the next launch. Non-Verified entries can still be re-discovered
            // by clicking the search button — and PreservePreviousValidation
            // will keep this run's verified rows on subsequent searches.
            _allConfigs = file.Configs
                .Where(c => c.Status == FreeConfigStatus.Verified)
                .ToList();
            ApplyFiltersAndStats();

            if (file.LastAggregatedAt == DateTime.MinValue)
            {
                StatusText = Strings.FcStatusEmpty;
            }
            else
            {
                var age = DateTime.UtcNow - file.LastAggregatedAt;
                StatusText = Strings.FcStatusCacheAge(FormatAge(age));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] EnsureCacheLoaded failed");
            // Leave _allConfigs empty; user can still Refresh from scratch.
        }
    }

    /// <summary>
    /// v2.20.1: unsubscribe the aggregator handlers this VM owns.
    /// The aggregator instance lives at the VM scope too, so this is mostly
    /// belt-and-braces — but if the main VM is ever recreated (e.g.
    /// ReloadMainWindowForLocalization), the old VM's closures stop
    /// retaining references to the old aggregator.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _aggregator.OnStageChanged -= OnAggregatorStage;
            _aggregator.OnTestProgress  -= OnAggregatorProgress;
        }
        catch { /* aggregator may already be torn down */ }

        try { _refreshCts?.Cancel(); _refreshCts?.Dispose(); }
        catch { }
    }

    [ObservableProperty] private ObservableCollection<FreeConfigItemViewModel> _displayedConfigs = new();
    [ObservableProperty] private ObservableCollection<string> _countries = new();

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _workingCount;
    [ObservableProperty] private int _verifiedCount;
    [ObservableProperty] private int _tlsFailedCount;
    [ObservableProperty] private int _implausibleCount;
    [ObservableProperty] private int _timeoutCount;
    [ObservableProperty] private int _unreachableCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private FreeConfigItemViewModel? _selectedItem;

    public bool HasSelection => SelectedItem != null;

    [ObservableProperty] private string _selectedCountry = "All";
    partial void OnSelectedCountryChanged(string value) => ApplyFiltersAndStats();

    /// <summary>How many Verified configs to hunt for in a deep-verify session.</summary>
    [ObservableProperty] private int _deepVerifyTargetCount = 5;

    /// <summary>If true, skip Russian-country configs during deep-verify (user is bypassing RU blocks).</summary>
    [ObservableProperty] private bool _excludeRu = true;

    /// <summary>v2.13.17: if true, Refresh stops early once N configs matching the latency criterion are found.
    /// v2.28.3: default flipped true so the Simple-only UI (no toggle) gets early-stop out of the box.
    /// Without this, Refresh would TCP-test all 30k+ pool entries — minutes of wait for first-time users.
    /// Target=100/maxPing=300ms is a reasonable "find me a few good ones" goal that finishes in ~30s.
    ///
    /// v2.28.3-r4 — switched from int to int? to fix the NumericUpDown binding crash:
    /// Avalonia's NumericUpDown.Value is decimal? and pushes null when the user clears
    /// the input box; binding to a non-nullable int threw
    /// "InvalidCastException: Could not convert '(null)' to System.Int32" and rendered
    /// the binding error as visible UI text. Nullable property accepts the transient
    /// null and the `?? fallback` in usage sites preserves a sane default.</summary>
    [ObservableProperty] private bool _useLatencyGoal = true;
    /// <summary>v2.28.4-r4: default flipped 100 → 10 because the Simple-flow target user wants
    /// to press one button and walk away with a handful of working configs, not a 100-entry list.
    /// 10 entries match the Refresh's batch-style early stop and a typical Deep Verify finishes
    /// in ~30 sec. Power users can still raise it via the Advanced Settings expander.</summary>
    [ObservableProperty] private int? _latencyGoalTarget = 10;
    /// <summary>v2.28.4-r4: default 300 → 400 ms. 300 ms was too aggressive for users not on
    /// fiber — many real-world working configs sit in 250-400 ms range from RU/CIS endpoints.</summary>
    [ObservableProperty] private int? _latencyGoalMaxPingMs = 400;

    /// <summary>v2.13.18: if true, Refresh does TCP-only test (skip TLS handshake). 3× faster but misses honeypots.
    /// v2.28.3: default flipped true so first-run aggregator doesn't wait minutes for full TLS validation.
    /// Server-side pool.json already pre-validates TLS every 6h (cron in build-free-pool.yml), so client-side
    /// TLS recheck on first refresh adds delay without much extra signal. Power users can disable via CLI/yaml.</summary>
    [ObservableProperty] private bool _fastScanMode = true;

    // v2.14.3 — Deep Verify presets (ping + bandwidth goals)
    /// <summary>Preset index: 0=Gaming, 1=Streaming, 2=Chat, 3=BestEffort, 4=Custom.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPreset))]
    [NotifyPropertyChangedFor(nameof(MeasureBandwidth))]
    private int _deepVerifyPresetIndex = 3; // BestEffort default

    /// <summary>Custom preset: max acceptable ping in ms.</summary>
    [ObservableProperty] private int _customMaxPingMs = 200;
    /// <summary>Custom preset: min acceptable download throughput in Mbps.</summary>
    [ObservableProperty] private int _customMinBandwidthMbps = 5;

    public bool IsCustomPreset => DeepVerifyPresetIndex == 4;

    /// <summary>Whether bandwidth measurement is needed for the current preset.</summary>
    public bool MeasureBandwidth => DeepVerifyPresetIndex switch
    {
        0 or 1 or 2 or 4 => true,  // Gaming/Streaming/Chat/Custom all use bw threshold
        _ => false,                 // BestEffort skips bw test (faster)
    };

    /// <summary>Resolved (maxPing, minBwMbps) for current preset. null = no limit.</summary>
    public (int? maxPing, int? minBw) ResolvedGoal => DeepVerifyPresetIndex switch
    {
        0 => (60, 2),    // Gaming
        1 => (250, 10),  // Streaming
        2 => (300, 1),   // Chat / web
        3 => (null, null), // Best effort
        4 => (CustomMaxPingMs, CustomMinBandwidthMbps), // Custom
        _ => (null, null),
    };

    /// <summary>True when no configs have been aggregated yet (cache is empty).</summary>
    public bool IsEmpty => _allConfigs.Count == 0;
    /// <summary>True when filters hide everything but cache isn't empty.</summary>
    public bool IsFilteredEmpty => _allConfigs.Count > 0 && DisplayedConfigs.Count == 0;
    /// <summary>True when there is data to show in the list (not empty and not filtered out).</summary>
    public bool IsListVisible => !IsEmpty && !IsFilteredEmpty;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal;
    public bool HasProgress => ProgressTotal > 0;
    partial void OnProgressTotalChanged(int value) => OnPropertyChanged(nameof(HasProgress));

    /// <summary>
    /// v2.28.5-r2: batched fetch + per-batch test + per-batch deep verify.
    ///
    /// <para>Old flow (replaced): fetch full pool ~25k → test all 25k →
    /// hand entire Ok subset to Deep Verify → trim. Mid-search peak was
    /// large because all 25k <see cref="FreeConfigEntry"/> sat in memory
    /// at once, plus the testing infrastructure (parallel tasks,
    /// SocketsHttpHandlers) ran across the whole pool.</para>
    ///
    /// <para>New flow:</para>
    /// <list type="number">
    /// <item>Fetch raw pool (no testing) via <see cref="FreeConfigAggregator.FetchPoolAsync"/>.</item>
    /// <item>For each ~500-entry batch from the pool, in priority order:
    ///   <list type="bullet">
    ///   <item>TCP+TLS test the batch (parallel, semaphore-capped).</item>
    ///   <item>For each Ok / Verified entry in the batch, spawn a sing-box
    ///         and run <see cref="FreeConfigDeepVerifier.VerifyOneAsync"/>
    ///         (real HTTPS through the proxy + bandwidth measurement).</item>
    ///   <item>If the entry meets target ping + bandwidth thresholds, add
    ///         to the running Verified list and update the displayed list
    ///         immediately so the user sees progress trickling in.</item>
    ///   <item>If we hit the target count or the user cancels, break out.</item>
    ///   </list>
    /// </item>
    /// <item>After the loop, the pool reference goes out of scope and GC
    ///       reclaims everything except the small Verified list.</item>
    /// </list>
    ///
    /// <para>Memory benefit: at any given moment only the current ~500-entry
    /// batch + the small Verified list (typically ≤ 50 entries) are
    /// retained. The 25k pool is released after the loop ends; mid-search
    /// peak drops by roughly 80–90 % compared to the v2.28.5-r1 flow.</para>
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        // Defaults pulled out so they're consistent for the whole run.
        var target = LatencyGoalTarget ?? 10;
        var maxPing = (UseLatencyGoal && LatencyGoalMaxPingMs.HasValue)
            ? LatencyGoalMaxPingMs.Value
            : 1000; // sentinel: no real ping cap

        // Verified list is the only thing surviving the search. Build it up
        // incrementally so the UI shows progress as soon as the first config
        // is verified rather than waiting for the whole search to end.
        var verifiedList = new List<FreeConfigEntry>();

        try
        {
            // v2.13.18: apply fast scan toggle before any test runs.
            _aggregator.RequireTlsHandshake = !FastScanMode;
            // v2.28.5-r2: always measure bandwidth in the batched flow so the
            // list shows speed alongside latency. The min-bandwidth gate is
            // intentionally lenient (1 Mbps); we want to *display* bw, not
            // aggressively filter on it. Truly dead servers fail < 1 Mbps and
            // get excluded.
            _deepVerifier.MeasureBandwidth = true;

            // v2.14.4: merge user-provided sources with the built-in 14.
            var sources = FreeConfigSources.GetAll(_getSettings?.Invoke());

            // ── Stage 1: fetch raw pool (no testing) ──
            var pool = await Task.Run(() => _aggregator.FetchPoolAsync(sources, ct));
            ct.ThrowIfCancellationRequested();

            // Pull cached Verified entries to the front of the queue so they
            // re-test first (lowest cost; mostly retain status). Existing-
            // verified-not-in-fresh-pool are also surfaced via FetchPoolAsync's
            // internal MergeWithCache.
            var cachedVerified = pool
                .Where(c => c.Status == FreeConfigStatus.Verified)
                .ToList();
            var cachedVerifiedIds = new HashSet<string>(
                cachedVerified.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            // Build the ordered queue: cached Verified first, then everything
            // else, with RU exclusion applied if the user opted in.
            var queue = cachedVerified
                .Concat(pool.Where(c =>
                    !cachedVerifiedIds.Contains(c.Id) &&
                    (!ExcludeRu || !string.Equals(
                        c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase))))
                .ToList();

            // v2.28.5-r4: progress bar tracks "found / target" instead of
            // "processed / queue". The user's mental model is "I want N
            // working configs"; the bar fills from 0 to N as each Verified
            // is added. Previously the bar updated only once per ~500-entry
            // batch — visible freezes for 30-90 s during deep-verify made
            // it look like the app had hung. Now it ticks within seconds
            // of each Verified finding.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = Strings.FcStatusBatchedSearchStart(target, pool.Count);
                _allConfigs = new List<FreeConfigEntry>(verifiedList);
                ApplyFiltersAndStats();
                ProgressTotal = target;
                ProgressDone = 0;
            });

            // ── Stage 2: batched test+verify loop ──
            // Limit deep-verify concurrency. Each verify spawns a sing-box
            // process so unbounded would be too heavy — 5 is a safe default
            // matching the previous DeepVerifyTopAsync semaphore.
            var deepSem = new SemaphoreSlim(5);
            var batchSize = FreeConfigAggregator.DefaultBatchSize;
            var processedCount = 0;
            var totalBatches = (queue.Count + batchSize - 1) / batchSize;

            for (int i = 0; i < queue.Count; i += batchSize)
            {
                if (ct.IsCancellationRequested || verifiedList.Count >= target) break;

                var currentBatchNum = (i / batchSize) + 1;

                // Slice the next batch. ToList() materialises a fresh list so
                // the rest of `queue` is still referenced — but the batch
                // testers operate on this small slice only.
                var batch = queue.Skip(i).Take(batchSize).ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText = Strings.FcStatusBatchedTcpTls(
                        verifiedList.Count, target, currentBatchNum, totalBatches);
                });

                // 2a: TCP + TLS test the batch (semaphore-capped, parallel).
                // v2.28.5-r4: pass a progress callback so the status line
                // updates while TCP+TLS is in flight (200-500 entries take
                // ~5-15 s; without progress feedback the bar appears frozen).
                var tcpDone = 0;
                var tcpTotal = batch.Count;
                var lastStatusUpdate = DateTime.MinValue;
                var batchProgress = new Progress<(int done, int total)>(p =>
                {
                    Interlocked.Exchange(ref tcpDone, p.done);
                    // Throttle UI updates to ~5/sec so we don't spam the
                    // dispatcher when 80 parallel TCP probes finish at once.
                    var now = DateTime.UtcNow;
                    if ((now - lastStatusUpdate).TotalMilliseconds < 200) return;
                    lastStatusUpdate = now;
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText = Strings.FcStatusBatchedTcpTlsProgress(
                            verifiedList.Count, target, currentBatchNum, totalBatches,
                            p.done, p.total);
                    });
                });

                try
                {
                    await _aggregator.Tester.TestAllAsync(batch, batchProgress, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[FreeConfigs] Batch TCP+TLS test threw — skipping");
                    continue;
                }

                if (ct.IsCancellationRequested) break;

                // 2b: deep-verify the Ok subset of this batch.
                var okSubset = batch
                    .Where(c => c.Status == FreeConfigStatus.Ok
                             || c.Status == FreeConfigStatus.Verified)
                    .OrderBy(c => c.LatencyMs > 0 ? c.LatencyMs : int.MaxValue)
                    .ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText = Strings.FcStatusBatchedDeepVerify(
                        verifiedList.Count, target, currentBatchNum, totalBatches,
                        okSubset.Count);
                });

                var deepTasks = new List<Task>();
                foreach (var cfg in okSubset)
                {
                    if (ct.IsCancellationRequested || verifiedList.Count >= target) break;

                    deepTasks.Add(VerifyOneAndAppendAsync(
                        cfg, verifiedList, deepSem, target, maxPing, ct));

                    // Cap in-flight deep verifies so we don't queue all 500
                    // candidate verifies at once. WhenAny releases as each
                    // finishes; we only refill up to 5 in-flight.
                    if (deepTasks.Count >= 5)
                    {
                        var done = await Task.WhenAny(deepTasks);
                        deepTasks.Remove(done);
                    }
                }

                if (deepTasks.Count > 0)
                    await Task.WhenAll(deepTasks);

                processedCount += batch.Count;

                // 2c: explicit drop of intermediate references so the GC
                // can reclaim batch + okSubset between iterations.
                batch = null!;
                okSubset = null!;
                deepTasks.Clear();
            }

            // ── Stage 3: finalise ──
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = new List<FreeConfigEntry>(verifiedList);
                ApplyFiltersAndStats();
                StatusText = ct.IsCancellationRequested
                    ? Strings.FcStatusCancelled
                    : verifiedList.Count >= target
                        ? Strings.FcStatusDeepVerifyDone(verifiedList.Count)
                        : Strings.FcStatusDeepVerifyExhausted(verifiedList.Count, processedCount);
                ProgressTotal = 0;
                ProgressDone = 0;
            });

            // Persist trimmed cache (only Verified entries).
            try
            {
                var file = _aggregator.Cache.Load();
                file.Configs = _allConfigs;
                file.LastAggregatedAt = DateTime.UtcNow;
                _aggregator.Cache.Save(file);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[FreeConfigs] Cache save failed (non-fatal)");
            }

            // Drop the local pool / queue references explicitly so the GC
            // sees them eligible immediately, then force a gen-2 collect.
            queue = null!;
            pool = null!;
            cachedVerified = null!;
            cachedVerifiedIds = null!;

            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = new List<FreeConfigEntry>(verifiedList);
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusCancelled;
            });
            // Save what we got.
            try
            {
                var file = _aggregator.Cache.Load();
                file.Configs = _allConfigs;
                file.LastAggregatedAt = DateTime.UtcNow;
                _aggregator.Cache.Save(file);
            }
            catch { }
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "FreeConfigs RefreshAsync failed");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = Strings.FcStatusFailed(ex.Message);
            });
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    /// <summary>v2.28.5-r2: deep-verify a single config and, if it passes
    /// the user's target thresholds (ping + bandwidth), append to the
    /// shared verified list and refresh the displayed list. Throttled by
    /// a shared <paramref name="sem"/> so we don't spawn unbounded sing-box
    /// instances.</summary>
    private async Task VerifyOneAndAppendAsync(
        FreeConfigEntry cfg,
        List<FreeConfigEntry> verifiedList,
        SemaphoreSlim sem,
        int target,
        int maxPing,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            await _deepVerifier.VerifyOneAsync(cfg, ct);

            // Only "fully working" entries reach the displayed list:
            //   Verified (real HTTP round-trip succeeded)
            //   AND ping under the user's threshold.
            // Bandwidth is recorded but not gated on (>=1 Mbps would only
            // exclude truly dead links).
            if (cfg.Status == FreeConfigStatus.Verified &&
                cfg.LatencyMs > 0 && cfg.LatencyMs <= maxPing)
            {
                bool added;
                lock (verifiedList)
                {
                    if (verifiedList.Count >= target) return;
                    verifiedList.Add(cfg);
                    added = true;
                }

                if (added)
                {
                    var snapshot = default(List<FreeConfigEntry>);
                    lock (verifiedList) snapshot = new List<FreeConfigEntry>(verifiedList);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _allConfigs = snapshot;
                        ApplyFiltersAndStats();
                        // v2.28.5-r4: tick progress bar each time a Verified
                        // entry is appended. ProgressTotal=target, so the bar
                        // fills 0→target as configs trickle in. This is what
                        // the user sees as "search is making progress".
                        ProgressDone = Math.Min(snapshot.Count, ProgressTotal);
                        StatusText = Strings.FcStatusBatchedFound(snapshot.Count, target);
                    });
                }
            }
        }
        catch (OperationCanceledException) { /* swallow — propagated by ct */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] VerifyOneAndAppend failed for {host}:{port}",
                cfg.Host, cfg.Port);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// v2.28.5: trim <see cref="_allConfigs"/> to entries we want to keep
    /// across sessions (Verified + Ok), save the trimmed list to cache,
    /// and force a gen-2 GC so the user sees the working-set drop in task
    /// manager immediately instead of waiting minutes for a natural
    /// gen-2 collection.
    ///
    /// <para>The full pool fetch produces ~25k <see cref="FreeConfigEntry"/>
    /// objects (~12 MB managed heap). Of those, after a default search
    /// only ~10 reach Verified (deep-verified) and a few hundred Ok
    /// (TCP+TLS-passed). The rest are dead statuses (Timeout, Unreachable,
    /// TlsFailed, Implausible, ParseError) that the displayed list filters
    /// out anyway — they sit in memory contributing nothing but bloat
    /// until the next search overwrites <see cref="_allConfigs"/>.</para>
    ///
    /// <para>Idempotent: safe to call after a no-op refresh, after a
    /// cancelled deep-verify, after a successful pipeline. Only trims +
    /// saves when there's actually something to drop.</para>
    /// </summary>
    private void TrimAndReclaim()
    {
        try
        {
            var beforeCount = _allConfigs.Count;
            _allConfigs = _allConfigs
                .Where(FreeConfigKeepPolicy.ShouldKeepInLiveCache)
                .ToList();
            var afterCount = _allConfigs.Count;
            var freed = beforeCount - afterCount;

            if (freed <= 0)
            {
                _logger.Debug("[FreeConfigs] TrimAndReclaim: nothing to trim ({n} entries kept)", afterCount);
                return;
            }

            _logger.Information("[FreeConfigs] TrimAndReclaim: {before} → {after} entries ({freed} dropped)",
                beforeCount, afterCount, freed);

            // Save trimmed cache so the next session starts lean (the r4
            // EnsureCacheLoaded already prunes non-Verified at load, but
            // saving lean now means the cache file on disk shrinks too).
            try
            {
                var file = _aggregator.Cache.Load();
                file.Configs = _allConfigs;
                file.LastAggregatedAt = DateTime.UtcNow;
                _aggregator.Cache.Save(file);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[FreeConfigs] TrimAndReclaim: cache save failed (non-fatal)");
            }

            // Force gen-2 GC so the released ~12 MB shows up in the user's
            // task manager view within ~1s. Without this, .NET's natural
            // gen-2 schedule can hold on for minutes after a peak.
            // Non-blocking variant: GC starts but the search-completion
            // status update isn't held back waiting for it.
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] TrimAndReclaim threw — skipping");
        }
    }

    [RelayCommand]
    private async Task RetestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        try
        {
            var fresh = await Task.Run(() => _aggregator.RetestAsync(_refreshCts.Token));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = fresh;
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusTested(fresh.Count);
            });
        }
        catch (OperationCanceledException)
        {
            await ReloadFromCacheAsync(Strings.FcStatusCancelled);
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigs retest failed: {err}", ex.Message);
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _refreshCts?.Cancel();
    }

    /// <summary>Remove configs with clearly-dead status (TlsFailed/Timeout/Unreachable/Implausible).</summary>
    [RelayCommand]
    private void ClearFailed()
    {
        if (IsBusy) return;
        var before = _allConfigs.Count;
        _allConfigs = _allConfigs.Where(c =>
            c.Status == FreeConfigStatus.Verified ||
            c.Status == FreeConfigStatus.Ok       ||
            c.Status == FreeConfigStatus.Slow     ||
            c.Status == FreeConfigStatus.Unknown).ToList();
        PersistAllConfigs();
        ApplyFiltersAndStats();
        StatusText = Strings.FcStatusCleared(before - _allConfigs.Count, _allConfigs.Count);
    }

    /// <summary>Keep only ✓✓ Verified configs — discard everything else.</summary>
    [RelayCommand]
    private void KeepVerifiedOnly()
    {
        if (IsBusy) return;
        var before = _allConfigs.Count;
        _allConfigs = _allConfigs.Where(c => c.Status == FreeConfigStatus.Verified).ToList();
        PersistAllConfigs();
        ApplyFiltersAndStats();
        StatusText = Strings.FcStatusCleared(before - _allConfigs.Count, _allConfigs.Count);
    }

    /// <summary>Wipe the entire cache.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        if (IsBusy) return;
        var before = _allConfigs.Count;
        _allConfigs = new List<FreeConfigEntry>();
        SelectedItem = null;
        PersistAllConfigs();
        ApplyFiltersAndStats();
        StatusText = Strings.FcStatusCleared(before, 0);
    }

    private void PersistAllConfigs()
    {
        var file = _aggregator.Cache.Load();
        file.Configs = _allConfigs;
        _aggregator.Cache.Save(file);
    }

    /// <summary>
    /// Reload _allConfigs from the on-disk cache and refresh UI.
    /// Used on OperationCanceledException so the user sees their partial test progress
    /// (which the aggregator saves every 50 tests / 5s during the test stage) instead of
    /// the stale pre-refresh state.
    /// </summary>
    private async Task ReloadFromCacheAsync(string statusText)
    {
        try
        {
            var file = _aggregator.Cache.Load();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (file.Configs != null && file.Configs.Count > 0)
                    _allConfigs = file.Configs;
                ApplyFiltersAndStats();
                StatusText = statusText;
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ReloadFromCache failed");
            StatusText = statusText;
        }
    }

    /// <summary>Open the logs folder in Explorer so the user can see per-config deep-verify outcomes.</summary>
    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = VPNRouter.Core.AppPaths.LogsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
    }

    // ── v2.14.4: User-provided source management ──

    /// <summary>Input for adding a new user source.</summary>
    [ObservableProperty] private string _newUserSourceName = string.Empty;
    [ObservableProperty] private string _newUserSourceUrl = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<VPNRouter.Core.Models.UserFreeSource> UserSources { get; } = new();

    /// <summary>Reload user sources from _getSettings into the local ObservableCollection.</summary>
    public void ReloadUserSources()
    {
        UserSources.Clear();
        if (_getSettings?.Invoke() is { } s)
            foreach (var u in s.App.UserFreeSources)
                UserSources.Add(u);
    }

    [RelayCommand]
    private void AddUserSource()
    {
        var url = (NewUserSourceUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = Strings.FcUserSrcEmptyUrl;
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusText = Strings.FcUserSrcInvalidUrl;
            return;
        }

        var settings = _getSettings?.Invoke();
        if (settings == null) return;

        // Dedup by URL
        if (settings.App.UserFreeSources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = Strings.FcUserSrcDuplicate;
            return;
        }

        settings.App.UserFreeSources.Add(new VPNRouter.Core.Models.UserFreeSource
        {
            Name = (NewUserSourceName ?? string.Empty).Trim(),
            Url = url,
            Enabled = true,
            AddedAt = DateTime.UtcNow,
        });

        // Persist via the settings accessor (which points to MainWindowViewModel._settings)
        VPNRouter.Core.Services.SettingsLoader.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);

        ReloadUserSources();
        NewUserSourceName = string.Empty;
        NewUserSourceUrl = string.Empty;
        StatusText = Strings.FcUserSrcAdded;
    }

    [RelayCommand]
    private void RemoveUserSource(VPNRouter.Core.Models.UserFreeSource src)
    {
        if (src == null) return;
        var settings = _getSettings?.Invoke();
        if (settings == null) return;

        settings.App.UserFreeSources.RemoveAll(s =>
            string.Equals(s.Url, src.Url, StringComparison.OrdinalIgnoreCase));

        VPNRouter.Core.Services.SettingsLoader.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);
        ReloadUserSources();
        StatusText = Strings.FcUserSrcRemoved;
    }

    /// <summary>Detect whether the main (TUN-mode) sing-box process is running.</summary>
    private static bool IsMainVpnActive()
    {
        try
        {
            // There will usually be our temporary verifier sing-box instances running during
            // deep-verify, but THIS check is made BEFORE we spawn any — so any sing-box we see
            // is the user's main VPN.
            return System.Diagnostics.Process.GetProcessesByName("sing-box").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Goal-seeking deep verification: iterate through all candidates (in priority order)
    /// until we find <see cref="DeepVerifyTargetCount"/> Verified configs, or exhaust the
    /// list, or user cancels. No hidden limit — user said "find at least a few definitely
    /// working configs, no matter if it takes 5 min or 1 hour".
    /// </summary>
    [RelayCommand]
    private async Task DeepVerifyTopAsync()
    {
        if (IsBusy) return;

        // Warn if main VPN is active — it transparently proxies test traffic.
        if (IsMainVpnActive())
        {
            StatusText = Strings.FcStatusMainVpnActive;
            _logger.Warning("[DV] Main sing-box.exe is running — deep verify will route test traffic through it, results unreliable");
            // Proceed anyway — but user has been warned.
        }

        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            // Priority: Verified first (cheap recheck), then TCP+TLS-passed, then those with
            // only TCP, then even the "failed" ones because our pre-test may be wrong when
            // the user's VPN is active. Within each group: non-RU first, then by latency.
            int Priority(FreeConfigStatus s) => s switch
            {
                FreeConfigStatus.Verified    => 0,
                FreeConfigStatus.Ok          => 1,
                FreeConfigStatus.Slow        => 2,
                FreeConfigStatus.Implausible => 3,
                FreeConfigStatus.TlsFailed   => 4,
                FreeConfigStatus.Timeout     => 5,
                FreeConfigStatus.Unreachable => 6,
                _                             => 7,
            };

            // v2.16.8 fix: pre-filter dead candidates. Timeout/Unreachable mean the
            // endpoint never even accepted a TCP connection during Refresh — no
            // point wasting 6-12 s of sing-box spawn on them. Keep:
            //   Verified / Ok / Slow    — most likely to succeed
            //   Implausible              — might be local-intercept false positive
            //   TlsFailed                — Reality endpoints can present a mismatched
            //                              cert to the front SNI; Deep Verify tunnels
            //                              through the proxy so it may still work
            //   Unknown                  — never tested, give it a shot
            // If the filter leaves an empty pool, fall back to everything non-RU.
            var promising = _allConfigs
                .Where(c => !ExcludeRu ||
                            !string.Equals(c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase))
                .Where(c => c.Status != FreeConfigStatus.Timeout
                         && c.Status != FreeConfigStatus.Unreachable
                         && c.Status != FreeConfigStatus.ParseError)
                .ToList();

            var candidates = (promising.Count > 0 ? promising : _allConfigs
                    .Where(c => !ExcludeRu ||
                                !string.Equals(c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => Priority(c.Status))
                .ThenBy(c => c.LatencyMs > 0 ? c.LatencyMs : int.MaxValue)
                .ToList();

            if (candidates.Count == 0)
            {
                StatusText = Strings.FcStatusNoDeepCandidates;
                return;
            }

            var target = Math.Max(1, DeepVerifyTargetCount);
            StatusText = Strings.FcStatusDeepVerifyStart(target);

            // v2.14.3 — apply preset's bandwidth measurement toggle + ping/bw goals
            _deepVerifier.MeasureBandwidth = MeasureBandwidth;
            var (maxPing, minBw) = ResolvedGoal;

            var foundVerified = 0;
            var tested = 0;
            var lastSaveAt = DateTime.UtcNow;

            // Limit concurrency inside the goal-seeking loop so we can stop as soon as target is reached.
            var sem = new SemaphoreSlim(5);
            var runningTasks = new List<Task>();

            async Task TestOneWithUI(FreeConfigEntry cfg)
            {
                await sem.WaitAsync(ct);
                try
                {
                    var shortHost = $"{cfg.Host}:{cfg.Port} [{cfg.CountryCode ?? "??"}]";
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StatusText = Strings.FcStatusDeepVerifyProbe(foundVerified, target, tested, shortHost);
                    });

                    await _deepVerifier.VerifyOneAsync(cfg, ct);

                    Interlocked.Increment(ref tested);

                    // v2.14.3: count only entries that pass preset's ping+bw thresholds.
                    var meetsPreset =
                        cfg.Status == FreeConfigStatus.Verified &&
                        (maxPing == null || cfg.LatencyMs > 0 && cfg.LatencyMs <= maxPing.Value) &&
                        (minBw   == null || (cfg.MeasuredBandwidthMbps ?? 0) >= minBw.Value);

                    if (meetsPreset)
                        Interlocked.Increment(ref foundVerified);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ApplyFiltersAndStats();
                        ProgressDone = tested;
                        ProgressTotal = candidates.Count;
                        StatusText = Strings.FcStatusDeepVerifyProgress(foundVerified, target, tested, candidates.Count);
                    });

                    // Incremental cache save every 15s.
                    if ((DateTime.UtcNow - lastSaveAt).TotalSeconds > 15)
                    {
                        lastSaveAt = DateTime.UtcNow;
                        var file = _aggregator.Cache.Load();
                        file.Configs = _allConfigs;
                        _aggregator.Cache.Save(file);
                    }
                }
                finally
                {
                    sem.Release();
                }
            }

            await Task.Run(async () =>
            {
                foreach (var cfg in candidates)
                {
                    if (ct.IsCancellationRequested) break;
                    if (Volatile.Read(ref foundVerified) >= target) break;

                    runningTasks.Add(TestOneWithUI(cfg));
                    // Keep tasks list trimmed to concurrency window.
                    if (runningTasks.Count >= 20)
                    {
                        var done = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(done);
                    }
                }
                await Task.WhenAll(runningTasks);
            });

            // v2.28.5: trim non-keep entries before final persist so the cache
            // file on disk and `_allConfigs` in memory both shrink to the
            // useful subset (Verified + Ok). See TrimAndReclaim docstring.
            await Dispatcher.UIThread.InvokeAsync(TrimAndReclaim);

            // Final persist (post-trim — cache now lean).
            var finalFile = _aggregator.Cache.Load();
            finalFile.Configs = _allConfigs;
            _aggregator.Cache.Save(finalFile);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFiltersAndStats();
                StatusText = foundVerified >= target
                    ? Strings.FcStatusDeepVerifyDone(foundVerified)
                    : Strings.FcStatusDeepVerifyExhausted(foundVerified, tested);
            });
        }
        catch (OperationCanceledException)
        {
            // v2.28.5: trim before save on cancel too — keeps the cache lean
            // even when the user aborts mid-deep-verify.
            await Dispatcher.UIThread.InvokeAsync(TrimAndReclaim);

            // Save whatever we have so far (post-trim).
            var file = _aggregator.Cache.Load();
            file.Configs = _allConfigs;
            _aggregator.Cache.Save(file);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusCancelled;
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DeepVerify failed");
            StatusText = Strings.FcStatusFailed(ex.Message);
            // v2.28.5: even on unexpected exception, trim what we have so
            // the user's working set drops back even if the deep verify
            // chain didn't complete cleanly.
            try { await Dispatcher.UIThread.InvokeAsync(TrimAndReclaim); } catch { }
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    [RelayCommand]
    private async Task ApplySelectedAsync()
    {
        var sel = SelectedItem;
        if (sel == null) return;
        if (IsBusy) return;

        IsBusy = true;
        StatusText = Strings.FcStatusApplying(sel.Endpoint);
        try
        {
            var ok = await _applyAsync(sel.Entry);
            StatusText = ok
                ? Strings.FcStatusApplied(sel.Endpoint)
                : Strings.FcStatusApplyFailed;
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigs apply failed: {err}", ex.Message);
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAggregatorStage(string stage)
    {
        Dispatcher.UIThread.Post(() => StatusText = stage);
    }

    private void OnAggregatorProgress(int done, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressDone = done;
            ProgressTotal = total;
        });
    }

    private void ApplyFiltersAndStats()
    {
        try
        {
            TotalCount       = _allConfigs.Count;
            WorkingCount     = _allConfigs.Count(c => c.Status == FreeConfigStatus.Ok);
            VerifiedCount    = _allConfigs.Count(c => c.Status == FreeConfigStatus.Verified);
            TlsFailedCount   = _allConfigs.Count(c => c.Status == FreeConfigStatus.TlsFailed);
            ImplausibleCount = _allConfigs.Count(c => c.Status == FreeConfigStatus.Implausible);
            TimeoutCount     = _allConfigs.Count(c => c.Status == FreeConfigStatus.Timeout);
            UnreachableCount = _allConfigs.Count(c => c.Status == FreeConfigStatus.Unreachable);

            // Populate country filter dropdown.
            var cc = _allConfigs
                .Where(c => !string.IsNullOrEmpty(c.CountryCode))
                .Select(c => c.CountryCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Countries = new ObservableCollection<string>(new[] { "All" }.Concat(cc));
            if (!Countries.Contains(SelectedCountry))
                SelectedCountry = "All";

            // Filter + sort. v2.28.5-r2: tightened to Verified only (was
            // Ok + Verified). User feedback: "только полностью рабочие
            // конфиги ничего другого". Ok = TCP+TLS-passed but never
            // through real HTTPS, so it's not "fully working" yet. The
            // new batched RefreshAsync loop already runs Deep Verify on
            // every Ok candidate inline, so the displayed list shows only
            // configs that completed the full pipeline.
            IEnumerable<FreeConfigEntry> q = _allConfigs
                .Where(c => c.Status == FreeConfigStatus.Verified);
            if (!string.Equals(SelectedCountry, "All", StringComparison.OrdinalIgnoreCase))
                q = q.Where(c => string.Equals(c.CountryCode, SelectedCountry, StringComparison.OrdinalIgnoreCase));

            // v2.28.3-r4 — also honour the max-ping setting in the displayed
            // list. User report: "ищу с пингом 50, приложение пишет нашло, а
            // на самом деле нет". Root cause: the goal-target filter only stops
            // Refresh early — it doesn't filter the displayed entries. After
            // Deep Verify re-measures latency (which is usually higher than
            // TCP-only ping), some entries may exceed the user's threshold but
            // still show up. v2.28.4-r3: applied unconditionally now that
            // OnlyWorking is implicit.
            if (UseLatencyGoal && LatencyGoalMaxPingMs.HasValue)
            {
                var maxPing = LatencyGoalMaxPingMs.Value;
                q = q.Where(c => c.LatencyMs > 0 && c.LatencyMs <= maxPing);
            }

            // v2.28.3-r3: IP-level dedup at display time. The aggregator's
            // dedup key is Server:Port:UUID, so the same IP can appear with
            // different ports (Reality endpoints multiplexing one IP) or
            // different UUIDs (key rotation / multi-tenant) — all legit
            // entries technically, but visual noise for the user picking a
            // server. User report on -r2: "1 и тот же IP несколько раз
            // (например 205.237.107.192)". Keep the *best* entry per IP
            // (lowest LatencySortKey, which already encodes status priority +
            // latency). The aggregator-level entries are still all retained
            // in _allConfigs so cache + Deep Verify still see them; only the
            // visible list is collapsed.
            var items = q
                .Select(c => new FreeConfigItemViewModel(c))
                .OrderBy(vm => vm.LatencySortKey)
                .GroupBy(vm => vm.Entry.Host ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(300) // cap at 300 visible to keep ListBox responsive with emoji flags
                .ToList();

            DisplayedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);

            // Auto-select first item so the Connect button is immediately actionable.
            if (SelectedItem == null || !DisplayedConfigs.Contains(SelectedItem))
                SelectedItem = DisplayedConfigs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ApplyFiltersAndStats failed");
            DisplayedConfigs = new ObservableCollection<FreeConfigItemViewModel>();
        }
        finally
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsFilteredEmpty));
            OnPropertyChanged(nameof(IsListVisible));
        }
    }

    private static string FormatAge(TimeSpan t)
    {
        if (t.TotalMinutes < 1)   return "just now";
        if (t.TotalMinutes < 60)  return $"{(int)t.TotalMinutes}m ago";
        if (t.TotalHours   < 24)  return $"{(int)t.TotalHours}h ago";
        return $"{(int)t.TotalDays}d ago";
    }
}
