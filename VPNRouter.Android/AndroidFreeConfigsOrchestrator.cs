using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Android;

internal sealed class AndroidFreeConfigsOrchestrator
{
    private readonly FreeConfigCache _cache;
    private readonly FreeConfigPoolFetcher _poolFetcher;
    private readonly FreeConfigTester _tester;
    private readonly AndroidFreeConfigDeepVerifier _deepVerifier;
    private readonly ILogger _logger;

    private List<FreeConfigEntry> _saved = new();

    private CancellationTokenSource? _cts;
    private bool _busy;

    public AndroidFreeConfigsOrchestrator(ILogger logger)
    {
        _logger = logger;
        _cache = new FreeConfigCache(logger);
        _poolFetcher = new FreeConfigPoolFetcher(logger);
        _tester = new FreeConfigTester
        {
            RequireTlsHandshake = true,
        };
        _deepVerifier = new AndroidFreeConfigDeepVerifier(logger);
    }

    public bool IsBusy => _busy;

    public IReadOnlyList<FreeConfigEntry> Saved => _saved;

    public event Action<string>? OnStatus;
    public event Action<int, int>? OnProgress;
    public event Action<FreeConfigEntry>? OnFound;
    public event Action<int>? OnFinished;
    public event Action<string>? OnFailed;

    public event Action<FreeConfigEntry>? OnEntryUpgraded;

    public Task EnsureCacheLoadedAsync()
    {
        if (_saved.Count > 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                var file = _cache.Load();
                var now = DateTime.UtcNow;
                _saved = file.Configs?
                    .Where(c => FreeConfigKeepPolicy.ShouldRetainInSavedList(c, now))
                    .ToList() ?? new List<FreeConfigEntry>();
                _logger.Information("[Android.FreeConfigs] cache loaded: {n} saved entries", _saved.Count);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Android.FreeConfigs] cache load failed");
                _saved = new List<FreeConfigEntry>();
            }
        });
    }

    public async Task FindAsync(
        int target,
        int maxPingMs,
        bool excludeRu,
        int batchSize = 200)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var verifiedThisRun = new List<FreeConfigEntry>();

        try
        {
            OnStatus?.Invoke(Localization.FcStatusFetchingPool);

            List<FreeConfigEntry>? pool = null;
            try
            {
                pool = await _poolFetcher.FetchPoolAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Android.FreeConfigs] pool fetch failed");
            }

            if (pool == null || pool.Count == 0)
            {
                OnStatus?.Invoke(Localization.FcStatusPoolEmpty);
                OnFinished?.Invoke(0);
                return;
            }

            var cachedOk = new HashSet<string>(
                _saved.Where(c => c.Status == FreeConfigStatus.Ok ||
                                  c.Status == FreeConfigStatus.Verified)
                      .Select(c => c.Id),
                StringComparer.OrdinalIgnoreCase);

            bool KeepCountry(FreeConfigEntry c) =>
                !excludeRu || !string.Equals(c.CountryCode, "RU", StringComparison.OrdinalIgnoreCase);

            var head = pool.Where(c => cachedOk.Contains(c.Id) && KeepCountry(c)).ToList();
            var tail = pool.Where(c => !cachedOk.Contains(c.Id) && KeepCountry(c)).ToList();
            var queue = head.Concat(tail).ToList();

            OnStatus?.Invoke(string.Format(Localization.FcStatusPoolLoaded,
                pool.Count, queue.Count));
            OnProgress?.Invoke(0, target);

            var foundHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processed = 0;
            for (int i = 0; i < queue.Count; i += batchSize)
            {
                if (ct.IsCancellationRequested) break;
                if (verifiedThisRun.Count >= target) break;

                var slice = queue.Skip(i).Take(batchSize).ToList();
                var batchProgress = new Progress<(int done, int total)>(p =>
                {
                    OnStatus?.Invoke(string.Format(Localization.FcStatusTesting,
                        verifiedThisRun.Count, target,
                        processed + p.done, queue.Count));
                });

                try
                {
                    await _tester.TestAllAsync(slice, batchProgress, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[Android.FreeConfigs] batch test threw — skipping");
                    processed += slice.Count;
                    continue;
                }

                processed += slice.Count;

                var candidates = slice
                    .Where(c => c.Status == FreeConfigStatus.Ok &&
                                c.LatencyMs > 0 &&
                                c.LatencyMs <= maxPingMs)
                    .OrderBy(c => c.LatencyMs)
                    .Where(c => !foundHosts.Contains(c.Host))
                    .ToList();

                foreach (var cand in candidates)
                    OnFound?.Invoke(cand);

                foreach (var cand in candidates)
                {
                    if (ct.IsCancellationRequested) break;
                    if (verifiedThisRun.Count >= target) break;
                    if (foundHosts.Contains(cand.Host)) continue;

                    OnStatus?.Invoke(string.Format(Localization.FcStatusDeepVerifying,
                        verifiedThisRun.Count, target));
                    try
                    {
                        await _deepVerifier.VerifyOneAsync(cand, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[Android.FreeConfigs] deep verify threw for {host}:{port}",
                            cand.Host, cand.Port);
                    }

                    if (cand.Status == FreeConfigStatus.Verified)
                    {
                        foundHosts.Add(cand.Host);
                        verifiedThisRun.Add(cand);
                        UpsertSaved(cand);
                        OnEntryUpgraded?.Invoke(cand);
                        OnProgress?.Invoke(verifiedThisRun.Count, target);
                        OnStatus?.Invoke(string.Format(Localization.FcStatusFound,
                            verifiedThisRun.Count, target));
                    }
                }
            }

            _logger.Information("[Android.FreeConfigs] find complete: {n}/{target} verified",
                verifiedThisRun.Count, target);

            try
            {
                var file = _cache.Load();
                file.Configs = _saved;
                file.LastAggregatedAt = DateTime.UtcNow;
                _cache.Save(file);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Android.FreeConfigs] cache save failed (non-fatal)");
            }

            OnStatus?.Invoke(verifiedThisRun.Count >= target
                ? string.Format(Localization.FcStatusDoneOk, verifiedThisRun.Count)
                : string.Format(Localization.FcStatusDoneExhausted,
                    verifiedThisRun.Count, target));
            OnFinished?.Invoke(verifiedThisRun.Count);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var file = _cache.Load();
                file.Configs = _saved;
                file.LastAggregatedAt = DateTime.UtcNow;
                _cache.Save(file);
            }
            catch {  }

            OnStatus?.Invoke(Localization.FcStatusCancelled);
            OnFinished?.Invoke(verifiedThisRun.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Android.FreeConfigs] FindAsync failed");
            OnStatus?.Invoke(Localization.FcStatusFailed(ex.Message));
            OnFailed?.Invoke(ex.Message);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch {  }
    }

    public void RemoveSaved(FreeConfigEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Id)) return;
        var removed = _saved.RemoveAll(c =>
            string.Equals(c.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;

        try
        {
            var file = _cache.Load();
            file.Configs = _saved;
            file.LastAggregatedAt = DateTime.UtcNow;
            _cache.Save(file);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Android.FreeConfigs] cache save (RemoveSaved) failed");
        }
    }

    public void ClearSaved()
    {
        if (_saved.Count == 0) return;
        _saved = new List<FreeConfigEntry>();
        try
        {
            var file = _cache.Load();
            file.Configs = _saved;
            file.LastAggregatedAt = DateTime.UtcNow;
            _cache.Save(file);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Android.FreeConfigs] cache save (ClearSaved) failed");
        }
    }

    private void UpsertSaved(FreeConfigEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Id)) return;
        for (int i = 0; i < _saved.Count; i++)
        {
            if (string.Equals(_saved[i].Id, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                _saved[i] = entry;
                return;
            }
        }
        _saved.Add(entry);
    }
}
