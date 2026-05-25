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

    // ─────────────────────────────────────────────────────────────────────────
    // v2.37.0-r3 — delegate-to-Flowseal probe (the canonical, slow, accurate
    // path). Spawns `utils/test zapret.ps1` hidden, pipes "2\n1\n" stdin to
    // auto-answer (DPI checkers mode + all configs), streams stdout to parse
    // per-config progress + final "Best config: X" winner.
    //
    // WHY: r1/r2 HTTP HEAD probe is too lenient — HTTP HEAD 200 OK can pass
    // even when DPI mangles the actual TLS stream afterwards. Flowseal's DPI
    // checker (mode 2) does TCP-byte-level analysis detecting the "16-20
    // freeze" pattern that's signature of DPI. Takes 2-5 minutes per full
    // sweep but the verdict is trustworthy.
    //
    // USER (2026-05-25): «у тебя прошел очень быстро, через bat файл занимает
    // минуты времени» — that's the cost of accuracy.
    //
    // Hidden console (CreateNoWindow + WindowStyle.Hidden + UseShellExecute
    // false) — user never sees the powershell window; only the in-app progress
    // chip "Тестирую (5/20): general (FAKE TLS AUTO ALT2)…".
    //
    // Caller responsibilities:
    //   - Cancellation: CancellationToken kills the powershell process.
    //   - On success: caller invokes startStrategy(winner) to apply.
    //   - On failure (null winner): caller surfaces fallback UI.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Result of one Flowseal script sweep.
    /// </summary>
    /// <remarks>
    /// r4 added <paramref name="Diagnostic"/> and <paramref name="ErrorLines"/>:
    /// - Diagnostic carries a short token explaining short-circuit reasons
    ///   ("not_admin", "not_windows", "sweep_timeout", "missing_script") so
    ///   the ViewModel can surface a specific toast instead of generic "no
    ///   strategy matched". Empty/null for the happy path.
    /// - ErrorLines is a ring-buffered tail of the last 8 [ERROR]/[WARN]
    ///   lines the Flowseal script emitted on stdout. Useful for surfacing
    ///   "zapret service installed", "curl missing", "DPI suite fetch failed"
    ///   conditions instead of swallowing them in debug log.
    /// Both fields default to empty for back-compat with r3 callers.
    /// </remarks>
    public sealed record FlowsealSweepResult(
        string? Winner,
        int TestedCount,
        int TotalCount,
        string FullOutput,
        string? Diagnostic = null,
        IReadOnlyList<string>? ErrorLines = null,
        // r37 — per-strategy results captured during the sweep. Each strategy
        // that produced at least one status line ends up here. Allows the
        // Hero ComboBox to badge every probed strategy, not just the winner.
        // Null/empty when sweep failed before any strategy completed.
        IReadOnlyDictionary<string, ZapretStrategyTestResult>? PerStrategyResults = null);

    /// <summary>
    /// Run Flowseal's `utils/test zapret.ps1` with auto-answers (mode=DPI,
    /// configs=all), parse stdout for per-config progress + final winner.
    /// Returns null winner if all configs failed.
    /// </summary>
    /// <param name="zapretInstallDir">Where Flowseal is installed (parent of utils/).</param>
    /// <param name="progress">Per-config progress reporter: emits as each
    /// `[N/M] strategy.bat` line is parsed from stdout.</param>
    /// <param name="logger">For diagnostic logging.</param>
    /// <param name="ct">Cancellation — kills the powershell process.</param>
    public static async Task<FlowsealSweepResult> RunFlowsealProbeAsync(
        string zapretInstallDir,
        IProgress<FlowsealProgress>? progress,
        ILogger? logger,
        CancellationToken ct)
    {
        // C.3 (r4): admin pre-check. Flowseal's `test zapret.ps1` exits
        // immediately on non-admin with [ERROR] Run as Administrator. If
        // we don't surface that, the user sees "no strategy matched" with
        // no clue why. Returning a typed Diagnostic="not_admin" lets the
        // ViewModel render a specific localized error toast instead.
        //
        // Our desktop app already requires admin for TUN setup so this is
        // double-coverage — defensive against UAC quirks / scheduled
        // task / service-mode restarts that could lose elevation.
        if (!OperatingSystem.IsWindows())
        {
            logger?.Information("[ZapretAutoStrategy] Flowseal probe only runs on Windows");
            return new FlowsealSweepResult(null, 0, 0, "Flowseal probe is Windows-only",
                Diagnostic: "not_windows", ErrorLines: Array.Empty<string>());
        }

        if (!IsRunningAsAdmin())
        {
            logger?.Warning("[ZapretAutoStrategy] Cannot run Flowseal probe — process not elevated");
            return new FlowsealSweepResult(null, 0, 0, "Process not elevated",
                Diagnostic: "not_admin", ErrorLines: Array.Empty<string>());
        }

        var scriptPath = Path.Combine(zapretInstallDir, "utils", "test zapret.ps1");
        if (!File.Exists(scriptPath))
        {
            logger?.Warning("[ZapretAutoStrategy] Flowseal test zapret.ps1 not found at {Path}", scriptPath);
            return new FlowsealSweepResult(null, 0, 0, $"missing: {scriptPath}",
                Diagnostic: "missing_script", ErrorLines: Array.Empty<string>());
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = zapretInstallDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        var outputBuilder = new System.Text.StringBuilder();
        string? winner = null;
        int testedCount = 0;
        int totalCount = 0;

        // r4 Part A — live X/Y status counter state. Each config-header line
        // ("  [N/M] strategy.bat") resets these to 0; each per-test status
        // line ("[TargetId][HTTP|TLS1.2|TLS1.3] code=... status=OK|FAIL|...")
        // increments TotalChecks; status=OK additionally increments OkCount.
        // The progress event fires on every status line so the UI lede can
        // render "Тестирую (5/20): general (ALT3) · 12/18" with sub-second
        // updates instead of waiting for the next config header.
        //
        // Locked behind a sync object — Process.OutputDataReceived can fire
        // on the threadpool. Mutations of int fields are atomic on x64 but
        // we still need the (ok, total) pair to be coherent for the progress
        // snapshot.
        int currentOkCount = 0;
        int currentTotalChecks = 0;
        var counterLock = new object();

        // r33: track current strategy name so early-winner detection can
        // record the winner. Flowseal's "Best config:" line comes at the
        // very end after iterating EVERY strategy — that's the 2-7 min
        // wait user complained about. Early-exit kills the script as
        // soon as a strategy aces enough targets to be confident.
        string currentStrategyName = string.Empty;
        bool earlyWinnerKilled = false;

        // r37: per-strategy results table. Filled out as each strategy
        // completes (the NEXT configHeaderRx match closes out the
        // previous one). Also finalized once after the loop ends to
        // capture the very last strategy's score.
        var perStrategyResults = new Dictionary<string, ZapretStrategyTestResult>(
            StringComparer.Ordinal);
        var perStrategyLock = new object();

        // r4 C.4 — error-line ring buffer. Captures the last 8 [ERROR]/
        // [WARN]/[WARNING] lines for surface to the user via toast when
        // sweep returns no winner. Bounded so a chatty script can't OOM.
        var errorLines = new List<string>(capacity: 8);

        // Pre-compiled regex hot path — these fire on every stdout line.
        var configHeaderRx = new System.Text.RegularExpressions.Regex(
            @"\[(\d+)/(\d+)\]\s+(.+?)(?:\.bat)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var statusLineRx = new System.Text.RegularExpressions.Regex(
            @"^\s*\[[^\]]+\]\[(?:HTTP|TLS1\.[23])\]\s.*?\bstatus=(\w+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var winnerRx = new System.Text.RegularExpressions.Regex(
            @"Best config:\s*(.+?)(?:\.bat)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var errorLineRx = new System.Text.RegularExpressions.Regex(
            @"^\s*\[(?:ERROR|WARN|WARNING)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        // Per-line stdout handler. Script writes via Write-Host so we get
        // each Write-Host invocation as one line on stdout.
        proc.OutputDataReceived += (sender, args) =>
        {
            if (args.Data == null) return;
            var line = args.Data;
            outputBuilder.AppendLine(line);

            // Per-config progress: "  [12/20] general (ALT5).bat" — resets
            // the running OK/total counters for the new config.
            var m = configHeaderRx.Match(line);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out var n)
                && int.TryParse(m.Groups[2].Value, out var t))
            {
                // r37: finalize previous strategy's result BEFORE resetting
                // counters. The very first configHeaderRx hit has empty
                // currentStrategyName so this skips cleanly.
                int prevOk, prevTotal;
                string prevName;
                lock (counterLock)
                {
                    prevOk = currentOkCount;
                    prevTotal = currentTotalChecks;
                    prevName = currentStrategyName;
                    currentOkCount = 0;
                    currentTotalChecks = 0;
                    testedCount = n;
                    totalCount = t;
                }
                if (!string.IsNullOrEmpty(prevName) && prevTotal > 0)
                {
                    lock (perStrategyLock)
                    {
                        perStrategyResults[prevName] = new ZapretStrategyTestResult
                        {
                            Passed = prevOk,
                            Total = prevTotal,
                            At = DateTime.UtcNow,
                        };
                    }
                }

                var strategy = m.Groups[3].Value.Trim();
                currentStrategyName = strategy;  // r33: remember for early-winner
                progress?.Report(new FlowsealProgress(n, t, strategy, 0, 0));
                return;
            }

            // Per-test status: "[YT_LIVE@0][HTTP] code=200 size=... status=OK"
            // Each config has multiple targets × 3 test labels — we count
            // status=OK as pass, everything else (FAIL/UNSUPPORTED/LIKELY_BLOCKED)
            // as a fail for the running score.
            var s = statusLineRx.Match(line);
            if (s.Success)
            {
                var status = s.Groups[1].Value;
                int snapOk, snapTotal, snapN, snapT;
                lock (counterLock)
                {
                    currentTotalChecks++;
                    if (status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                        currentOkCount++;
                    snapOk = currentOkCount;
                    snapTotal = currentTotalChecks;
                    snapN = testedCount;
                    snapT = totalCount;
                }
                if (snapN > 0 && snapT > 0)
                {
                    // Re-emit with same strategy name we don't have here;
                    // ViewModel keeps last-known StrategyName so empty is fine.
                    // Use empty-string strategy to signal "score update only".
                    progress?.Report(new FlowsealProgress(snapN, snapT, string.Empty, snapOk, snapTotal));
                }

                // r33: early-winner detection. If the current strategy has
                // ALL checks pass (100% OK) AND we've gathered enough samples
                // (>=16, typical strategy = 24 status lines: 8 targets × 3
                // test labels HTTP/TLS1.2/TLS1.3), declare it the winner
                // and kill the script. Saves user 2-7 min of waiting
                // through every remaining strategy that won't beat 100%.
                if (!earlyWinnerKilled
                    && snapOk == snapTotal
                    && snapTotal >= 16
                    && !string.IsNullOrEmpty(currentStrategyName))
                {
                    earlyWinnerKilled = true;
                    winner = currentStrategyName;
                    logger?.Information(
                        "[ZapretAutoStrategy] Early winner detected: {Strategy} ({Ok}/{Total}) — killing script",
                        winner, snapOk, snapTotal);
                    try { proc.Kill(entireProcessTree: true); }
                    catch (Exception ex)
                    {
                        logger?.Warning(ex, "[ZapretAutoStrategy] Early-kill threw (proc may already be dead)");
                    }
                }
                return;
            }

            // Error / warning lines — ring buffer (cap at 8).
            if (errorLineRx.IsMatch(line))
            {
                lock (counterLock)
                {
                    if (errorLines.Count >= 8) errorLines.RemoveAt(0);
                    errorLines.Add(line.Trim());
                }
                logger?.Debug("[ZapretAutoStrategy] flowseal-script: {Line}", line.Trim());
                return;
            }

            // Final winner: "Best config: general (ALT3).bat"
            var w = winnerRx.Match(line);
            if (w.Success)
            {
                winner = w.Groups[1].Value.Trim();
                logger?.Information("[ZapretAutoStrategy] Flowseal sweep winner: {Strategy}", winner);
            }
        };
        proc.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
                logger?.Debug("[ZapretAutoStrategy] flowseal-stderr: {Line}", args.Data);
        };

        logger?.Information("[ZapretAutoStrategy] Spawning Flowseal script: {Path}", scriptPath);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Auto-answer the two prompts: 2=DPI checker mode, 1=all configs.
        // Script reads via Read-Host; piping via stdin reaches it once it
        // calls Read-Host. Close stdin after answering so any subsequent
        // Read-Host fails cleanly instead of hanging.
        try
        {
            await proc.StandardInput.WriteLineAsync("2".AsMemory(), ct).ConfigureAwait(false);
            await proc.StandardInput.WriteLineAsync("1".AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretAutoStrategy] Failed to pipe Flowseal answers");
        }

        // r4 C.2 — hard timeout cap. Theoretical worst case is bounded by
        // script's per-test 5 s timeout × test-suite-count × config-count,
        // which can spiral past 10 min on flaky networks. We define a hard
        // 10-min cap and kill the process tree if breached. Reasonable user
        // patience ceiling per r3 release notes ("2-7 minutes typical").
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FlowsealMaxSweepTime);
        bool hitTimeout = false;
        string? timeoutDiagnostic = null;

        using (timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    // Distinguish user-cancel from timeout-cancel — only the
                    // latter is "abnormal" and worth surfacing as a diagnostic.
                    if (!ct.IsCancellationRequested)
                    {
                        hitTimeout = true;
                        logger?.Warning("[ZapretAutoStrategy] Flowseal sweep exceeded {Cap} cap — killing", FlowsealMaxSweepTime);
                    }
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* defensive — process may have finished */ }
        }))
        {
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (hitTimeout) timeoutDiagnostic = "sweep_timeout";
                else
                {
                    logger?.Information("[ZapretAutoStrategy] Flowseal sweep canceled by user");
                    IReadOnlyList<string> errSnap;
                    lock (counterLock) { errSnap = errorLines.ToArray(); }
                    return new FlowsealSweepResult(null, testedCount, totalCount, outputBuilder.ToString(),
                        Diagnostic: "canceled", ErrorLines: errSnap);
                }
            }
        }

        IReadOnlyList<string> finalErrors;
        lock (counterLock) { finalErrors = errorLines.ToArray(); }

        // r37: finalize the very last strategy's result (the loop only
        // records previous strategies when a NEW configHeaderRx matches,
        // so the last one — possibly the winner — needs an explicit close).
        int lastOk, lastTotal;
        string lastName;
        lock (counterLock)
        {
            lastOk = currentOkCount;
            lastTotal = currentTotalChecks;
            lastName = currentStrategyName;
        }
        if (!string.IsNullOrEmpty(lastName) && lastTotal > 0)
        {
            lock (perStrategyLock)
            {
                perStrategyResults[lastName] = new ZapretStrategyTestResult
                {
                    Passed = lastOk,
                    Total = lastTotal,
                    At = DateTime.UtcNow,
                };
            }
        }

        IReadOnlyDictionary<string, ZapretStrategyTestResult> perStrategySnap;
        lock (perStrategyLock)
        {
            perStrategySnap = new Dictionary<string, ZapretStrategyTestResult>(
                perStrategyResults, StringComparer.Ordinal);
        }

        logger?.Information(
            "[ZapretAutoStrategy] Flowseal sweep exited code={Code}, tested={N}/{Total}, winner={W}, errs={E}, perStrategy={S}",
            proc.HasExited ? proc.ExitCode : -1, testedCount, totalCount,
            winner ?? "<none>", finalErrors.Count, perStrategySnap.Count);

        return new FlowsealSweepResult(winner, testedCount, totalCount, outputBuilder.ToString(),
            Diagnostic: timeoutDiagnostic, ErrorLines: finalErrors,
            PerStrategyResults: perStrategySnap);
    }

    /// <summary>
    /// Hard cap on Flowseal sweep wall-time. Per r3 release notes the
    /// typical sweep is 2–7 min; anything beyond 10 min is a script bug or
    /// network catastrophe and should be aborted with a clear diagnostic.
    /// </summary>
    public static readonly TimeSpan FlowsealMaxSweepTime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Extended in r4: <paramref name="OkCount"/> and <paramref name="TotalChecks"/>
    /// carry the running per-config check tally so the UI lede can render
    /// «Тестирую (5/20): general (ALT3) · 12/18». r3 callers ignore the
    /// defaulted fields and stay correct.
    ///
    /// When the parser emits a "score-only update" (per-test status line
    /// processed but no new config header), <paramref name="StrategyName"/>
    /// is empty — the ViewModel keeps its last known strategy name.
    /// </summary>
    public sealed record FlowsealProgress(
        int CurrentIndex,
        int TotalCount,
        string StrategyName,
        int OkCount = 0,
        int TotalChecks = 0);

    /// <summary>
    /// True if the current process is running as a Windows administrator.
    /// Returns false on non-Windows (caller is expected to gate by OS).
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// r4 Part B — check for orphaned ipset switch flag left by an
    /// interrupted Flowseal sweep. The script writes `ipset_switched.flag`
    /// at zapret root after backing up `lists/ipset-all.txt` to
    /// `lists/ipset-all.test-backup.txt` and switching the live list to
    /// "any" mode. On graceful exit / Ctrl+C the script restores; our
    /// Process.Kill(entireProcessTree:true) bypasses both finally and trap
    /// so we adopt the safety net ourselves.
    /// </summary>
    public static bool HasOrphanedIpsetFlag(string zapretInstallDir)
    {
        try
        {
            var flagPath = Path.Combine(zapretInstallDir, "ipset_switched.flag");
            return File.Exists(flagPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// r4 Part B — restore `lists/ipset-all.txt` from the script's backup
    /// and delete the orphan flag. Idempotent: no-op if no flag exists.
    /// Matches Flowseal's `Set-IpsetMode -mode "restore"` semantics:
    /// Move backup over the live file (overwrite). If the backup is
    /// missing but the flag is present, leave the live file alone (don't
    /// guess) and just delete the stale flag — the script handles this
    /// case the same way on next run.
    /// </summary>
    public static void RestoreIpsetAfterKill(string zapretInstallDir, ILogger? logger)
    {
        try
        {
            var flagPath = Path.Combine(zapretInstallDir, "ipset_switched.flag");
            if (!File.Exists(flagPath))
            {
                return; // nothing to do — common case
            }

            var listsDir = Path.Combine(zapretInstallDir, "lists");
            var livePath = Path.Combine(listsDir, "ipset-all.txt");
            var backupPath = Path.Combine(listsDir, "ipset-all.test-backup.txt");

            if (File.Exists(backupPath))
            {
                File.Move(backupPath, livePath, overwrite: true);
                logger?.Information("[ZapretAutoStrategy] Restored orphaned ipset from prior probe interrupt");
            }
            else
            {
                logger?.Warning("[ZapretAutoStrategy] Orphan ipset flag present but no backup at {Path} — leaving live list alone", backupPath);
            }

            try { File.Delete(flagPath); }
            catch (Exception ex) { logger?.Warning(ex, "[ZapretAutoStrategy] Failed to delete orphan ipset flag"); }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretAutoStrategy] RestoreIpsetAfterKill failed");
        }
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
