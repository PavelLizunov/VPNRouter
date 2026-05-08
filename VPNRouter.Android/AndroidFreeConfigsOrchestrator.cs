using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 — Android-side orchestrator for the Free Configs feature
/// (handbook §1.1 "desktop reference is source-of-truth"). Mirrors the
/// portion of <c>VPNRouter.App.ViewModels.FreeConfigs.FreeConfigsPageViewModel</c>
/// that Android needs, minus the desktop-only bits:
///
/// <list type="bullet">
///   <item>NO Avalonia.Threading.Dispatcher — Android's BuildXxxView()
///   already runs on UI thread; events fire synchronously and the UI
///   layer can dispatch back if it ever runs an op off-thread.</item>
///   <item>NO FreeConfigDeepVerifier — Android sing-box is libbox
///   in-process, single instance; can't spawn extra runtimes per probe.
///   We use TCP+TLS (FreeConfigTester) as the success bar. Status=Ok,
///   not Status=Verified.</item>
///   <item>NO bandwidth column — without Deep Verify, no throughput
///   measurement.</item>
///   <item>NO GC.Collect / SkiaSharp.SKGraphics.PurgeAllCaches — runtime
///   handles GC on Android.</item>
///   <item>NO per-row Recheck / RecheckAllStale — Saved tab here is a
///   passive snapshot of last-find. Refresh = run Find again.</item>
/// </list>
///
/// <para>Cache + pool fetch use the unmodified Core services
/// (<see cref="FreeConfigCache"/>, <see cref="FreeConfigPoolFetcher"/>,
/// <see cref="FreeConfigTester"/>) — they're platform-neutral. AppPaths
/// resolves to <c>/data/user/0/&lt;package&gt;/.config/vpnrouter/cache/</c>
/// on Android (Linux branch of <see cref="VPNRouter.Core.AppPaths"/>).</para>
/// </summary>
internal sealed class AndroidFreeConfigsOrchestrator
{
    private readonly FreeConfigCache _cache;
    private readonly FreeConfigPoolFetcher _poolFetcher;
    private readonly FreeConfigTester _tester;
    private readonly ILogger _logger;

    /// <summary>
    /// Working set persisted across app restarts. Loaded from cache on
    /// first <see cref="EnsureCacheLoadedAsync"/>; written back on every
    /// successful Find run.
    /// </summary>
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
            // v2.32.0: TCP+TLS = success bar on Android (no Deep Verify
            // path). Keep TLS validation on so honeypots / dead Reality
            // endpoints get filtered.
            RequireTlsHandshake = true,
        };
    }

    public bool IsBusy => _busy;

    /// <summary>Snapshot of the saved (cumulative) configs list.</summary>
    public IReadOnlyList<FreeConfigEntry> Saved => _saved;

    public event Action<string>? OnStatus;
    public event Action<int, int>? OnProgress; // (done, total)
    public event Action<FreeConfigEntry>? OnFound;
    public event Action<int>? OnFinished; // verified count this run
    public event Action<string>? OnFailed;

    /// <summary>
    /// Lazy-load the persisted saved list from cache. Idempotent — call
    /// every time the overlay opens; subsequent calls no-op.
    /// </summary>
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

    /// <summary>
    /// Find working configs:
    /// 1) Pull <c>pool.json</c> via <see cref="FreeConfigPoolFetcher"/>
    ///    (server-side pre-aggregated list, refreshed every 6h via
    ///    <c>build-free-pool.yml</c>);
    /// 2) Filter by ExcludeRu and IP-dedupe;
    /// 3) Test TCP+TLS in <paramref name="batchSize"/>-entry batches
    ///    (parallel within batch, capped via FreeConfigTester.MaxConcurrency)
    ///    — surface successful entries as they land via <see cref="OnFound"/>;
    /// 4) Stop early when <paramref name="target"/> Ok entries collected
    ///    OR when the user cancels.
    /// </summary>
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

            // Build queue: cached Verified/Ok first (likely still working),
            // then everything else; ExcludeRu applied across both halves.
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
                pool.Count, Math.Min(queue.Count, batchSize * 4)));
            OnProgress?.Invoke(0, target);

            // IP-dedupe at display time so two entries on the same host
            // don't both count toward the user's target.
            var foundHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processed = 0;
            // Hard cap at queue.Count entries to test, but we expect to
            // hit `target` long before. Even at 200 entries × 1.5 s avg
            // per probe / 80 concurrent ≈ 4 s wall clock.
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

                // Pluck Ok entries that meet the ping threshold + new host.
                foreach (var cfg in slice
                    .Where(c => c.Status == FreeConfigStatus.Ok &&
                                c.LatencyMs > 0 &&
                                c.LatencyMs <= maxPingMs)
                    .OrderBy(c => c.LatencyMs))
                {
                    if (verifiedThisRun.Count >= target) break;
                    if (!foundHosts.Add(cfg.Host)) continue;

                    verifiedThisRun.Add(cfg);
                    UpsertSaved(cfg);
                    OnFound?.Invoke(cfg);
                    OnProgress?.Invoke(verifiedThisRun.Count, target);
                    OnStatus?.Invoke(string.Format(Localization.FcStatusFound,
                        verifiedThisRun.Count, target));
                }
            }

            // Persist cumulative saved set + emit final status.
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
            catch { /* swallow on cancel */ }

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
        catch { /* swallow */ }
    }

    /// <summary>Drop a single entry from the saved list and persist.</summary>
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

    /// <summary>Wipe the entire saved list and persist.</summary>
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

    /// <summary>
    /// Insert <paramref name="entry"/> into <see cref="_saved"/> if absent
    /// (by Id), otherwise replace so the row carries the latest TestedAt /
    /// LatencyMs / Status. Mirrors desktop's Phase 1 UpsertSavedConfig.
    /// </summary>
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
