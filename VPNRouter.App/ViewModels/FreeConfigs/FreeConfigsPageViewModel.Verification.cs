using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

public partial class FreeConfigsPageViewModel
{
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
            catch { }
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
        catch (OperationCanceledException) { }
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
}
