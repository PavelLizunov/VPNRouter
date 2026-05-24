// =============================================================================
// ZapretAutoStrategy — v2.37.0-r1 multi-target probe orchestrator.
//
// Per `plans/research-one-button-zapret-deep-2026-05-24.md` §3.3 option E +
// companion `plans/research-zapret-auto-probe-methodology-2026-05-24.md` §1.
//
// v2.37 evolution from r9's single-target probe:
//
//   - Probes the canonical Flowseal target set in parallel (Discord ×4 +
//     YouTube ×4 = 8 endpoints) instead of just youtube.com. Targets mirror
//     `utils/targets.txt` from Flowseal/zapret-discord-youtube — same list
//     their `test zapret.ps1` exercises, just via HttpClient instead of
//     curl.exe + DPI-timing analysis. Per-target HEAD with 5s timeout, all
//     8 in flight concurrently → ~2 s wall-clock per attempt regardless of
//     count.
//
//   - Tier classification: >=70 % pass → Tier1 (confirmed), 30-70 → Tier2
//     (partial — usable but not all targets reachable), <30 → Tier3 (failed).
//     MVP treats Tier1+Tier2 both as success (the partial may be ok for the
//     user's actual sites); polish phase will split them.
//
//   - Score-aware progress: per-attempt result carries "N/8 ok" counter so
//     the UI lede can render "Тестирую (1/3): general (ALT3) — 7/8 ok" and
//     the air-pill on win can say "В эфире · general (ALT3) · 7/8".
//
// Substrate-agnostic delegate-driven API preserved verbatim — zapret2
// migration is still ~30 LOC (swap start/stop delegates, same probe layer).
//
// NOT IN SCOPE for r1 (deferred to v2.37 polish):
//   - DPI-checker mode (TCP 16-20 freeze detection) via PowerShell wrapper
//     to Flowseal's `test zapret.ps1`. Phase 2.
//   - Per-ISP catalog (option D from research §3.1).
//   - Probe result caching to `%ProgramData%/VPNRouter/cache/zapret_probe.json`.
//   - Multi-protocol probe (HTTP/3 / QUIC).
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// One-button auto-strategy probe orchestrator. Substrate-agnostic — driven
/// via delegates so it works the same on Flowseal winws.exe today and on
/// any future zapret2 winws2.exe substitute.
/// </summary>
public static class ZapretAutoStrategy
{
    /// <summary>
    /// Default seed order — Flowseal community-validated highest-hit-rate
    /// strategies. ALT3 first per ZapretUpdater.ParseStrategies sort
    /// heuristic (line 679+).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSeedOrder = new[]
    {
        "general (ALT3)",
        "general",
        "general (ALT)",
    };

    /// <summary>
    /// Canonical DPI-blocked target set, mirroring Flowseal's
    /// `utils/targets.txt` Discord + YouTube subsets. Probed in parallel.
    /// Hardcoded as the v2.37 default; `ProbeTargetsAsync` will optionally
    /// override from the actual targets.txt file if installed.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultProbeTargets = new[]
    {
        // Discord — the #2 use-case, voice depends on these unblocking
        "https://discord.com",
        "https://gateway.discord.gg",
        "https://cdn.discordapp.com",
        "https://updates.discord.com",
        // YouTube — the #1 DPI-blocked target in the RU footprint
        "https://www.youtube.com",
        "https://youtu.be",
        "https://i.ytimg.com",
        "https://redirector.googlevideo.com",
    };

    /// <summary>Per-strategy soak before probing. WinDivert binds in ~1 s;
    /// 20 s gives the new filter chain time to settle without making the
    /// magic-button wait feel infinite.</summary>
    public static readonly TimeSpan SoakDelay = TimeSpan.FromSeconds(20);

    /// <summary>Per-target HEAD probe timeout. Real successes are sub-second
    /// on a working strategy; anything past 5 s is DPI hang.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Wait window for ImmediateExitDetected (Bug-r9-G) to short-
    /// circuit a doomed strategy attempt.</summary>
    public static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(3);

    /// <summary>Tier1 threshold — % of probes that must pass for a strategy
    /// to be declared "confirmed".</summary>
    public const int Tier1MinPassPercent = 70;

    /// <summary>Tier2 threshold — % of probes for "partial" verdict. Below
    /// this → Tier3 fail.</summary>
    public const int Tier2MinPassPercent = 30;

    public sealed record AttemptResult(
        string StrategyName,
        AttemptTier Tier,
        int PassCount,
        int TotalCount,
        TimeSpan Elapsed,
        string? Diagnostic = null);

    public enum AttemptTier
    {
        /// <summary>>=70 % of probes succeeded — strategy works.</summary>
        Tier1Confirmed,
        /// <summary>30-70 % succeeded — partial bypass (usable but degraded).</summary>
        Tier2Partial,
        /// <summary>HEAD probes failed at <30 % but winws.exe stayed up.</summary>
        Tier3Failed,
        /// <summary>winws.exe died fast (Bug-r9-G AV/syntax).</summary>
        ImmediateExit,
        /// <summary>Probe couldn't reach ANYTHING — likely no internet, abort sweep.</summary>
        NoSignal,
    }

    public sealed record SweepResult(
        string? WinningStrategy,
        AttemptTier WinningTier,
        int WinningPassCount,
        int WinningTotalCount,
        IReadOnlyList<AttemptResult> Attempts,
        bool NoSignal);

    public sealed record ProgressUpdate(
        int AttemptIndex,
        int TotalAttempts,
        string StrategyName,
        AttemptPhase Phase,
        int CurrentPassCount = 0,
        int CurrentTotalCount = 0);

    public enum AttemptPhase
    {
        Starting,
        Soaking,
        Probing,
        Stopping,
        Succeeded,
        Failed,
    }

    /// <summary>
    /// Run the probe loop. Each attempt: start strategy → race
    /// ImmediateExit vs soak → multi-target HEAD probe → classify tier.
    /// On Tier1+Tier2 — winner stays running, sweep returns the name.
    /// </summary>
    public static async Task<SweepResult> ProbeAsync(
        IReadOnlyList<string> candidateStrategies,
        IReadOnlyCollection<string>? availableStrategyNames,
        Func<string, Task> startStrategy,
        Func<Task> stopStrategy,
        Func<Task> immediateExitTrigger,
        HttpClient httpClient,
        IProgress<ProgressUpdate>? progress,
        ILogger? logger,
        CancellationToken ct)
    {
        var attempts = new List<AttemptResult>(capacity: candidateStrategies.Count);

        // Filter the seed list against what's actually installed.
        var available = availableStrategyNames is null
            ? null
            : new HashSet<string>(availableStrategyNames, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<string>(capacity: candidateStrategies.Count);
        foreach (var name in candidateStrategies)
        {
            if (available is null || available.Contains(name))
                resolved.Add(name);
        }

        if (resolved.Count == 0)
        {
            logger?.Warning("[ZapretAutoStrategy] No candidate strategies available; sweep aborted");
            return new SweepResult(null, AttemptTier.Tier3Failed, 0, 0, attempts, NoSignal: false);
        }

        // Load probe targets — prefer Flowseal's targets.txt if it exists
        // (lets community curation flow through to us). Fall back to hardcoded.
        var targets = LoadTargets(logger);
        logger?.Information("[ZapretAutoStrategy] Probe targets: {Count} URLs", targets.Count);

        for (int i = 0; i < resolved.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = resolved[i];
            var attemptStart = DateTime.UtcNow;
            logger?.Information("[ZapretAutoStrategy] Attempt {Index}/{Total}: {Strategy}",
                i + 1, resolved.Count, name);

            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Starting));

            try { await startStrategy(name).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[ZapretAutoStrategy] startStrategy({Strategy}) threw — skipping", name);
                attempts.Add(new AttemptResult(name, AttemptTier.Tier3Failed, 0, targets.Count,
                    DateTime.UtcNow - attemptStart, $"start_threw: {ex.Message}"));
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                continue;
            }

            // Race immediate-exit detection against the soak window.
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Soaking));
            var immediateExitTask = immediateExitTrigger();
            var soakTask = Task.Delay(SoakDelay, ct);
            var raced = await Task.WhenAny(immediateExitTask, soakTask).ConfigureAwait(false);

            if (raced == immediateExitTask)
            {
                logger?.Warning("[ZapretAutoStrategy] {Strategy}: immediate exit (Bug-r9-G)", name);
                attempts.Add(new AttemptResult(name, AttemptTier.ImmediateExit, 0, targets.Count,
                    DateTime.UtcNow - attemptStart, "winws_immediate_exit"));
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                continue;
            }

            // Multi-target probe.
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Probing));
            var probeReport = await ProbeAllTargetsAsync(targets, httpClient, logger, ct).ConfigureAwait(false);

            var passPercent = targets.Count == 0 ? 0 : (probeReport.PassCount * 100) / targets.Count;
            var tier = ClassifyTier(probeReport, targets.Count, passPercent);

            logger?.Information(
                "[ZapretAutoStrategy] {Strategy}: {Pass}/{Total} probes ok ({Percent}%) -> {Tier}",
                name, probeReport.PassCount, targets.Count, passPercent, tier);

            attempts.Add(new AttemptResult(name, tier, probeReport.PassCount, targets.Count,
                DateTime.UtcNow - attemptStart));

            if (tier == AttemptTier.NoSignal)
            {
                logger?.Warning("[ZapretAutoStrategy] NoSignal — likely offline, abort sweep");
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Stopping,
                    probeReport.PassCount, targets.Count));
                try { await stopStrategy().ConfigureAwait(false); } catch { /* defensive */ }
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                return new SweepResult(null, tier, probeReport.PassCount, targets.Count, attempts, NoSignal: true);
            }

            if (tier == AttemptTier.Tier1Confirmed || tier == AttemptTier.Tier2Partial)
            {
                // Winner — keep running. Surface counts in the progress event
                // so the air-pill text can render N/8.
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Succeeded,
                    probeReport.PassCount, targets.Count));
                return new SweepResult(name, tier, probeReport.PassCount, targets.Count, attempts, NoSignal: false);
            }

            // Tier3 — strategy doesn't bypass DPI here. Stop, escalate.
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Stopping,
                probeReport.PassCount, targets.Count));
            try { await stopStrategy().ConfigureAwait(false); }
            catch (Exception ex) { logger?.Warning(ex, "[ZapretAutoStrategy] stopStrategy() threw"); }
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));

            // Brief WinDivert handle release window between strategies.
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return new SweepResult(null, AttemptTier.Tier3Failed, 0, targets.Count, attempts, NoSignal: false);
    }

    /// <summary>Aggregated outcome of the per-strategy multi-target probe.</summary>
    public sealed record ProbeReport(int PassCount, int FailCount, int NoSignalCount);

    /// <summary>
    /// Probe all targets in parallel via HEAD. Each target counts as:
    /// pass (HTTP < 500), fail (timeout/TLS error/etc), or nosignal (network
    /// down). NoSignalCount > targets.Count*0.6 → likely offline.
    /// </summary>
    public static async Task<ProbeReport> ProbeAllTargetsAsync(
        IReadOnlyList<string> targets,
        HttpClient httpClient,
        ILogger? logger,
        CancellationToken ct)
    {
        var tasks = targets.Select(url => ProbeOneTargetAsync(url, httpClient, logger, ct)).ToArray();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        int pass = 0, fail = 0, noSignal = 0;
        foreach (var o in outcomes)
        {
            switch (o)
            {
                case ProbeOutcome.Success: pass++; break;
                case ProbeOutcome.Failed: fail++; break;
                case ProbeOutcome.NoSignal: noSignal++; break;
            }
        }
        return new ProbeReport(pass, fail, noSignal);
    }

    /// <summary>Single HEAD probe.</summary>
    public static async Task<ProbeOutcome> ProbeOneTargetAsync(
        string url,
        HttpClient httpClient,
        ILogger? logger,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            };
            using var resp = await httpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code >= 200 && code < 500) return ProbeOutcome.Success;
            return ProbeOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ProbeOutcome.NoSignal;
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome.Failed;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException sx
            && (sx.SocketErrorCode == System.Net.Sockets.SocketError.NetworkDown
                || sx.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable
                || sx.SocketErrorCode == System.Net.Sockets.SocketError.HostUnreachable))
        {
            return ProbeOutcome.NoSignal;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretAutoStrategy] Probe failed for {Url}", url);
            return ProbeOutcome.Failed;
        }
    }

    public enum ProbeOutcome
    {
        Success,
        Failed,
        NoSignal,
    }

    /// <summary>
    /// Tier classifier — turns per-attempt pass/fail counts into the verdict.
    /// </summary>
    private static AttemptTier ClassifyTier(ProbeReport report, int totalTargets, int passPercent)
    {
        // If ALL probes report NoSignal — no internet at all, abort.
        if (totalTargets > 0 && report.NoSignalCount >= (int)(totalTargets * 0.6))
            return AttemptTier.NoSignal;

        if (passPercent >= Tier1MinPassPercent) return AttemptTier.Tier1Confirmed;
        if (passPercent >= Tier2MinPassPercent) return AttemptTier.Tier2Partial;
        return AttemptTier.Tier3Failed;
    }

    /// <summary>
    /// Try to load probe targets from Flowseal's installed `utils/targets.txt`
    /// (per `targets.txt` format: KeyName = "https://..." lines, # comments).
    /// Falls back to hardcoded DefaultProbeTargets if file missing / unparseable.
    /// Lets community curation flow through to our probe.
    /// </summary>
    public static IReadOnlyList<string> LoadTargets(ILogger? logger)
    {
        try
        {
            var path = Path.Combine(ZapretUpdater.ZapretDir, "utils", "targets.txt");
            if (!File.Exists(path))
            {
                logger?.Debug("[ZapretAutoStrategy] targets.txt not found, using built-in defaults");
                return DefaultProbeTargets;
            }

            var lines = File.ReadAllLines(path);
            var result = new List<string>(capacity: 16);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                // Format: KeyName = "https://host"  OR  KeyName = "PING:1.2.3.4"
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var value = line.Substring(eq + 1).Trim().Trim('"');
                // Skip ping-only entries (we don't do ICMP probes — too noisy on
                // censored networks anyway) and keep only HTTPS targets.
                if (value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(value);
            }

            if (result.Count == 0)
            {
                logger?.Warning("[ZapretAutoStrategy] targets.txt yielded 0 URLs, falling back to defaults");
                return DefaultProbeTargets;
            }

            // Cap to 12 to keep probe-time bounded even if upstream targets.txt
            // grows. The first ~12 entries are Discord/YouTube/Google which are
            // the highest-signal DPI targets anyway.
            if (result.Count > 12) result = result.Take(12).ToList();
            return result;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretAutoStrategy] Failed to read targets.txt, using defaults");
            return DefaultProbeTargets;
        }
    }
}
