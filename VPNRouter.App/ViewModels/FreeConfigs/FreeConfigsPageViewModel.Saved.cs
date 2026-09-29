using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

public partial class FreeConfigsPageViewModel
{
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

    [ObservableProperty] private ObservableCollection<FreeConfigItemViewModel> _displayedConfigs = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSavedEmpty))]
    private ObservableCollection<FreeConfigItemViewModel> _displayedSavedConfigs = new();

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
        catch (OperationCanceledException) { }
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
}
