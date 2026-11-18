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
    private Task? _loadSaved;

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

    public bool IsBusy => _cts != null;

    public IReadOnlyList<FreeConfigEntry> Saved => _saved;

    public event Action<string>? OnStatus;
    public event Action<int, int>? OnProgress;
    public event Action<FreeConfigEntry>? OnFound;
    public event Action<int>? OnFinished;
    public event Action<string>? OnFailed;

    public event Action<FreeConfigEntry>? OnEntryUpgraded;

    public Task EnsureCacheLoadedAsync() => _loadSaved ??= LoadSavedAsync();

    private async Task LoadSavedAsync()
    {
        try
        {
            var file = await _cache.LoadAsync();
            var now = DateTime.UtcNow;
            _saved = file.Configs.Where(c => FreeConfigKeepPolicy.ShouldRetainInSavedList(c, now)).ToList();
            _logger.Information("[Android.FreeConfigs] cache loaded: {n} saved entries", _saved.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Android.FreeConfigs] cache load failed");
        }
    }

    public async Task FindAsync(
        int target,
        int maxPingMs,
        bool excludeRu,
        int batchSize = 200)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var verifiedThisRun = new List<FreeConfigEntry>();

        try
        {
            await EnsureCacheLoadedAsync();
            ct.ThrowIfCancellationRequested();
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
                        _logger.Warning("[Android.FreeConfigs] Verification failed ({Type})", ex.GetType().Name);
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

            SaveSaved();

            OnStatus?.Invoke(verifiedThisRun.Count >= target
                ? string.Format(Localization.FcStatusDoneOk, verifiedThisRun.Count)
                : string.Format(Localization.FcStatusDoneExhausted,
                    verifiedThisRun.Count, target));
            OnFinished?.Invoke(verifiedThisRun.Count);
        }
        catch (OperationCanceledException)
        {
            SaveSaved();

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
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async Task RecheckSavedAsync(FreeConfigEntry entry)
    {
        if (IsBusy || !_saved.Contains(entry)) return;
        using var operation = new CancellationTokenSource();
        _cts = operation;
        var prior = FreeConfigFreshness.RecheckSnapshot.Capture(entry);
        try
        {
            OnStatus?.Invoke(Localization.FcStatusRecheckOne(entry.Host, entry.Port, entry.CountryCode ?? "??"));
            OnProgress?.Invoke(0, 1);
            await _deepVerifier.VerifyOneAsync(entry, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            FreeConfigFreshness.MergeRecheckResult(entry, prior, DateTime.UtcNow);
            SaveSaved();
            var passed = entry.Status == FreeConfigStatus.Verified;
            OnProgress?.Invoke(1, 1);
            OnStatus?.Invoke(Localization.FcStatusRecheckAllDone(passed ? 1 : 0, passed ? 0 : 1));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            FreeConfigFreshness.RestorePriorState(entry, prior);
            OnStatus?.Invoke(Localization.FcStatusCancelled);
        }
        finally
        {
            _cts = null;
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch { }
    }

    public void RemoveSaved(FreeConfigEntry entry)
    {
        if (IsBusy || !_saved.Remove(entry)) return;
        SaveSaved();
    }

    public void ClearSaved()
    {
        if (IsBusy || _saved.Count == 0) return;
        _saved.Clear();
        SaveSaved();
    }

    private void SaveSaved() => _cache.Save(new FreeConfigCache.CacheFile
    {
        Configs = _saved,
        LastAggregatedAt = DateTime.UtcNow,
    });

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
