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

public static class ZapretAutoStrategy
{
    public static readonly IReadOnlyList<string> DefaultProbeTargets = new[]
    {
        "https://discord.com",
        "https://gateway.discord.gg",
        "https://cdn.discordapp.com",
        "https://updates.discord.com",
        "https://www.youtube.com",
        "https://youtu.be",
        "https://i.ytimg.com",
        "https://redirector.googlevideo.com",
    };

    public static readonly TimeSpan SoakDelay = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(3);

    public const int Tier1MinPassPercent = 70;

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
        Tier1Confirmed,
        Tier2Partial,
        Tier3Failed,
        ImmediateExit,
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
                try { await stopStrategy().ConfigureAwait(false); } catch { }
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));
                return new SweepResult(null, tier, probeReport.PassCount, targets.Count, attempts, NoSignal: true);
            }

            if (tier == AttemptTier.Tier1Confirmed || tier == AttemptTier.Tier2Partial)
            {
                progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Succeeded,
                    probeReport.PassCount, targets.Count));
                return new SweepResult(name, tier, probeReport.PassCount, targets.Count, attempts, NoSignal: false);
            }

            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Stopping,
                probeReport.PassCount, targets.Count));
            try { await stopStrategy().ConfigureAwait(false); }
            catch (Exception ex) { logger?.Warning(ex, "[ZapretAutoStrategy] stopStrategy() threw"); }
            progress?.Report(new ProgressUpdate(i, resolved.Count, name, AttemptPhase.Failed));

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return new SweepResult(null, AttemptTier.Tier3Failed, 0, targets.Count, attempts, NoSignal: false);
    }

    public sealed record ProbeReport(int PassCount, int FailCount, int NoSignalCount);

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
            logger?.Debug(ex, "[ZapretAutoStrategy] Probe failed for {Url}", CanaryPolicy.RedactUrl(url));
            return ProbeOutcome.Failed;
        }
    }

    public enum ProbeOutcome
    {
        Success,
        Failed,
        NoSignal,
    }

    public sealed record FlowsealSweepResult(
        string? Winner,
        int TestedCount,
        int TotalCount,
        string FullOutput,
        string? Diagnostic = null,
        IReadOnlyList<string>? ErrorLines = null,
        IReadOnlyDictionary<string, ZapretStrategyTestResult>? PerStrategyResults = null,
        string? ProbeLogPath = null,
        bool EarlyWinner = false);

    internal static readonly System.Text.RegularExpressions.Regex ConfigHeaderRx =
        new(@"\[(\d+)/(\d+)\]\s+(.+?)(?:\.bat)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static readonly System.Text.RegularExpressions.Regex StatusLineRx =
        new(@"^\s*(?:\[[^\]]+\])?\[(?:HTTP|TLS1\.[23])\]\s.*?\bstatus=(\w+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static readonly System.Text.RegularExpressions.Regex WinnerRx =
        new(@"Best config:\s*(.+?)(?:\.bat)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static readonly System.Text.RegularExpressions.Regex ErrorLineRx =
        new(@"^\s*\[(?:ERROR|WARN|WARNING)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    internal const string SilentWrapperStrategyName = "_vpnrouter_silent";

    private static bool IsSilentWrapper(string? name) =>
        string.Equals(name?.Trim(), SilentWrapperStrategyName, StringComparison.OrdinalIgnoreCase);

internal static string? BestStrategyByScore(
        IReadOnlyDictionary<string, ZapretStrategyTestResult> perStrategy)
    {
        string? best = null;
        int bestPassed = 0;
        double bestRatio = -1;
        foreach (var kv in perStrategy)
        {
            if (kv.Value.Passed <= 0) continue;
            double ratio = kv.Value.Total > 0
                ? (double)kv.Value.Passed / kv.Value.Total : 0;
            if (kv.Value.Passed > bestPassed
                || (kv.Value.Passed == bestPassed && ratio > bestRatio))
            {
                best = kv.Key;
                bestPassed = kv.Value.Passed;
                bestRatio = ratio;
            }
        }
        return best;
    }

    internal static System.Diagnostics.ProcessStartInfo BuildFlowsealProbeStartInfo(
        string zapretInstallDir, string scriptPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = zapretInstallDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    public static async Task<FlowsealSweepResult> RunFlowsealProbeAsync(
        string zapretInstallDir,
        IProgress<FlowsealProgress>? progress,
        ILogger? logger,
        CancellationToken ct)
    {
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

        var psi = BuildFlowsealProbeStartInfo(zapretInstallDir, scriptPath);

        var probeLogPath = Path.Combine(AppPaths.LogsDir,
            $"zapret-probe-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        StreamWriter? probeLog = null;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            probeLog = new StreamWriter(probeLogPath, append: false,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            { AutoFlush = true };
            probeLog.WriteLine($"# Zapret probe log started at {DateTime.UtcNow:O}");
            probeLog.WriteLine($"# zapretInstallDir = {zapretInstallDir}");
            probeLog.WriteLine($"# scriptPath       = {scriptPath}");
            probeLog.WriteLine($"# VPNRouter        = {VPNRouter.Core.AppVersion.Version}");
            logger?.Information("[ZapretAutoStrategy] Probe log → {Path}", probeLogPath);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretAutoStrategy] Failed to open probe log (continuing without)");
        }
        var probeLogLock = new object();

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        var state = new FlowsealSweepState(progress, logger, probeLog, probeLogLock,
            () => proc.Kill(entireProcessTree: true));

        proc.OutputDataReceived += (sender, args) =>
        {
            if (args.Data == null) return;
            state.HandleLine(args.Data);
        };
        proc.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                logger?.Debug("[ZapretAutoStrategy] flowseal-stderr: {Line}", args.Data);
                if (probeLog != null)
                {
                    try
                    {
                        lock (probeLogLock)
                        {
                            probeLog.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [STDERR] {args.Data}");
                        }
                    }
                    catch { }
                }
            }
        };

        var preExistingWinwsPids = new HashSet<int>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("winws"))
            {
                try { preExistingWinwsPids.Add(p.Id); } catch { }
                p.Dispose();
            }
            logger?.Debug("[ZapretAutoStrategy] Pre-probe winws.exe PIDs: {N}", preExistingWinwsPids.Count);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretAutoStrategy] Pre-probe winws snapshot failed");
        }

        logger?.Information("[ZapretAutoStrategy] Spawning Flowseal script: {Path}", scriptPath);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

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
                    if (!ct.IsCancellationRequested)
                    {
                        hitTimeout = true;
                        logger?.Warning("[ZapretAutoStrategy] Flowseal sweep exceeded {Cap} cap — killing", FlowsealMaxSweepTime);
                    }
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
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
                    errSnap = state.SnapshotErrors();
                    CleanupOrphanWinws(preExistingWinwsPids, logger);
                    CloseProbeLog(probeLog, probeLogLock, "canceled", null, logger);
                    return new FlowsealSweepResult(null, state.TestedCount, state.TotalCount, state.Output,
                        Diagnostic: "canceled", ErrorLines: errSnap,
                        ProbeLogPath: probeLog != null ? probeLogPath : null,
                        EarlyWinner: false);
                }
            }
        }

        IReadOnlyList<string> finalErrors;
        finalErrors = state.SnapshotErrors();

        CleanupOrphanWinws(preExistingWinwsPids, logger);

        state.RecordCurrentStrategy();
        var perStrategySnap = state.SnapshotScores();

        if (string.IsNullOrEmpty(state.Winner))
        {
            var fallback = BestStrategyByScore(perStrategySnap);
            if (!string.IsNullOrEmpty(fallback))
            {
                state.Winner = fallback;
                logger?.Information(
                    "[ZapretAutoStrategy] No explicit winner line — promoting best-scoring strategy {Strategy} ({Ok}/{Total})",
                    fallback, perStrategySnap[fallback].Passed, perStrategySnap[fallback].Total);
            }
        }

        logger?.Information(
            "[ZapretAutoStrategy] Flowseal sweep exited code={Code}, tested={N}/{Total}, winner={W}, errs={E}, perStrategy={S}",
            proc.HasExited ? proc.ExitCode : -1, state.TestedCount, state.TotalCount,
            state.Winner ?? "<none>", finalErrors.Count, perStrategySnap.Count);

        CloseProbeLog(probeLog, probeLogLock,
            outcome: state.Winner != null ? "winner" : (timeoutDiagnostic ?? "no_winner"),
            winner: state.Winner,
            logger: logger);

        return new FlowsealSweepResult(state.Winner, state.TestedCount, state.TotalCount, state.Output,
            Diagnostic: timeoutDiagnostic, ErrorLines: finalErrors,
            PerStrategyResults: perStrategySnap,
            ProbeLogPath: probeLog != null ? probeLogPath : null,
            EarlyWinner: state.EarlyWinnerKilled);
    }

    private sealed class FlowsealSweepState
    {
        private readonly IProgress<FlowsealProgress>? _progress;
        private readonly Serilog.ILogger? _logger;
        private readonly StreamWriter? _probeLog;
        private readonly object _probeLogLock;
        private readonly Action _killScript;
        private readonly object _counterLock = new();
        private readonly object _perStrategyLock = new();
        private readonly System.Text.StringBuilder _output = new();
        private readonly Dictionary<string, ZapretStrategyTestResult> _perStrategy =
            new(StringComparer.Ordinal);
        private readonly List<string> _errorLines = new(capacity: 8);
        private int _currentOk;
        private int _currentTotal;
        private string _currentName = string.Empty;

        public FlowsealSweepState(
            IProgress<FlowsealProgress>? progress,
            Serilog.ILogger? logger,
            StreamWriter? probeLog,
            object probeLogLock,
            Action killScript)
        {
            _progress = progress;
            _logger = logger;
            _probeLog = probeLog;
            _probeLogLock = probeLogLock;
            _killScript = killScript;
        }

        public string? Winner { get; set; }

        public int TestedCount { get; private set; }

        public int TotalCount { get; private set; }

        public bool EarlyWinnerKilled { get; private set; }

        public string Output => _output.ToString();

        public void HandleLine(string line)
        {
        _output.AppendLine(line);
        if (_probeLog != null)
        {
            try
            {
                lock (_probeLogLock)
                {
                    _probeLog.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {line}");
                }
            }
            catch { }
        }

        var m = ConfigHeaderRx.Match(line);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var n)
            && int.TryParse(m.Groups[2].Value, out var t))
        {
            int prevOk, prevTotal;
            string prevName;
            lock (_counterLock)
            {
                prevOk = _currentOk;
                prevTotal = _currentTotal;
                prevName = _currentName;
                _currentOk = 0;
                _currentTotal = 0;
                TestedCount = n;
                TotalCount = t;
            }
            if (!string.IsNullOrEmpty(prevName) && prevTotal > 0)
            {
                lock (_perStrategyLock)
                {
                    _perStrategy[prevName] = new ZapretStrategyTestResult
                    {
                        Passed = prevOk,
                        Total = prevTotal,
                        At = DateTime.UtcNow,
                    };
                }
            }

            var strategy = m.Groups[3].Value.Trim();
            _currentName = strategy;
            _progress?.Report(new FlowsealProgress(n, t, strategy, 0, 0));
            return;
        }

        var s = StatusLineRx.Match(line);
        if (s.Success)
        {
            var status = s.Groups[1].Value;
            bool isPass = status.Equals("OK", StringComparison.OrdinalIgnoreCase)
                || status.Equals("UNSUPPORTED", StringComparison.OrdinalIgnoreCase);
            int snapOk, snapTotal, snapN, snapT;
            lock (_counterLock)
            {
                _currentTotal++;
                if (isPass)
                    _currentOk++;
                snapOk = _currentOk;
                snapTotal = _currentTotal;
                snapN = TestedCount;
                snapT = TotalCount;
            }
            if (snapN > 0 && snapT > 0)
            {
                _progress?.Report(new FlowsealProgress(snapN, snapT, string.Empty, snapOk, snapTotal));
            }

            if (!EarlyWinnerKilled
                && snapOk == snapTotal
                && snapTotal >= 16
                && !string.IsNullOrEmpty(_currentName))
            {
                EarlyWinnerKilled = true;
                Winner = _currentName;
                _logger?.Information(
                    "[ZapretAutoStrategy] Early winner detected: {Strategy} ({Ok}/{Total}) — killing script",
                    Winner, snapOk, snapTotal);
                if (_probeLog != null)
                {
                    try
                    {
                        lock (_probeLogLock)
                        {
                            _probeLog.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} # EARLY-WINNER {_currentName} ({snapOk}/{snapTotal}) — killing script, skipping remaining {snapT - snapN} configs");
                        }
                    }
                    catch { }
                }
                _progress?.Report(new FlowsealProgress(
                    snapN, snapT, _currentName, snapOk, snapTotal));
                try { _killScript(); }
                catch (Exception ex)
                {
                    _logger?.Warning(ex, "[ZapretAutoStrategy] Early-kill threw (proc may already be dead)");
                }
            }
            return;
        }

        if (ErrorLineRx.IsMatch(line))
        {
            lock (_counterLock)
            {
                if (_errorLines.Count >= 8) _errorLines.RemoveAt(0);
                _errorLines.Add(line.Trim());
            }
            _logger?.Debug("[ZapretAutoStrategy] flowseal-script: {Line}", line.Trim());
            return;
        }

        var w = WinnerRx.Match(line);
        if (w.Success)
        {
            Winner = w.Groups[1].Value.Trim();
            _logger?.Information("[ZapretAutoStrategy] Flowseal sweep winner: {Strategy}", Winner);
        
        }

        public IReadOnlyList<string> SnapshotErrors()
        {
            lock (_counterLock) return _errorLines.ToArray();
        }

        public void RecordCurrentStrategy()
        {
            int lastOk, lastTotal;
            string lastName;
            lock (_counterLock)
            {
                lastOk = _currentOk;
                lastTotal = _currentTotal;
                lastName = _currentName;
            }
            if (!string.IsNullOrEmpty(lastName) && lastTotal > 0)
            {
                lock (_perStrategyLock)
                {
                    _perStrategy[lastName] = new ZapretStrategyTestResult
                    {
                        Passed = lastOk,
                        Total = lastTotal,
                        At = DateTime.UtcNow,
                    };
                }
            }
        }

        public IReadOnlyDictionary<string, ZapretStrategyTestResult> SnapshotScores()
        {
            lock (_perStrategyLock)
                return new Dictionary<string, ZapretStrategyTestResult>(_perStrategy, StringComparer.Ordinal);
        }
    }

    private static void CleanupOrphanWinws(HashSet<int> preExistingPids, Serilog.ILogger? logger)
    {
        int killed = 0;
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("winws"))
            {
                try
                {
                    if (!preExistingPids.Contains(p.Id))
                    {
                        logger?.Debug("[ZapretAutoStrategy] Killing orphan winws PID {Pid}", p.Id);
                        p.Kill(entireProcessTree: true);
                        killed++;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, "[ZapretAutoStrategy] orphan winws kill failed PID {Pid}", p.Id);
                }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretAutoStrategy] CleanupOrphanWinws enumeration failed");
        }
        if (killed > 0)
            logger?.Information("[ZapretAutoStrategy] Cleaned up {N} orphan winws.exe", killed);
    }

    private static void CloseProbeLog(StreamWriter? probeLog, object probeLogLock,
        string outcome, string? winner, Serilog.ILogger? logger)
    {
        if (probeLog == null) return;
        try
        {
            lock (probeLogLock)
            {
                probeLog.WriteLine($"# Probe finished at {DateTime.UtcNow:O} — outcome={outcome}, winner={winner ?? "<none>"}");
                probeLog.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretAutoStrategy] probe-log close failed");
        }
    }

    public static readonly TimeSpan FlowsealMaxSweepTime = TimeSpan.FromMinutes(10);

    public sealed record FlowsealProgress(
        int CurrentIndex,
        int TotalCount,
        string StrategyName,
        int OkCount = 0,
        int TotalChecks = 0);

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

    public static bool HasOrphanedIpsetFlag(string zapretInstallDir)
    {
        try
        {
            var flagPath = Path.Combine(zapretInstallDir, "ipset_switched.flag");
            return File.Exists(flagPath);
        }
        catch { return false; }
    }

    public static void RestoreIpsetAfterKill(string zapretInstallDir, ILogger? logger)
    {
        try
        {
            var flagPath = Path.Combine(zapretInstallDir, "ipset_switched.flag");
            if (!File.Exists(flagPath))
            {
                return;
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

    private static AttemptTier ClassifyTier(ProbeReport report, int totalTargets, int passPercent)
    {
        if (totalTargets > 0 && report.NoSignalCount >= (int)(totalTargets * 0.6))
            return AttemptTier.NoSignal;

        if (passPercent >= Tier1MinPassPercent) return AttemptTier.Tier1Confirmed;
        if (passPercent >= Tier2MinPassPercent) return AttemptTier.Tier2Partial;
        return AttemptTier.Tier3Failed;
    }

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
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var value = line.Substring(eq + 1).Trim().Trim('"');
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
