using System.Collections.ObjectModel;
using System.Runtime;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

public partial class FreeConfigsPageViewModel : ObservableObject, IDisposable
{
    private readonly FreeConfigAggregator _aggregator;
    private readonly FreeConfigDeepVerifier _deepVerifier;
    private readonly ILogger _logger;
    private readonly Func<FreeConfigEntry, Task<bool>> _applyAsync;
    private readonly Func<VPNRouter.Core.Models.AppSettings>? _getSettings;
    private readonly VPNRouter.Core.Services.ISettingsStore _settingsStore;
    private readonly FreeConfigCache _savedCache;
    private HashSet<string>? _lastCountryCodes;

    private List<FreeConfigEntry> _allConfigs = new();

    private List<FreeConfigEntry> _savedConfigs = new();

    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

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
        _settingsStore = settingsStore ?? VPNRouter.Core.Services.RealSettingsStore.Instance;
        _savedCache = new FreeConfigCache(logger, Path.Combine(VPNRouter.Core.AppPaths.DataDir, "free_configs_saved.json"));
        _aggregator = new FreeConfigAggregator(logger);
        _aggregator.OnStageChanged += OnAggregatorStage;
        _aggregator.OnTestProgress  += OnAggregatorProgress;
        _deepVerifier = new FreeConfigDeepVerifier(logger);
        ReloadUserSources();

        StatusText = Strings.FcStatusEmpty;
    }

    public void EnsureCacheLoaded()
    {
        if (_cacheLoaded || _disposed) return;
        _cacheLoaded = true;

        try
        {
            var now = DateTime.UtcNow;
            var savedFile = _savedCache.Load();
            var kept = savedFile.Configs
                .Where(c => FreeConfigKeepPolicy.ShouldRetainInSavedList(c, now))
                .ToList();

            if (kept.Count == 0 && !File.Exists(_savedCache.FilePath))
            {
                var legacyFile = _aggregator.Cache.Load();
                kept = legacyFile.Configs
                    .Where(c => FreeConfigKeepPolicy.ShouldRetainInSavedList(c, now))
                    .ToList();
                if (kept.Count > 0)
                {
                    _savedConfigs = new List<FreeConfigEntry>(kept);
                    SaveSavedConfigsToCache();
                }
            }

            _allConfigs = new List<FreeConfigEntry>();
            _savedConfigs = new List<FreeConfigEntry>(kept);
            ApplyFiltersAndStats();
            RebuildSavedDisplayList();
            NotifySavedTabBindings();
            if (_savedConfigs.Count > 0 && SelectedFreeTabIndex == 0)
                SelectedFreeTabIndex = 1;

            var poolFile = _aggregator.Cache.Load();
            if (poolFile.LastAggregatedAt == DateTime.MinValue)
            {
                StatusText = Strings.FcStatusEmpty;
            }
            else
            {
                var age = DateTime.UtcNow - poolFile.LastAggregatedAt;
                StatusText = Strings.FcStatusCacheAge(FormatAge(age));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] EnsureCacheLoaded failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _aggregator.OnStageChanged -= OnAggregatorStage;
            _aggregator.OnTestProgress  -= OnAggregatorProgress;
        }
        catch {  }

        try { _refreshCts?.Cancel(); _refreshCts?.Dispose(); }
        catch { }
    }

    [ObservableProperty] private ObservableCollection<FreeConfigItemViewModel> _displayedConfigs = new();

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchTab))]
    [NotifyPropertyChangedFor(nameof(IsSavedTab))]
    private int _selectedFreeTabIndex;

    public bool IsSearchTab => SelectedFreeTabIndex == 0;
    public bool IsSavedTab  => SelectedFreeTabIndex == 1;

    public int SavedConfigsCount => _savedConfigs.Count;

    public string SavedTabHeaderText => SavedConfigsCount > 0
        ? Strings.FcTabSavedWithCount(SavedConfigsCount)
        : Strings.FcTabSaved;

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

    public string SavedRecheckStaleButtonText => Strings.FcSavedRecheckStaleBtn(StaleSavedCount);

    public bool HasStaleSaved => StaleSavedCount > 0;

    public bool IsSavedEmpty => DisplayedSavedConfigs.Count == 0;

    [ObservableProperty] private string _selectedCountry = "All";
    partial void OnSelectedCountryChanged(string value) => ApplyFiltersAndStats();

    partial void OnExcludeRuChanged(bool value) => ApplyFiltersAndStats();

    [ObservableProperty] private int? _deepVerifyTargetCount = 5;

    [ObservableProperty] private bool _excludeRu = true;

    [ObservableProperty] private bool _useLatencyGoal = true;
    [ObservableProperty] private int? _latencyGoalTarget = 10;
    [ObservableProperty] private int? _latencyGoalMaxPingMs = 400;

    [ObservableProperty] private bool _fastScanMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPreset))]
    [NotifyPropertyChangedFor(nameof(MeasureBandwidth))]
    private int _deepVerifyPresetIndex = 3;

    [ObservableProperty] private int? _customMaxPingMs = 200;
    [ObservableProperty] private int? _customMinBandwidthMbps = 5;

    public bool IsCustomPreset => DeepVerifyPresetIndex == 4;

    public bool MeasureBandwidth => DeepVerifyPresetIndex switch
    {
        0 or 1 or 2 or 4 => true,
        _ => false,
    };

    public (int? maxPing, int? minBw) ResolvedGoal => DeepVerifyPresetIndex switch
    {
        0 => (60, 2),
        1 => (250, 10),
        2 => (300, 1),
        3 => (null, null),
        4 => (CustomMaxPingMs ?? 200, CustomMinBandwidthMbps ?? 5),
        _ => (null, null),
    };

    public bool IsEmpty => _allConfigs.Count == 0;
    public bool IsFilteredEmpty => _allConfigs.Count > 0 && DisplayedConfigs.Count == 0;
    public bool IsListVisible => !IsEmpty && !IsFilteredEmpty;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal;
    public bool HasProgress => ProgressTotal > 0;
    partial void OnProgressTotalChanged(int value) => OnPropertyChanged(nameof(HasProgress));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        var target = Math.Clamp(LatencyGoalTarget ?? 10, 1, 50);
        var maxPing = (UseLatencyGoal && LatencyGoalMaxPingMs.HasValue)
            ? Math.Clamp(LatencyGoalMaxPingMs.Value, 50, 2000)
            : 1000;

        var verifiedList = new List<FreeConfigEntry>();

        try
        {
            _aggregator.RequireTlsHandshake = !FastScanMode;
            _deepVerifier.MeasureBandwidth = true;

            var sources = FreeConfigSources.GetAll(_getSettings?.Invoke());

            var pool = await Task.Run(() => _aggregator.FetchPoolAsync(sources, ct));
            ct.ThrowIfCancellationRequested();

            bool CountryAllowed(FreeConfigEntry c) =>
                !ExcludeRu || !string.Equals(
                    c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase);

            var cachedVerified = pool
                .Where(c => c.Status == FreeConfigStatus.Verified)
                .Where(CountryAllowed)
                .ToList();
            var cachedVerifiedIds = new HashSet<string>(
                cachedVerified.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            var queue = cachedVerified
                .Concat(pool.Where(c =>
                    !cachedVerifiedIds.Contains(c.Id) && CountryAllowed(c)))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = Strings.FcStatusBatchedSearchStart(target, pool.Count);
                _allConfigs = new List<FreeConfigEntry>(verifiedList);
                ApplyFiltersAndStats();
                ProgressTotal = target;
                ProgressDone = 0;
            });

            var deepCap = ComputeAdaptiveDeepCap();
            _logger.Information("[FreeConfigs] adaptive deep-verify cap = {cap} (CPU cores: {cpu})",
                deepCap, Environment.ProcessorCount);
            var deepSem = new SemaphoreSlim(deepCap);
            var batchSize = FreeConfigAggregator.DefaultBatchSize;
            var processedCount = 0;
            var totalBatches = (queue.Count + batchSize - 1) / batchSize;

            var inFlightBatches = new List<Task>();
            const int MaxBatchesInFlight = 2;

            Task<List<FreeConfigEntry>>? prefetchedTcp = null;

            for (int i = 0; i < queue.Count; i += batchSize)
            {
                if (ct.IsCancellationRequested || verifiedList.Count >= target) break;

                var currentBatchNum = (i / batchSize) + 1;

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
                    }, ct);
                }

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

                var batchVerifyTask = DeepVerifyBatchAsync(
                    okSubset, verifiedList, deepSem, target, maxPing, ct);
                inFlightBatches.Add(batchVerifyTask);
                processedCount += batch.Count;

                batch = null!;
                okSubset = null!;

                if (inFlightBatches.Count >= MaxBatchesInFlight)
                {
                    var finished = await Task.WhenAny(inFlightBatches);
                    inFlightBatches.Remove(finished);
                    try { await finished; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[FreeConfigs] Batch deep-verify wave threw");
                    }
                }
            }

            if (inFlightBatches.Count > 0)
            {
                try { await Task.WhenAll(inFlightBatches); }
                catch (OperationCanceledException) { throw; }
                catch {  }
            }
            inFlightBatches.Clear();

            if (prefetchedTcp != null)
            {
                try { await prefetchedTcp; }
                catch {  }
                prefetchedTcp = null;
            }

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

    private static int ComputeAdaptiveDeepCap()
    {
        var cpu = Environment.ProcessorCount;
        if (cpu <= 3) return 3;
        if (cpu <= 7) return 5;
        return 8;
    }

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

    private async Task DeepVerifyBatchAsync(
        List<FreeConfigEntry> okSubset,
        List<FreeConfigEntry> verifiedList,
        SemaphoreSlim deepSem,
        int target,
        int maxPing,
        CancellationToken ct)
    {
        var deepTasks = new List<Task>();
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
            catch {  }
        }
    }

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
            var probedHost = cfg.Host;
            var probedPort = cfg.Port;
            var probedCc = string.IsNullOrEmpty(cfg.CountryCode) ? "??" : cfg.CountryCode;
            var startedFound = 0;
            lock (verifiedList) startedFound = verifiedList.Count;

            Dispatcher.UIThread.Post(() =>
            {
                StatusText = Strings.FcStatusBatchedProbing(
                    startedFound, target, probedHost, probedPort, probedCc);
            });

            var skipDeep = cfg.Status == FreeConfigStatus.Verified
                && cfg.LastDeepVerifyAt.HasValue
                && (DateTime.UtcNow - cfg.LastDeepVerifyAt.Value) < TimeSpan.FromHours(6)
                && cfg.LatencyMs > 0;

            if (!skipDeep)
            {
                await _deepVerifier.VerifyOneAsync(cfg, ct);
            }

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
                        UpsertSavedConfig(cfg);
                        ApplyFiltersAndStats();
                        RebuildSavedDisplayList();
                        ProgressDone = Math.Min(snapshot.Count, ProgressTotal);
                        StatusText = Strings.FcStatusBatchedFound(snapshot.Count, target);
                    });
                }
            }
        }
        catch (OperationCanceledException) {  }
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

    private void RebuildSavedDisplayList()
    {
        try
        {
            var prevSelectedId = SelectedItem?.Id;
            var items = _savedConfigs
                .Select(c => new FreeConfigItemViewModel(c))
                .OrderBy(vm => vm.FreshnessSortKey)
                .ToList();
            DisplayedSavedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);
            if (SelectedFreeTabIndex == 1 && !string.IsNullOrEmpty(prevSelectedId))
            {
                var matched = DisplayedSavedConfigs.FirstOrDefault(c => c.Id == prevSelectedId);
                if (matched != null)
                    SelectedItem = matched;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] RebuildSavedDisplayList failed");
            DisplayedSavedConfigs = new ObservableCollection<FreeConfigItemViewModel>();
        }
    }

    private void NotifySavedTabBindings()
    {
        OnPropertyChanged(nameof(SavedConfigsCount));
        OnPropertyChanged(nameof(SavedTabHeaderText));
        OnPropertyChanged(nameof(StaleSavedCount));
        OnPropertyChanged(nameof(SavedRecheckStaleButtonText));
        OnPropertyChanged(nameof(HasStaleSaved));
        OnPropertyChanged(nameof(IsSavedEmpty));
    }

    private void SaveSavedConfigsToCache()
    {
        try
        {
            var file = new FreeConfigCache.CacheFile
            {
                Configs = _savedConfigs.ToList(),
                LastAggregatedAt = DateTime.UtcNow,
            };
            _savedCache.Save(file);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigs] SaveSavedConfigsToCache failed (non-fatal)");
        }
    }

    [RelayCommand]
    private async Task RecheckOneAsync(FreeConfigItemViewModel? item)
    {
        if (item == null || IsBusy) return;

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
                await _aggregator.Tester.TcpPingOnlyAsync(entry, ct);
                await _deepVerifier.VerifyOneAsync(entry, ct);
                ct.ThrowIfCancellationRequested();
                FreeConfigFreshness.MergeRecheckResult(entry, prior, DateTime.UtcNow);
                _logger.Information("[Recheck] {host}:{port} → {result} ({ping} ms)",
                    entry.Host, entry.Port,
                    entry.LastVerifyFailedAt.HasValue ? "failed; last-good preserved" : "Verified",
                    entry.LatencyMs);
            }
            catch (OperationCanceledException)
            {
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
        catch (OperationCanceledException) {  }
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
                    await _aggregator.Tester.TcpPingOnlyAsync(cfg, ct);
                    await _deepVerifier.VerifyOneAsync(cfg, ct);
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

    [RelayCommand]
    private void RemoveFromSaved(FreeConfigItemViewModel? item)
    {
        if (item == null) return;
        var id = item.Entry.Id;
        if (string.IsNullOrEmpty(id)) return;

        _savedConfigs.RemoveAll(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (SelectedItem == item) SelectedItem = null;

        SaveSavedConfigsToCache();
        RebuildSavedDisplayList();
        NotifySavedTabBindings();
    }

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

    private static void ReclaimPostSearchMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        }
        catch {  }

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch {  }

        try
        {
            SkiaSharp.SKGraphics.PurgeAllCaches();
        }
        catch {  }
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

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            var logsDir = VPNRouter.Core.AppPaths.LogsDir;
            Directory.CreateDirectory(logsDir);

            System.Diagnostics.ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false
                };
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open",
                    UseShellExecute = false
                };
            }
            psi.ArgumentList.Add(logsDir);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
    }

    [ObservableProperty] private string _newUserSourceName = string.Empty;
    [ObservableProperty] private string _newUserSourceUrl = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<VPNRouter.Core.Models.UserFreeSource> UserSources { get; } = new();

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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText = Strings.FcUserSrcInvalidUrl;
            return;
        }

        var settings = _getSettings?.Invoke();
        if (settings == null) return;

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

        _settingsStore.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);
        ReloadUserSources();
        StatusText = Strings.FcUserSrcRemoved;
    }

    private static bool IsMainVpnActive()
    {
        try
        {
            return VPNRouter.Core.Services.ProcessQuery.AnyAlive("sing-box");
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task DeepVerifyTopAsync()
    {
        if (IsBusy) return;

        if (IsMainVpnActive())
        {
            StatusText = Strings.FcStatusMainVpnActive;
            _logger.Warning("[DV] Main sing-box.exe is running — deep verify will route test traffic through it, results unreliable");
        }

        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
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

            _deepVerifier.MeasureBandwidth = MeasureBandwidth;
            var (maxPing, minBw) = ResolvedGoal;

            var foundVerified = 0;
            var tested = 0;
            var lastSaveAt = DateTime.UtcNow;

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
                    if (runningTasks.Count >= 20)
                    {
                        var done = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(done);
                    }
                }
                await Task.WhenAll(runningTasks);
            });

            await Dispatcher.UIThread.InvokeAsync(TrimAndReclaim);

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
            await Dispatcher.UIThread.InvokeAsync(TrimAndReclaim);

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
            var total = _allConfigs.Count;
            var working = 0;
            var verified = 0;
            var tlsFailed = 0;
            var implausible = 0;
            var timeout = 0;
            var unreachable = 0;

            var uniqueCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bestByHost = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);

            var maxPing = (UseLatencyGoal && LatencyGoalMaxPingMs.HasValue)
                ? Math.Clamp(LatencyGoalMaxPingMs.Value, 50, 2000)
                : (int?)null;
            var filterCountry = SelectedCountry;
            var isAllCountry = string.IsNullOrEmpty(filterCountry) || string.Equals(filterCountry, "All", StringComparison.OrdinalIgnoreCase);

            for (var i = 0; i < _allConfigs.Count; i++)
            {
                var c = _allConfigs[i];
                switch (c.Status)
                {
                    case FreeConfigStatus.Ok: working++; break;
                    case FreeConfigStatus.Verified: verified++; break;
                    case FreeConfigStatus.TlsFailed: tlsFailed++; break;
                    case FreeConfigStatus.Implausible: implausible++; break;
                    case FreeConfigStatus.Timeout: timeout++; break;
                    case FreeConfigStatus.Unreachable: unreachable++; break;
                }

                if (!string.IsNullOrEmpty(c.CountryCode))
                {
                    uniqueCountries.Add(c.CountryCode);
                }

                if (c.Status != FreeConfigStatus.Verified)
                    continue;

                if (!isAllCountry && !string.Equals(c.CountryCode, filterCountry, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ExcludeRu && string.Equals(c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (maxPing.HasValue && (c.LatencyMs <= 0 || c.LatencyMs > maxPing.Value))
                    continue;

                var host = c.Host ?? string.Empty;
                if (bestByHost.TryGetValue(host, out var existing))
                {
                    if (FreeConfigItemViewModel.SortKeyFor(c) < FreeConfigItemViewModel.SortKeyFor(existing))
                    {
                        bestByHost[host] = c;
                    }
                }
                else
                {
                    bestByHost[host] = c;
                }
            }

            TotalCount       = total;
            WorkingCount     = working;
            VerifiedCount    = verified;
            TlsFailedCount   = tlsFailed;
            ImplausibleCount = implausible;
            TimeoutCount     = timeout;
            UnreachableCount = unreachable;

            if (_lastCountryCodes == null || !_lastCountryCodes.SetEquals(uniqueCountries))
            {
                _lastCountryCodes = uniqueCountries;
                var sortedCc = uniqueCountries.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                Countries = new ObservableCollection<string>(new[] { "All" }.Concat(sortedCc));
                if (!Countries.Contains(SelectedCountry))
                    SelectedCountry = "All";
            }

            var items = bestByHost.Values
                .OrderBy(FreeConfigItemViewModel.SortKeyFor)
                .Take(300)
                .Select(c => new FreeConfigItemViewModel(c))
                .ToList();

            var prevSelectedId = SelectedItem?.Id;
            DisplayedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);

            SelectedItem = (!string.IsNullOrEmpty(prevSelectedId)
                ? DisplayedConfigs.FirstOrDefault(c => c.Id == prevSelectedId)
                : null)
                ?? DisplayedConfigs.FirstOrDefault();
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
        var ru = string.Equals(VPNRouter.App.Localization.Strings.Lang, "ru", StringComparison.OrdinalIgnoreCase);
        if (t.TotalMinutes < 1)   return ru ? "только что" : "just now";
        if (t.TotalMinutes < 60)  return ru ? $"{(int)t.TotalMinutes} мин назад" : $"{(int)t.TotalMinutes}m ago";
        if (t.TotalHours   < 24)  return ru ? $"{(int)t.TotalHours} ч назад" : $"{(int)t.TotalHours}h ago";
        return ru ? $"{(int)t.TotalDays} дн назад" : $"{(int)t.TotalDays}d ago";
    }
}
