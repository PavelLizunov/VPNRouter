using System.Collections.ObjectModel;
using System.Runtime;
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
    // Phase 4 Wave 19 (v3.0 refactor): settings-persistence seam for the
    // Add/RemoveUserSource commands. Defaults to
    // <see cref="VPNRouter.Core.Services.RealSettingsStore.Instance"/> for
    // back-compat with the pre-3G-1 static-loader path; tests can pass
    // <c>InMemorySettingsStore</c>.
    private readonly VPNRouter.Core.Services.ISettingsStore _settingsStore;

    private List<FreeConfigEntry> _allConfigs = new();

    /// <summary>
    /// v2.28.6 Phase 1: persistent all-time-verified set (the future
    /// "Сохранённые" tab source). Built from the on-disk cache at
    /// <see cref="EnsureCacheLoaded"/> time, then accumulates entries
    /// from each search session (deduped by <see cref="FreeConfigEntry.Id"/>).
    /// Persisted back to <c>free_configs.json</c> after a search ends.
    ///
    /// <para>Phase 1 keeps this list parallel to <see cref="_allConfigs"/>
    /// without changing the displayed UI; Phase 2 introduces a separate
    /// "Сохранённые" tab that surfaces it.</para>
    /// </summary>
    private List<FreeConfigEntry> _savedConfigs = new();

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
        Func<VPNRouter.Core.Models.AppSettings>? getSettings = null,
        VPNRouter.Core.Services.ISettingsStore? settingsStore = null)
    {
        _logger = logger;
        _applyAsync = applyAsync;
        _getSettings = getSettings;
        // Phase 4 Wave 19: default to the real store; tests can pass an
        // <c>InMemorySettingsStore</c> to keep AddUserSource / RemoveUserSource
        // isolated from <c>%ProgramData%\VPNRouter\config.yaml</c>.
        _settingsStore = settingsStore ?? VPNRouter.Core.Services.RealSettingsStore.Instance;
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
            //
            // v2.28.6 Phase 2: tabs split — Search list is now ephemeral
            // (cleared on app open / new search), Saved list persists from
            // cache with a 30-day retention filter. If there's any saved
            // history, default to the Saved tab so returning users see
            // their working configs immediately instead of an empty Search
            // tab.
            var now = DateTime.UtcNow;
            var kept = file.Configs
                .Where(c => FreeConfigKeepPolicy.ShouldRetainInSavedList(c, now))
                .ToList();
            _allConfigs = new List<FreeConfigEntry>();
            _savedConfigs = new List<FreeConfigEntry>(kept);
            ApplyFiltersAndStats();
            RebuildSavedDisplayList();
            NotifySavedTabBindings();
            if (_savedConfigs.Count > 0 && SelectedFreeTabIndex == 0)
                SelectedFreeTabIndex = 1;

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

    /// <summary>v2.28.6 Phase 2: source for the Сохранённые tab list.
    /// Rebuilt from <see cref="_savedConfigs"/> by
    /// <see cref="RebuildSavedDisplayList"/> on every modification.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSavedEmpty))]
    private ObservableCollection<FreeConfigItemViewModel> _displayedSavedConfigs = new();

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

    /// <summary>v2.28.6 Phase 1: which Free Configs sub-tab is selected.
    /// 0 = Поиск (default, search-tab as today). 1 = Сохранённые (Phase 2
    /// will surface the persistent saved list here). Phase 1 keeps this
    /// property scaffolded but unused by the XAML — UI tabs land in Phase 2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchTab))]
    [NotifyPropertyChangedFor(nameof(IsSavedTab))]
    private int _selectedFreeTabIndex;

    public bool IsSearchTab => SelectedFreeTabIndex == 0;
    public bool IsSavedTab  => SelectedFreeTabIndex == 1;

    /// <summary>v2.28.6 Phase 1: count for the Сохранённые-tab badge.</summary>
    public int SavedConfigsCount => _savedConfigs.Count;

    /// <summary>v2.28.6 Phase 2: localised "★ Сохранённые (N)" header
    /// (or just "★ Сохранённые" when count is 0).</summary>
    public string SavedTabHeaderText => SavedConfigsCount > 0
        ? Strings.FcTabSavedWithCount(SavedConfigsCount)
        : Strings.FcTabSaved;

    /// <summary>v2.28.6 Phase 2/3: count of saved entries that the user
    /// might want to bulk-recheck — older than 24 h since last verify, or
    /// failed-last-check.
    /// <para>v2.31.4-r1 (F-25 follow-up): also include Verified entries
    /// with <c>LatencyMs &lt;= 0</c>. Those are the ones healed by the
    /// v2.31.3 cache migration (<see cref="FreeConfigCache"/>) — their
    /// <c>LastTestedAt</c> may still be recent so the time-based check
    /// misses them, but the UI shows "— ✓✓" instead of a real ping and
    /// the user wants to re-probe to get a real number. Without this
    /// branch the "↻ Recheck" button hides immediately after a successful
    /// recheck even though the displayed Saved tab is full of unverified
    /// rows.</para>
    /// </summary>
    public int StaleSavedCount
    {
        get
        {
            var now = DateTime.UtcNow;
            return _savedConfigs.Count(c =>
                (c.LastVerifyFailedAt.HasValue &&
                    (!c.LastTestedAt.HasValue ||
                        c.LastVerifyFailedAt.Value >= c.LastTestedAt.Value)) ||
                (c.LastTestedAt.HasValue &&
                    (now - c.LastTestedAt.Value).TotalHours > 24) ||
                (c.Status == VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified
                    && c.LatencyMs <= 0));
        }
    }

    /// <summary>v2.28.6 Phase 2: localised "↻ Recheck (N)" button label.
    /// When N=0 the bulk button is hidden via <see cref="HasStaleSaved"/>.</summary>
    public string SavedRecheckStaleButtonText => Strings.FcSavedRecheckStaleBtn(StaleSavedCount);

    public bool HasStaleSaved => StaleSavedCount > 0;

    public bool IsSavedEmpty => DisplayedSavedConfigs.Count == 0;

    [ObservableProperty] private string _selectedCountry = "All";
    partial void OnSelectedCountryChanged(string value) => ApplyFiltersAndStats();

    // v2.40.0 (review L3): re-filter the displayed Search list the moment the user
    // toggles ExcludeRu, mirroring OnSelectedCountryChanged. Without this, RU rows
    // surfaced by a search run before the user opted in stayed visible + selectable
    // until the next search / country change.
    partial void OnExcludeRuChanged(bool value) => ApplyFiltersAndStats();

    /// <summary>How many Verified configs to hunt for in a deep-verify session.
    /// v2.30.7-r2 (VM-6 audit fix): switched from int to int? to be NumericUpDown-safe
    /// per CLAUDE.md "NumericUpDown bind to int" gotcha. The field isn't currently
    /// bound in XAML but the planned design (free-configs-v2.14-roadmap.md) shows
    /// a NumericUpDown — defensive rewrite ahead of that.</summary>
    [ObservableProperty] private int? _deepVerifyTargetCount = 5;

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

    /// <summary>Custom preset: max acceptable ping in ms.
    /// v2.30.7-r2 (VM-6 audit fix): nullable for NumericUpDown safety.</summary>
    [ObservableProperty] private int? _customMaxPingMs = 200;
    /// <summary>Custom preset: min acceptable download throughput in Mbps.
    /// v2.30.7-r2 (VM-7 audit fix): nullable for NumericUpDown safety.</summary>
    [ObservableProperty] private int? _customMinBandwidthMbps = 5;

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
        4 => (CustomMaxPingMs ?? 200, CustomMinBandwidthMbps ?? 5), // Custom (?? fallback for nullable)
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
        // v2.40.0 (contracts G5 #7): clamp to the documented bounds — target
        // [1,50] (>50 deep-verifies ~forever), user max-ping [50,2000] ms.
        // Matches the Android click-handler clamp; the "no cap" sentinel branch
        // (UseLatencyGoal off) is intentionally left uncapped.
        var target = Math.Clamp(LatencyGoalTarget ?? 10, 1, 50);
        var maxPing = (UseLatencyGoal && LatencyGoalMaxPingMs.HasValue)
            ? Math.Clamp(LatencyGoalMaxPingMs.Value, 50, 2000)
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
            // v2.39.0 (audit #7): apply the RU-exclusion to BOTH queue halves.
            // Previously only the "fresh" half was filtered, so a cached RU
            // Verified row was prepended and bypassed the user's ExcludeRu opt-in
            // (an ordinary repeated-search path, not a rare edge case).
            bool CountryAllowed(FreeConfigEntry c) =>
                !ExcludeRu || !string.Equals(
                    c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase);

            var cachedVerified = pool
                .Where(c => c.Status == FreeConfigStatus.Verified)
                .Where(CountryAllowed)
                .ToList();
            var cachedVerifiedIds = new HashSet<string>(
                cachedVerified.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            // Build the ordered queue: cached Verified first, then everything else.
            var queue = cachedVerified
                .Concat(pool.Where(c =>
                    !cachedVerifiedIds.Contains(c.Id) && CountryAllowed(c)))
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

            // ── Stage 2: cross-batch overlapping pipeline (v2.29.0-r3) ──
            // Pre-r3 each batch ran TCP+TLS → deep-verify sequentially, with
            // batch N+1 starting only after batch N finished. With cross-
            // batch overlap, batch N+1's TCP+TLS runs in parallel with batch
            // N's deep-verify (TCP is network-IO-light; deep-verify spawns
            // sing-box). Hides ~10s of TCP wall-clock per batch behind the
            // already-running deep-verify. Mac tester request 2026-04-29:
            // "вот это можно делать ассинхронно — не последовательно
            // отправлять по 1 запросу а хуярить сразу пачку запросов".
            //
            // v2.29.0-r3 (5c): adaptive deep-verify concurrency cap.
            // Pre-r3: hardcoded 5 sing-box spawns. On 8+core machines that
            // leaves ~60% CPU idle during deep-verify. Now scales with
            // Environment.ProcessorCount: 1-3 cores=3, 4-7=5, 8+=8.
            var deepCap = ComputeAdaptiveDeepCap();
            _logger.Information("[FreeConfigs] adaptive deep-verify cap = {cap} (CPU cores: {cpu})",
                deepCap, Environment.ProcessorCount);
            var deepSem = new SemaphoreSlim(deepCap);
            var batchSize = FreeConfigAggregator.DefaultBatchSize;
            var processedCount = 0;
            var totalBatches = (queue.Count + batchSize - 1) / batchSize;

            // In-flight deep-verify tracking, ACROSS batches. Each entry is
            // the task that completes when batch K's deep-verify wave is
            // fully drained (all sub-tasks drained or cancellation observed).
            var inFlightBatches = new List<Task>();
            // Cap on how many batches are simultaneously in any phase
            // (TCP+TLS or deep-verify). 2 is enough to hide TCP wall-clock
            // behind deep-verify; 3+ would put more sing-box pressure
            // without much extra wall-clock saving (deep-verify dominates).
            const int MaxBatchesInFlight = 2;

            // Pre-test result of the next batch's TCP+TLS, executed in
            // parallel with the previous batch's deep-verify. Nullable —
            // first iteration has no prefetch; following iterations use
            // it instead of running TCP synchronously.
            Task<List<FreeConfigEntry>>? prefetchedTcp = null;

            for (int i = 0; i < queue.Count; i += batchSize)
            {
                if (ct.IsCancellationRequested || verifiedList.Count >= target) break;

                var currentBatchNum = (i / batchSize) + 1;

                // Acquire batch — either from prefetch or run TCP synchronously.
                List<FreeConfigEntry> batch;
                if (prefetchedTcp != null)
                {
                    try { batch = await prefetchedTcp; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[FreeConfigs] Prefetched TCP+TLS threw — skipping batch {n}", currentBatchNum);
                        prefetchedTcp = null;
                        continue;
                    }
                    prefetchedTcp = null;
                }
                else
                {
                    var slice = queue.Skip(i).Take(batchSize).ToList();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StatusText = Strings.FcStatusBatchedTcpTls(
                            verifiedList.Count, target, currentBatchNum, totalBatches);
                    });
                    try { batch = await RunTcpTlsBatchAsync(slice, currentBatchNum, totalBatches, target, verifiedList, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[FreeConfigs] Batch TCP+TLS test threw — skipping");
                        continue;
                    }
                }

                if (ct.IsCancellationRequested) break;

                // Kick off the NEXT batch's TCP+TLS now (cross-batch overlap).
                // Don't await; deep-verify of THIS batch will run in parallel.
                var nextStart = i + batchSize;
                if (nextStart < queue.Count && verifiedList.Count < target)
                {
                    var nextSlice = queue.Skip(nextStart).Take(batchSize).ToList();
                    var nextBatchNum = (nextStart / batchSize) + 1;
                    prefetchedTcp = Task.Run(async () =>
                    {
                        try
                        {
                            return await RunTcpTlsBatchAsync(
                                nextSlice, nextBatchNum, totalBatches,
                                target, verifiedList, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        // Logged inside RunTcpTlsBatchAsync; re-throw bubbles to await above.
                    }, ct);
                }

                // Deep-verify Ok subset of THIS batch.
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

                // Wrap the entire batch's deep-verify wave in a single Task
                // so we can track "batch N is still in flight" at the outer
                // level. Inner tasks share `deepSem` across batches — the
                // adaptive cap is GLOBAL, not per-batch.
                var batchVerifyTask = DeepVerifyBatchAsync(
                    okSubset, verifiedList, deepSem, target, maxPing, ct);
                inFlightBatches.Add(batchVerifyTask);
                processedCount += batch.Count;

                // Drop refs ASAP so GC can reclaim. The batchVerifyTask owns
                // its own copy of okSubset; original list refs are dropped here.
                batch = null!;
                okSubset = null!;

                // Cap in-flight batches. Wait for one to finish before
                // queueing more. With MaxBatchesInFlight=2 we have at most
                // 2 batches' worth of deep-verify tasks queued (each gated
                // by deepSem so total sing-box concurrency = deepCap, NOT
                // 2 * deepCap).
                if (inFlightBatches.Count >= MaxBatchesInFlight)
                {
                    var finished = await Task.WhenAny(inFlightBatches);
                    inFlightBatches.Remove(finished);
                    // If it threw inside, the await on Task.WhenAny doesn't
                    // observe — observe explicitly so we don't lose errors.
                    try { await finished; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[FreeConfigs] Batch deep-verify wave threw");
                    }
                }
            }

            // Drain any remaining in-flight batch waves.
            if (inFlightBatches.Count > 0)
            {
                try { await Task.WhenAll(inFlightBatches); }
                catch (OperationCanceledException) { throw; }
                catch { /* per-task warnings already logged */ }
            }
            inFlightBatches.Clear();

            // Cancel and observe any prefetch we left in flight (best-effort).
            if (prefetchedTcp != null)
            {
                try { await prefetchedTcp; }
                catch { /* ignored on cancel/error */ }
                prefetchedTcp = null;
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

            // v2.28.6 Phase 1: persist the all-time saved set, not just
            // this session's results. _savedConfigs already absorbed every
            // newly-verified entry via UpsertSavedConfig during the loop,
            // so cache file now holds the cumulative history (capped at
            // SavedConfigsRetentionDays at next load).
            try
            {
                var file = _aggregator.Cache.Load();
                file.Configs = _savedConfigs;
                file.LastAggregatedAt = DateTime.UtcNow;
                _aggregator.Cache.Save(file);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[FreeConfigs] Cache save failed (non-fatal)");
            }

            // Drop the local pool / queue references explicitly so the GC
            // sees them eligible immediately, then run the v2.28.5-r5
            // post-search reclaim sequence.
            queue = null!;
            pool = null!;
            cachedVerified = null!;
            cachedVerifiedIds = null!;

            ReclaimPostSearchMemory();
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = new List<FreeConfigEntry>(verifiedList);
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusCancelled;
            });
            // Save the saved-set (carries any partial verifies absorbed
            // before cancellation; see Phase 1 note in success path).
            try
            {
                var file = _aggregator.Cache.Load();
                file.Configs = _savedConfigs;
                file.LastAggregatedAt = DateTime.UtcNow;
                _aggregator.Cache.Save(file);
            }
            catch { }
            ReclaimPostSearchMemory();
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

    /// <summary>v2.29.0-r3: adaptive deep-verify concurrency cap based on
    /// CPU count. Pre-r3 was hardcoded 5 (matching the original sing-box
    /// spawn cost on a quad-core dev box). On 8+ core machines that
    /// leaves ~60% CPU idle during deep-verify; on 1-2 core VMs
    /// 5 simultaneous sing-box can starve the OS scheduler.
    ///
    /// <list type="bullet">
    /// <item>1-3 cores: cap = 3 (conservative — sing-box spawn + Reality
    /// TLS handshake is CPU-heavy).</item>
    /// <item>4-7 cores: cap = 5 (pre-r3 default — proven safe).</item>
    /// <item>8+ cores: cap = 8 (room to scale on modern desktops).</item>
    /// </list>
    ///
    /// <para>Each sing-box instance uses ~50 MB RSS + 2 ports (SOCKS local
    /// + Clash API) + outgoing TLS connections. cap=8 ⇒ ~400 MB peak
    /// memory + ~30+ ephemeral sockets per instance — well within the
    /// 16 k ephemeral-port pool on Windows / Linux / Mac.</para>
    /// </summary>
    private static int ComputeAdaptiveDeepCap()
    {
        var cpu = Environment.ProcessorCount;
        if (cpu <= 3) return 3;
        if (cpu <= 7) return 5;
        return 8;
    }

    /// <summary>v2.29.0-r3: extracted from inline in the batched RefreshAsync
    /// loop so it can be called synchronously OR via Task.Run (cross-batch
    /// prefetch). Performs TCP+TLS test of the slice with throttled UI
    /// status updates (~5/sec).</summary>
    private async Task<List<FreeConfigEntry>> RunTcpTlsBatchAsync(
        List<FreeConfigEntry> slice,
        int batchNum,
        int totalBatches,
        int target,
        List<FreeConfigEntry> verifiedList,
        CancellationToken ct)
    {
        var lastStatusUpdate = DateTime.MinValue;
        var batchProgress = new Progress<(int done, int total)>(p =>
        {
            // Throttle UI updates to ~5/sec so we don't spam the dispatcher
            // when 80 parallel TCP probes finish at once.
            var now = DateTime.UtcNow;
            if ((now - lastStatusUpdate).TotalMilliseconds < 200) return;
            lastStatusUpdate = now;
            int found;
            lock (verifiedList) found = verifiedList.Count;
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = Strings.FcStatusBatchedTcpTlsProgress(
                    found, target, batchNum, totalBatches, p.done, p.total);
            });
        });

        await _aggregator.Tester.TestAllAsync(slice, batchProgress, ct);
        return slice;
    }

    /// <summary>v2.29.0-r3: extracted from inline in the batched RefreshAsync
    /// loop so cross-batch overlap can wrap each batch's deep-verify wave
    /// in a single Task. Internal cap-of-deepCap (semaphore-shared with
    /// other in-flight batches) on simultaneous sing-box spawns.</summary>
    private async Task DeepVerifyBatchAsync(
        List<FreeConfigEntry> okSubset,
        List<FreeConfigEntry> verifiedList,
        SemaphoreSlim deepSem,
        int target,
        int maxPing,
        CancellationToken ct)
    {
        var deepTasks = new List<Task>();
        // In-flight cap matches the semaphore cap. The semaphore is the
        // hard ceiling on simultaneous sing-box spawns (shared across all
        // in-flight batches when cross-batch overlap is on); inFlightCap
        // here is the soft cap on TASK objects we have queued at the
        // batch level, to keep deepTasks from growing unbounded for
        // huge ok-subsets.
        var inFlightCap = ComputeAdaptiveDeepCap();
        foreach (var cfg in okSubset)
        {
            if (ct.IsCancellationRequested) break;
            int found;
            lock (verifiedList) found = verifiedList.Count;
            if (found >= target) break;

            deepTasks.Add(VerifyOneAndAppendAsync(
                cfg, verifiedList, deepSem, target, maxPing, ct));

            if (deepTasks.Count >= inFlightCap)
            {
                var done = await Task.WhenAny(deepTasks);
                deepTasks.Remove(done);
            }
        }
        if (deepTasks.Count > 0)
        {
            try { await Task.WhenAll(deepTasks); }
            catch (OperationCanceledException) { throw; }
            catch { /* per-verify failures are non-fatal at the batch level */ }
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
            // v2.28.5-r6: per-probe status update so the UI doesn't appear
            // frozen during the deep-verify phase. Each probe takes 3-5 s,
            // 5 run in parallel, so this fires every 600-1000 ms — enough
            // visible motion that ADHD-leaning / TikTok-pace users don't
            // mistake the wait for a hang.
            var probedHost = cfg.Host;
            var probedPort = cfg.Port;
            var probedCc = string.IsNullOrEmpty(cfg.CountryCode) ? "??" : cfg.CountryCode;
            var startedFound = 0;
            lock (verifiedList) startedFound = verifiedList.Count;

            // Fire-and-forget UI update; don't await so probe can start
            // immediately. Dispatcher batches these 5 parallel pokes and
            // only the latest one wins as visible status — that's exactly
            // what we want.
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = Strings.FcStatusBatchedProbing(
                    startedFound, target, probedHost, probedPort, probedCc);
            });

            // v2.29.0 Phase 3C: skip Deep Verify if this entry was verified
            // within the last 6 hours AND we already have a TCP ping number
            // for it. Saves 5-15 s per already-known-working config on the
            // cached re-test pass. The skip preserves Status=Verified +
            // LatencyMs + MeasuredBandwidthMbps as-is; downstream Append
            // logic still gates on Status==Verified + LatencyMs<=maxPing,
            // so behaviour is identical to a fresh successful verify.
            //
            // Why 6h: Verified entries have already passed real HTTP round-
            // trip + TLS handshake. The most likely failure mode in a 6h
            // window is server going down (caught by next-day refresh) or
            // SNI/cert rotation (rare for stable VLESS+Reality endpoints).
            // 6h trades a small staleness risk for noticeable UX speedup.
            var skipDeep = cfg.Status == FreeConfigStatus.Verified
                && cfg.LastDeepVerifyAt.HasValue
                && (DateTime.UtcNow - cfg.LastDeepVerifyAt.Value) < TimeSpan.FromHours(6)
                && cfg.LatencyMs > 0;

            if (!skipDeep)
            {
                await _deepVerifier.VerifyOneAsync(cfg, ct);
            }

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
                        // v2.28.6: also merge the freshly-verified entry
                        // into the persistent saved list (Id-deduped) and
                        // rebuild the Saved-tab list so the badge counter
                        // and the Saved view stay live during the search.
                        UpsertSavedConfig(cfg);
                        ApplyFiltersAndStats();
                        RebuildSavedDisplayList();
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
    /// v2.28.6 Phase 1: insert <paramref name="entry"/> into
    /// <see cref="_savedConfigs"/> if it's not already there (by
    /// <see cref="FreeConfigEntry.Id"/>); otherwise replace the existing
    /// row so the saved list reflects the freshest test results
    /// (LatencyMs, MeasuredBandwidthMbps, LastTestedAt). Notifies
    /// <see cref="SavedConfigsCount"/> for the future tab badge.
    ///
    /// <para>Called from the search flow on every newly-Verified entry.
    /// Phase 2's per-row Recheck will reuse this same helper.</para>
    /// </summary>
    private void UpsertSavedConfig(FreeConfigEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Id)) return;
        for (int i = 0; i < _savedConfigs.Count; i++)
        {
            if (string.Equals(_savedConfigs[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                _savedConfigs[i] = entry;
                NotifySavedTabBindings();
                return;
            }
        }
        _savedConfigs.Add(entry);
        NotifySavedTabBindings();
    }

    /// <summary>v2.28.6 Phase 2: rebuild <see cref="DisplayedSavedConfigs"/>
    /// from <see cref="_savedConfigs"/>. Sort: fresh < ageing < stale <
    /// failed; secondary by latency. Wraps each entry in a fresh
    /// <see cref="FreeConfigItemViewModel"/> so the freshness label / opacity
    /// reflect <c>DateTime.UtcNow</c> at build time.</summary>
    private void RebuildSavedDisplayList()
    {
        try
        {
            var items = _savedConfigs
                .Select(c => new FreeConfigItemViewModel(c))
                .OrderBy(vm => vm.FreshnessSortKey)
                .ToList();
            DisplayedSavedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] RebuildSavedDisplayList failed");
            DisplayedSavedConfigs = new ObservableCollection<FreeConfigItemViewModel>();
        }
    }

    /// <summary>v2.28.6 Phase 2: fire all derived saved-tab bindings.
    /// Called after every <see cref="_savedConfigs"/> mutation so the tab
    /// header badge / "Recheck (N)" label / IsSavedEmpty all stay coherent.</summary>
    private void NotifySavedTabBindings()
    {
        OnPropertyChanged(nameof(SavedConfigsCount));
        OnPropertyChanged(nameof(SavedTabHeaderText));
        OnPropertyChanged(nameof(StaleSavedCount));
        OnPropertyChanged(nameof(SavedRecheckStaleButtonText));
        OnPropertyChanged(nameof(HasStaleSaved));
        OnPropertyChanged(nameof(IsSavedEmpty));
    }

    /// <summary>v2.28.6 Phase 2: persist <see cref="_savedConfigs"/> to
    /// <c>free_configs.json</c>. Wraps the cache write in a try/catch
    /// because cache I/O failures shouldn't break the in-memory list.</summary>
    private void SaveSavedConfigsToCache()
    {
        try
        {
            var file = _aggregator.Cache.Load();
            file.Configs = _savedConfigs;
            file.LastAggregatedAt = DateTime.UtcNow;
            _aggregator.Cache.Save(file);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] SaveSavedConfigsToCache failed (non-fatal)");
        }
    }

    /// <summary>v2.28.6 Phase 3: re-verify a single saved entry. Mirrors the
    /// search-flow VerifyOneAndAppendAsync, but operates on an already-saved
    /// entry and preserves last-good Latency/Bandwidth on failure (the user
    /// can still see "this used to work at 15 ms / 50 Mbps" with a "failed"
    /// badge on top).</summary>
    [RelayCommand]
    private async Task RecheckOneAsync(FreeConfigItemViewModel? item)
    {
        if (item == null || IsBusy) return;

        // Snapshot last-good values before the verifier mutates them.
        var entry = item.Entry;
        var prior = FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        item.IsRecheckRunning = true;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = Strings.FcStatusRecheckOne(entry.Host, entry.Port,
                    string.IsNullOrEmpty(entry.CountryCode) ? "??" : entry.CountryCode);
            });

            _deepVerifier.MeasureBandwidth = true;
            try
            {
                // v2.28.6-r5: refresh raw TCP ping FIRST so cfg.LatencyMs
                // reflects current network RTT to the proxy server (not the
                // 5-7-RTT-inflated HTTP roundtrip the deep verifier writes).
                // Quick: TCP-only, ~500 ms - 1.5 s typical.
                await _aggregator.Tester.TcpPingOnlyAsync(entry, ct);
                await _deepVerifier.VerifyOneAsync(entry, ct);
                // v2.40.0 (review M4): VerifyOneAsync SWALLOWS the user-cancel OCE
                // internally (its catch has no `when` filter + doesn't rethrow), so
                // a cancel during the multi-second deep-verify would otherwise fall
                // through to MergeRecheckResult and — because no fresh
                // LastDeepVerifyAt was stamped — be recorded as a spurious
                // "failed last check". Re-detect the cancel here so the catch below
                // runs RestorePriorState instead (cancel != failure).
                ct.ThrowIfCancellationRequested();
                FreeConfigFreshness.MergeRecheckResult(entry, prior, DateTime.UtcNow);
                _logger.Information("[Recheck] {host}:{port} → {result} ({ping} ms)",
                    entry.Host, entry.Port,
                    entry.LastVerifyFailedAt.HasValue ? "failed; last-good preserved" : "Verified",
                    entry.LatencyMs);
            }
            catch (OperationCanceledException)
            {
                // v2.28.6-r2 cancel safety: restore the entry to prior
                // state. Without this, a cancelled recheck would leave
                // Status = TlsFailed (or whatever the verifier mutated to)
                // and the retention filter would drop the entry on next
                // cache load. Don't set LastVerifyFailedAt — cancel isn't
                // a failure event.
                FreeConfigFreshness.RestorePriorState(entry, prior);
                throw;
            }

            UpsertSavedConfig(entry);
            SaveSavedConfigsToCache();
            RebuildSavedDisplayList();
            NotifySavedTabBindings();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = entry.LastVerifyFailedAt.HasValue
                    ? Strings.FcStatusRecheckAllDone(0, 1)
                    : Strings.FcStatusRecheckAllDone(1, 0);
            });
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] RecheckOne failed for {host}:{port}",
                entry.Host, entry.Port);
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            item.IsRecheckRunning = false;
            IsBusy = false;
        }
    }

    /// <summary>v2.28.6 Phase 3: re-verify all saved entries that are stale
    /// (older than 24 h, or failed-last-check). 5-permit semaphore on
    /// sing-box spawns matches the search-flow concurrency. Cancellable.
    /// <para>v2.31.4-r1: also re-verify Verified entries with LatencyMs&lt;=0
    /// (post-migration "needs re-verify" state) — keep the predicate in
    /// sync with <see cref="StaleSavedCount"/> so the button label and the
    /// command's actual work agree.</para>
    /// </summary>
    [RelayCommand]
    private async Task RecheckAllStaleAsync()
    {
        if (IsBusy) return;
        var stale = _savedConfigs
            .Where(c =>
                (c.LastVerifyFailedAt.HasValue &&
                    (!c.LastTestedAt.HasValue ||
                        c.LastVerifyFailedAt.Value >= c.LastTestedAt.Value)) ||
                (c.LastTestedAt.HasValue &&
                    (DateTime.UtcNow - c.LastTestedAt.Value).TotalHours > 24) ||
                (c.Status == VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified
                    && c.LatencyMs <= 0))
            .ToList();
        if (stale.Count == 0) return;

        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        var verified = 0;
        var failed = 0;

        try
        {
            _deepVerifier.MeasureBandwidth = true;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = Strings.FcStatusRecheckAllStart(stale.Count);
                ProgressTotal = stale.Count;
                ProgressDone = 0;
            });

            var sem = new SemaphoreSlim(5);
            var done = 0;
            var tasks = stale.Select(async cfg =>
            {
                await sem.WaitAsync(ct);
                var prior = FreeConfigFreshness.RecheckSnapshot.Capture(cfg);
                try
                {
                    // v2.28.6-r5: see RecheckOneAsync — refresh TCP ping
                    // so LatencyMs is raw network RTT, not HTTP RTT.
                    await _aggregator.Tester.TcpPingOnlyAsync(cfg, ct);
                    await _deepVerifier.VerifyOneAsync(cfg, ct);
                    // v2.40.0 (review M4): re-detect a cancel swallowed inside
                    // VerifyOneAsync so the catch runs RestorePriorState instead of
                    // recording a spurious "failed last check" via the merge.
                    ct.ThrowIfCancellationRequested();
                    FreeConfigFreshness.MergeRecheckResult(cfg, prior, DateTime.UtcNow);
                    if (cfg.LastVerifyFailedAt.HasValue) Interlocked.Increment(ref failed);
                    else Interlocked.Increment(ref verified);

                    var d = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText = Strings.FcStatusRecheckAllProgress(d, stale.Count);
                        ProgressDone = d;
                    });
                }
                catch (OperationCanceledException)
                {
                    // v2.28.6-r2 cancel safety — see RecheckOneAsync above.
                    FreeConfigFreshness.RestorePriorState(cfg, prior);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[Recheck-bulk] failed for {host}:{port}",
                        cfg.Host, cfg.Port);
                }
                finally
                {
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            SaveSavedConfigsToCache();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RebuildSavedDisplayList();
                NotifySavedTabBindings();
                StatusText = Strings.FcStatusRecheckAllDone(verified, failed);
            });
        }
        catch (OperationCanceledException)
        {
            SaveSavedConfigsToCache();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RebuildSavedDisplayList();
                NotifySavedTabBindings();
                StatusText = Strings.FcStatusCancelled;
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] RecheckAllStale failed");
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    /// <summary>v2.28.6 Phase 3: drop a single entry from the persistent
    /// saved list. No confirmation — the entry is re-discoverable on the
    /// next search if the upstream pool still has it.</summary>
    [RelayCommand]
    private void RemoveFromSaved(FreeConfigItemViewModel? item)
    {
        if (item == null) return;
        var id = item.Entry.Id;
        if (string.IsNullOrEmpty(id)) return;

        _savedConfigs.RemoveAll(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        // Drop selection too if the user removed the row they had selected.
        if (SelectedItem == item) SelectedItem = null;

        SaveSavedConfigsToCache();
        RebuildSavedDisplayList();
        NotifySavedTabBindings();
    }

    /// <summary>v2.28.6 Phase 3: wipe the entire saved list. No
    /// confirmation per the plan — saved entries are re-discoverable via
    /// next search.</summary>
    [RelayCommand]
    private void ClearAllSaved()
    {
        if (_savedConfigs.Count == 0) return;
        _savedConfigs.Clear();
        SelectedItem = null;
        SaveSavedConfigsToCache();
        RebuildSavedDisplayList();
        NotifySavedTabBindings();
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

            ReclaimPostSearchMemory();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] TrimAndReclaim threw — skipping");
        }
    }

    /// <summary>
    /// v2.28.5-r5: aggressive post-search reclaim. Called after the batched
    /// flow finishes (success / cancel / exception) and from
    /// <see cref="TrimAndReclaim"/> in the legacy path.
    ///
    /// <para>Three steps, in order:</para>
    /// <list type="number">
    /// <item><b>Schedule LOH compaction</b> (`GCSettings.LargeObjectHeapCompactionMode`
    ///   = `CompactOnce`). Without this, large objects (e.g. the multi-MB
    ///   `pool.json` byte buffer the fetcher allocated) live in the LOH
    ///   indefinitely; gen-2 GC sweeps them but doesn't compact, so
    ///   working set stays elevated even after the references are dead.</item>
    /// <item><b>Force gen-2 GC</b> (blocking, compacting). Releases the
    ///   freshly-marked-dead pool entries + the LOH buffers in one pass.
    ///   Blocking is fine here — search just ended, user is looking at
    ///   results, sub-second hitch is acceptable for a working-set drop.</item>
    /// <item><b>Skia.PurgeAllCaches</b> — drops native font atlases and
    ///   GPU texture caches. User report on memory-research plan: a single
    ///   `PurgeAllCaches` call dropped ~40 MB working set in a long-lived
    ///   Avalonia app. The 60-s `RuntimeStatus` purge is amortised; this
    ///   one happens at the exact moment the user expects "memory should
    ///   drop now".</item>
    /// </list>
    /// </summary>
    private static void ReclaimPostSearchMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        }
        catch { /* not supported on all runtimes; non-fatal */ }

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // Second pass — sweep finalized objects this round picked up.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { /* GC.Collect can be no-op if disallowed; non-fatal */ }

        try
        {
            SkiaSharp.SKGraphics.PurgeAllCaches();
        }
        catch { /* native-side failures are not fatal */ }
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

        // Persist via the injected store (Phase 4 Wave 19). Default
        // RealSettingsStore.Instance routes to SettingsLoader.Save, preserving
        // the pre-3G-1 behaviour. The settings accessor still points to
        // MainWindowViewModel._settings — only the persistence boundary changed.
        _settingsStore.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);

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

        // Phase 4 Wave 19: persist via injected store (see AddUserSourceAsync above).
        _settingsStore.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);
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
            // v2.40.0-r3 (audit P0 handle-leak sweep): ProcessQuery disposes the Process[].
            return VPNRouter.Core.Services.ProcessQuery.AnyAlive("sing-box");
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

            var target = Math.Max(1, DeepVerifyTargetCount ?? 5);
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

        // v2.40.0 (contracts B4 #4): connectable ⇔ Status==Verified. A public
        // config that only passed the weaker TCP/TLS gate (or whose last check
        // failed) is not connectable until deep verify confirms it. Mirrors the
        // Android ApplyFcConnectGate. The Search list is Verified-filtered and
        // Saved retains only Verified, so this is normally unreachable — it's
        // the explicit guard layer (UI → VM → Core) the framework requires.
        if (sel.Entry.Status != FreeConfigStatus.Verified)
        {
            StatusText = Strings.FcConnectNeedsVerify;
            return;
        }

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

            // v2.39.0 (audit #7): re-apply the RU exclusion at the display
            // boundary as a safety net. A cached/stale RU Verified row already
            // sitting in _allConfigs (e.g. from a search before the user opted
            // in) must not appear in the SEARCH list when ExcludeRu is on.
            // NOTE (review L6): this scopes the SEARCH tab only — the Saved tab is
            // a user-curated list and is intentionally NOT RU-filtered, so a saved
            // RU row stays connectable. The queue builder already drops RU from
            // processing; this is belt-and-suspenders at the Search visible boundary.
            if (ExcludeRu)
                q = q.Where(c => !string.Equals(c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase));

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
                // v2.40.0 (review L4): clamp to the same [50,2000] bound the search
                // gate uses, so the displayed list and the search honour one
                // threshold (no "found N but list shows fewer" divergence).
                var maxPing = Math.Clamp(LatencyGoalMaxPingMs.Value, 50, 2000);
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

    // v2.30.4-r1 (UX-61 fix): localize the age string instead of hardcoding
    // English. Pre-r1 the Free Configs subtitle said "Обновлено 1d ago" —
    // mixing RU "Обновлено" with EN "1d ago". Now uses the same Lang
    // signal as Strings to pick the matching locale.
    private static string FormatAge(TimeSpan t)
    {
        var ru = string.Equals(VPNRouter.App.Localization.Strings.Lang, "ru", StringComparison.OrdinalIgnoreCase);
        if (t.TotalMinutes < 1)   return ru ? "только что" : "just now";
        if (t.TotalMinutes < 60)  return ru ? $"{(int)t.TotalMinutes} мин назад" : $"{(int)t.TotalMinutes}m ago";
        if (t.TotalHours   < 24)  return ru ? $"{(int)t.TotalHours} ч назад" : $"{(int)t.TotalHours}h ago";
        return ru ? $"{(int)t.TotalDays} дн назад" : $"{(int)t.TotalDays}d ago";
    }
}
