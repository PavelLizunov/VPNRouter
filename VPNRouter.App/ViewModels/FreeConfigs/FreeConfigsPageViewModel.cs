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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _aggregator.OnStageChanged -= OnAggregatorStage;
            _aggregator.OnTestProgress  -= OnAggregatorProgress;
        }
        catch { }

        try { _refreshCts?.Cancel(); _refreshCts?.Dispose(); }
        catch { }
    }

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

    [ObservableProperty] private string _selectedCountry = "All";
    partial void OnSelectedCountryChanged(string value) => ApplyFiltersAndStats();

    partial void OnExcludeRuChanged(bool value) => ApplyFiltersAndStats();

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
                catch { }
            }
            inFlightBatches.Clear();

            if (prefetchedTcp != null)
            {
                try { await prefetchedTcp; }
                catch { }
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
