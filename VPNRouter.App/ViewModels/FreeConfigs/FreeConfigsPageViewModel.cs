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
public partial class FreeConfigsPageViewModel : ObservableObject
{
    private readonly FreeConfigAggregator _aggregator;
    private readonly FreeConfigDeepVerifier _deepVerifier;
    private readonly ILogger _logger;
    private readonly Func<FreeConfigEntry, Task<bool>> _applyAsync;
    private readonly Func<VPNRouter.Core.Models.AppSettings>? _getSettings;

    private List<FreeConfigEntry> _allConfigs = new();
    private CancellationTokenSource? _refreshCts;

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

        // Load cached snapshot if exists.
        var file = _aggregator.Cache.Load();
        _allConfigs = file.Configs;
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

    [ObservableProperty] private bool _onlyWorking = true;
    partial void OnOnlyWorkingChanged(bool value) => ApplyFiltersAndStats();

    /// <summary>How many Verified configs to hunt for in a deep-verify session.</summary>
    [ObservableProperty] private int _deepVerifyTargetCount = 5;

    /// <summary>If true, skip Russian-country configs during deep-verify (user is bypassing RU blocks).</summary>
    [ObservableProperty] private bool _excludeRu = true;

    /// <summary>v2.13.17: if true, Refresh stops early once N configs matching the latency criterion are found.</summary>
    [ObservableProperty] private bool _useLatencyGoal = false;
    [ObservableProperty] private int _latencyGoalTarget = 50;
    [ObservableProperty] private int _latencyGoalMaxPingMs = 200;

    /// <summary>v2.13.18: if true, Refresh does TCP-only test (skip TLS handshake). 3× faster but misses honeypots.</summary>
    [ObservableProperty] private bool _fastScanMode = false;

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

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        try
        {
            // v2.13.18: apply fast scan toggle before RefreshAsync reads it
            _aggregator.RequireTlsHandshake = !FastScanMode;

            // v2.14.4: merge user-provided sources with built-in 14.
            var sources = FreeConfigSources.GetAll(_getSettings?.Invoke());

            var fresh = await Task.Run(() => _aggregator.RefreshAsync(
                sources: sources,
                goalTargetCount:  UseLatencyGoal ? LatencyGoalTarget : (int?)null,
                goalMaxLatencyMs: UseLatencyGoal ? LatencyGoalMaxPingMs : (int?)null,
                ct: _refreshCts.Token));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = fresh;
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusRefreshed(fresh.Count);
            });
        }
        catch (OperationCanceledException)
        {
            // CRITICAL: Aggregator saves partial test results to cache every 50 tests / 5s.
            // On cancel, reload cache into the UI so the user sees their accumulated progress
            // (not the stale pre-refresh state). Otherwise 14k/24k tested feels "reset".
            await ReloadFromCacheAsync(Strings.FcStatusCancelled);
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigs refresh failed: {err}", ex.Message);
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

            var candidates = _allConfigs
                .Where(c => !ExcludeRu ||
                            !string.Equals(c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase))
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

            // Final persist.
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
            // Save whatever we have so far.
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

            // Apply filter + sort.
            IEnumerable<FreeConfigEntry> q = _allConfigs;
            if (OnlyWorking)
                q = q.Where(c => c.Status == FreeConfigStatus.Ok || c.Status == FreeConfigStatus.Verified);
            if (!string.Equals(SelectedCountry, "All", StringComparison.OrdinalIgnoreCase))
                q = q.Where(c => string.Equals(c.CountryCode, SelectedCountry, StringComparison.OrdinalIgnoreCase));

            var items = q
                .Select(c => new FreeConfigItemViewModel(c))
                .OrderBy(vm => vm.LatencySortKey)
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
