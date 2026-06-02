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
///   <item>NO bandwidth column — Deep Verify on Android doesn't run
///   the 5&#x202F;MB throughput probe (would need a separate libbox box
///   per measurement, doubling spin-up cost).</item>
///   <item>NO GC.Collect / SkiaSharp.SKGraphics.PurgeAllCaches — runtime
///   handles GC on Android.</item>
///   <item>NO per-row Recheck / RecheckAllStale — Saved tab here is a
///   passive snapshot of last-find. Refresh = run Find again.</item>
/// </list>
///
/// <para>Bug&#x202F;#1 (v3.0 android-alpha r5+, 2026-05-11): Deep Verify is
/// now present via <see cref="AndroidFreeConfigDeepVerifier"/>, which
/// spins a transient libbox <c>BoxService</c> per config (SOCKS inbound
/// only — no TUN) and HTTP-probes Cloudflare through it. Pre-fix the
/// pipeline stopped at TCP+TLS and every entry showed single&#x202F;✓.
/// The post-TCP-TLS Deep Verify pass upgrades successful entries to
/// Status=Verified (✓✓) in place — see <see cref="OnEntryUpgraded"/>
/// for the UI hook. Verify is sequential (one box at a time) since
/// libbox's concurrent-instance behavior is uncharted.</para>
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
    private readonly AndroidFreeConfigDeepVerifier _deepVerifier;
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
            // TCP+TLS gate: filters out honeypots / dead Reality endpoints
            // before they reach the (much slower) Deep Verify pass below.
            RequireTlsHandshake = true,
        };
        _deepVerifier = new AndroidFreeConfigDeepVerifier(logger);
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
    /// Bug&#x202F;#1: fired when an already-found entry transitions from
    /// <see cref="FreeConfigStatus.Ok"/> (single&#x202F;✓) to
    /// <see cref="FreeConfigStatus.Verified"/> (✓✓) after the Deep Verify
    /// pass. The UI handler should replace the entry in its
    /// ObservableCollection so the row re-renders with the new badge —
    /// FreeConfigEntry is a plain POCO (no INotifyPropertyChanged), so
    /// in-place mutation alone won't redraw.
    /// </summary>
    public event Action<FreeConfigEntry>? OnEntryUpgraded;

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
    ///    — surface candidates as they land via <see cref="OnFound"/>;
    /// 4) Deep-verify candidates (real HTTP through libbox) and stop when
    ///    <paramref name="target"/> entries pass DEEP verify, the queue is
    ///    exhausted, or the user cancels.
    ///
    /// <para>v2.39.0 (public-configs audit P1): the target counts VERIFIED
    /// entries, not TCP/TLS candidates. Pre-fix the run stopped once
    /// <paramref name="target"/> Ok candidates were collected and only then
    /// deep-verified them — if several failed deep verify the user was left
    /// with fewer than <paramref name="target"/> connectable configs and the
    /// search never back-filled from the remaining pool. Now deep verify is
    /// interleaved per batch and only Verified entries count toward the target
    /// and are persisted to the durable Saved list.</para>
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
                pool.Count, queue.Count));
            OnProgress?.Invoke(0, target);

            // Host-dedupe: at most one VERIFIED config per host counts toward the
            // target. v2.40.0 (review L2): a host is claimed only AFTER one of its
            // candidates deep-verifies (see the loop below), not at surface time.
            var foundHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processed = 0;
            // v2.40.0 (review N1): the loop tests batches until `target` VERIFIED
            // entries are found, the queue is exhausted, or the user cancels —
            // there is no fixed cap. We expect to hit `target` long before
            // exhausting a large pool.
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

                // TCP/TLS candidates from this batch (ping threshold + new
                // host), best latency first. They surface immediately as
                // single-check rows so the user sees progress, but the UI keeps
                // Connect DISABLED until deep verify upgrades a row to Verified.
                var candidates = slice
                    .Where(c => c.Status == FreeConfigStatus.Ok &&
                                c.LatencyMs > 0 &&
                                c.LatencyMs <= maxPingMs)
                    .OrderBy(c => c.LatencyMs)
                    // Skip only hosts ALREADY verified in a prior batch. Within
                    // this batch the host is claimed in the verify loop on success
                    // (review L2) — so distinct candidates on one host stay
                    // eligible until one of them actually deep-verifies.
                    .Where(c => !foundHosts.Contains(c.Host))
                    .ToList();

                foreach (var cand in candidates)
                    OnFound?.Invoke(cand);

                // Deep-verify candidates (sequential - libbox runs one box at a
                // time; concurrent-instance behavior is uncharted) until we
                // reach the target VERIFIED count or this batch's candidates
                // drain. Only Verified entries count toward the target and get
                // persisted to the durable Saved list - a candidate that fails
                // deep verify stays a single-check row and the loop pulls more
                // from later batches.
                //
                // Failure modes are isolated by AndroidFreeConfigDeepVerifier:
                // bridge unavailable -> returns silently; libbox throws ->
                // logged + entry stays Ok; per-config timeout -> entry stays Ok.
                // None abort the pass - we always continue to the next entry.
                foreach (var cand in candidates)
                {
                    if (ct.IsCancellationRequested) break;
                    if (verifiedThisRun.Count >= target) break;
                    // review L2: a sibling candidate on this host already verified
                    // earlier in this batch — skip the dup (one Verified per host).
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
                        foundHosts.Add(cand.Host);      // claim host only on success (review L2)
                        verifiedThisRun.Add(cand);
                        UpsertSaved(cand);              // persist ONLY verified
                        OnEntryUpgraded?.Invoke(cand);  // upgrades badge, enables Connect
                        OnProgress?.Invoke(verifiedThisRun.Count, target);
                        OnStatus?.Invoke(string.Format(Localization.FcStatusFound,
                            verifiedThisRun.Count, target));
                    }
                }
            }

            _logger.Information("[Android.FreeConfigs] find complete: {n}/{target} verified",
                verifiedThisRun.Count, target);

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
