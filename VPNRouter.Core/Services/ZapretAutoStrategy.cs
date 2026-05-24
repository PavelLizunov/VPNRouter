// =============================================================================
// ZapretAutoStrategy — v2.36.0-r8 MVP one-button auto-probe orchestrator.
//
// Per `plans/research-one-button-zapret-deep-2026-05-24.md` §3.3, option E
// (Hybrid). The companion methodology doc
// `plans/research-zapret-auto-probe-methodology-2026-05-24.md` covers the
// probe-set rationale and tier schema in depth.
//
// WHY:
//   Zapret strategy effectiveness is empirical — what works on Beeline RU
//   may not work on Rostelecom. Today the user picks blindly from an 18-item
//   ComboBox. This orchestrator tries the 3 highest-hit-rate Flowseal presets
//   in sequence, runs an HTTP HEAD probe against a known DPI-blocked target
//   (youtube.com), and persists the winner.
//
// DESIGN:
//   - Substrate-agnostic. Takes `start` + `stop` + `wait-for-immediate-exit`
//     delegates. Doesn't know whether it's driving Flowseal/winws.exe or a
//     future zapret2/winws2.exe. Migration to zapret2 = swap the delegates,
//     keep the orchestrator. Per the migration research, this is the
//     long-term insurance for `plans/research-zapret2-bolvan-migration-2026-05-24.md`.
//
//   - Default candidate order: `general (ALT3)` → `general` → `general (ALT)`.
//     ALT3 is already sort-pinned first by `ZapretUpdater.ParseStrategies`
//     (score 0 at line 679-683). The other two are the highest-population
//     fallbacks per Flowseal community feedback.
//
//   - Per-strategy budget: ~35 s (2 s spawn + warmup + ~30 s soak + 1 s stop).
//     ImmediateExitDetected (Bug-r9-G) short-circuits to ~5 s when the
//     strategy crashes fast (AV quarantine or syntax error).
//
//   - Probe URL: HEAD https://www.youtube.com/ with 5 s timeout. Success on
//     HTTP 2xx/3xx (Google sometimes redirects to consent.youtube.com on
//     fresh sessions — still a "got through DPI" signal). HEAD with no body
//     fetch saves bytes. Cache-Control: no-cache to dodge residual cache.
//
// NOT IN SCOPE (deferred to v2.37 polish):
//   - Per-ISP catalog (option D in research §3.1). Seed order would be
//     informed by user's ASN; here we use the static default.
//   - Multi-target probe (youtube + discord + 4pda). MVP single target.
//   - Tier classification with Partial/Confirmed badges. MVP is binary
//     pass/fail.
//   - Result caching (`%ProgramData%\VPNRouter\cache\zapret_probe.json`).
//     MVP re-probes every time. User explicit strategy override still
//     bypasses the loop.
// =============================================================================

using System;
using System.Collections.Generic;
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
    /// Default seed order, mirroring ZapretUpdater.ParseStrategies sort
    /// heuristic. ALT3 first per Flowseal community wiki ("the one that
    /// works on Rostelecom/MegaFon residential"). Fallbacks are the
    /// runner-ups by population.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSeedOrder = new[]
    {
        "general (ALT3)",
        "general",
        "general (ALT)",
    };

    /// <summary>
    /// HEAD-probe target. YouTube SNI is the canonical TSPU-blocked test
    /// host in the RU footprint — if it resolves, DPI bypass demonstrably
    /// works on the current link. Cache-busted via Cache-Control header so
    /// residual DoH/system-resolver responses don't false-positive.
    /// </summary>
    public const string ProbeUrl = "https://www.youtube.com/";

    /// <summary>Per-strategy soak before probing. WinDivert binds in ~1 s;
    /// 30 s gives the user's browser/Discord a chance to actually push traffic
    /// through the new filter so any half-broken strategy surfaces.</summary>
    public static readonly TimeSpan SoakDelay = TimeSpan.FromSeconds(30);

    /// <summary>HEAD probe timeout. If we're past 5 s, the strategy isn't working —
    /// real successes are sub-second.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Wait window for ImmediateExitDetected to short-circuit a doomed
    /// strategy attempt. Bug-r9-G's own 2 s window plus a safety margin.</summary>
    public static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Result of a single strategy attempt. <see cref="Tier"/> follows the
    /// methodology doc's 4-tier ladder; MVP only emits Tier1 / Tier3 /
    /// NoSignal — Tier2 (Partial) is deferred to multi-target polish.
    /// </summary>
    public sealed record AttemptResult(
        string StrategyName,
        AttemptTier Tier,
        TimeSpan Elapsed,
        string? Diagnostic = null);

    public enum AttemptTier
    {
        /// <summary>HEAD probe succeeded → strategy works.</summary>
        Tier1Confirmed,
        /// <summary>HEAD probe failed but winws.exe stayed up → DPI not bypassed.</summary>
        Tier3Failed,
        /// <summary>winws.exe died fast (Bug-r9-G AV/syntax) — strategy unviable here.</summary>
        ImmediateExit,
        /// <summary>Network down or canceled — abort full sweep.</summary>
        NoSignal,
    }

    /// <summary>
    /// Final sweep outcome — winning strategy or null on all-fail.
    /// </summary>
    public sealed record SweepResult(
        string? WinningStrategy,
        IReadOnlyList<AttemptResult> Attempts,
        bool NoSignal);

    /// <summary>
    /// Progress event payload — emitted before each attempt so the VM can
    /// update the hero "Тестирую стратегию (i/N): name" chip.
    /// </summary>
    public sealed record ProgressUpdate(
        int AttemptIndex,
        int TotalAttempts,
        string StrategyName,
        AttemptPhase Phase);

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
    /// Run the probe loop. Each attempt: call <paramref name="startStrategy"/>,
    /// observe <paramref name="immediateExitTrigger"/> for fast-fail, soak
    /// for <see cref="SoakDelay"/>, HEAD-probe <see cref="ProbeUrl"/>, then
    /// call <paramref name="stopStrategy"/>. On Tier1 — return the winner
    /// WITHOUT stopping (caller wants the strategy to keep running).
    /// </summary>
    /// <param name="candidateStrategies">Names to probe, in priority order.
    /// Caller filters from full strategy list — if a name isn't available,
    /// it's silently skipped.</param>
    /// <param name="availableStrategyNames">Set of strategy names actually
    /// installed. Used to filter the seed order. Null means "trust all
    /// candidates".</param>
    /// <param name="startStrategy">Spawn winws.exe with the named strategy.
    /// Throws on failure. Substrate-agnostic — caller binds to Flowseal
    /// .bat invocation today, zapret2 Lua name tomorrow.</param>
    /// <param name="stopStrategy">Stop winws.exe cleanly. Called between
    /// attempts and on overall fail. NOT called on Tier1 success — caller
    /// wants the winner to stay running.</param>
    /// <param name="immediateExitTrigger">Returns a task that completes when
    /// the started winws.exe exits within <see cref="ImmediateExitWindow"/>.
    /// Returns a never-completing task when the trigger doesn't fire (the
    /// orchestrator races it against the soak delay). Caller wires this to
    /// the existing Bug-r9-G <c>ZapretManager.ImmediateExitDetected</c>
    /// event with a per-attempt TaskCompletionSource.</param>
    /// <param name="httpClient">HTTP client for the probe. Caller provides
    /// to centralize timeout / DNS / DoH policy. Pass a fresh one if
    /// no shared client available.</param>
    /// <param name="progress">Optional progress reporter — emits one update
    /// per Phase transition.</param>
    /// <param name="logger">For diagnostic logging.</param>
    /// <param name="ct">Cancellation. Honored in soak, probe, stop.</param>
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

        // Filter the seed list against what's actually installed. If we have no
        // installed match, treat as immediate all-fail rather than blindly
        // calling startStrategy with an unknown name.
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
            return new SweepResult(null, attempts, NoSignal: false);
        }

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
                attempts.Add(new AttemptResult(name, AttemptTier.Tier3Failed,
                    DateTime.UtcNow - attemptStart, $"start_threw: {ex.Message}"));
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                continue;
            }

            // Race immediate-exit detection against the soak window. If the
            // ImmediateExitDetected event fires (Bug-r9-G — winws.exe died
            // within 2 s, almost always AV quarantine), abort this strategy
            // fast instead of waiting the full 30 s for a doomed soak.
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Soaking));
            var immediateExitTask = immediateExitTrigger();
            var soakTask = Task.Delay(SoakDelay, ct);
            var raced = await Task.WhenAny(immediateExitTask, soakTask).ConfigureAwait(false);

            if (raced == immediateExitTask)
            {
                logger?.Warning("[ZapretAutoStrategy] {Strategy}: immediate exit detected (Bug-r9-G), abort", name);
                attempts.Add(new AttemptResult(name, AttemptTier.ImmediateExit,
                    DateTime.UtcNow - attemptStart, "winws_immediate_exit"));
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                // No stopStrategy() — the process already self-killed.
                continue;
            }

            // winws.exe survived the soak. Run the HEAD probe.
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Probing));
            var probeOk = await ProbeOnceAsync(httpClient, logger, ct).ConfigureAwait(false);

            if (probeOk == ProbeOutcome.Success)
            {
                logger?.Information("[ZapretAutoStrategy] {Strategy} CONFIRMED (Tier1)", name);
                attempts.Add(new AttemptResult(name, AttemptTier.Tier1Confirmed,
                    DateTime.UtcNow - attemptStart));
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Succeeded));
                return new SweepResult(name, attempts, NoSignal: false);
            }

            if (probeOk == ProbeOutcome.NoSignal)
            {
                logger?.Warning("[ZapretAutoStrategy] Probe NoSignal — likely offline or canceled, abort sweep");
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Stopping));
                try { await stopStrategy().ConfigureAwait(false); } catch { /* defensive */ }
                attempts.Add(new AttemptResult(name, AttemptTier.NoSignal,
                    DateTime.UtcNow - attemptStart, "probe_nosignal"));
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                return new SweepResult(null, attempts, NoSignal: true);
            }

            // Probe failed but winws.exe is up — strategy doesn't bypass DPI on
            // this link. Stop and move to the next candidate.
            logger?.Information("[ZapretAutoStrategy] {Strategy}: probe failed, escalating", name);
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Stopping));
            try { await stopStrategy().ConfigureAwait(false); }
            catch (Exception ex) { logger?.Warning(ex, "[ZapretAutoStrategy] stopStrategy() threw"); }

            attempts.Add(new AttemptResult(name, AttemptTier.Tier3Failed,
                DateTime.UtcNow - attemptStart, "probe_failed"));
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));

            // Brief breathing room for WinDivert kernel handle release before
            // next attempt spawns. Per research §6 R1.
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        // All candidates exhausted.
        return new SweepResult(null, attempts, NoSignal: false);
    }

    /// <summary>
    /// Single HEAD probe against the canonical DPI-blocked target.
    /// </summary>
    public static async Task<ProbeOutcome> ProbeOnceAsync(
        HttpClient httpClient,
        ILogger? logger,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, ProbeUrl);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            };

            using var resp = await httpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);

            // Any non-error HTTP status proves the TLS handshake completed and
            // the server answered. 2xx/3xx are normal; 403 happens on bot
            // detection but still proves the connection got through DPI; 451
            // (Unavailable For Legal Reasons) is what some DPI proxies return
            // when they don't outright reset — also a "we reached an endpoint"
            // signal even if YouTube is unhappy.
            var code = (int)resp.StatusCode;
            if (code >= 200 && code < 500)
            {
                logger?.Debug("[ZapretAutoStrategy] Probe OK: HTTP {Code}", code);
                return ProbeOutcome.Success;
            }
            logger?.Debug("[ZapretAutoStrategy] Probe got HTTP {Code} — counted as failure", code);
            return ProbeOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ProbeOutcome.NoSignal;
        }
        catch (OperationCanceledException)
        {
            // Timed out — DPI ate the handshake or hung the TCP stream.
            logger?.Debug("[ZapretAutoStrategy] Probe timeout");
            return ProbeOutcome.Failed;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException sx
            && (sx.SocketErrorCode == System.Net.Sockets.SocketError.NetworkDown
                || sx.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable
                || sx.SocketErrorCode == System.Net.Sockets.SocketError.HostUnreachable))
        {
            logger?.Warning("[ZapretAutoStrategy] Probe socket error {Code} — interface down", sx.SocketErrorCode);
            return ProbeOutcome.NoSignal;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretAutoStrategy] Probe failed");
            return ProbeOutcome.Failed;
        }
    }

    public enum ProbeOutcome
    {
        Success,
        Failed,
        NoSignal,
    }
}
