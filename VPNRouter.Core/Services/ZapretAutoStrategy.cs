using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

public static class ZapretAutoStrategy
{
    public static readonly IReadOnlyList<string> DefaultProbeTargets = Array.AsReadOnly(new[]
    {
        "https://discord.com",
        "https://gateway.discord.gg",
        "https://cdn.discordapp.com",
        "https://updates.discord.com",
        "https://www.youtube.com",
        "https://youtu.be",
        "https://i.ytimg.com",
        "https://redirector.googlevideo.com",
    });

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SweepTimeout = TimeSpan.FromMinutes(10);

    internal static bool IsUsable(int passed, int total) => total > 0 && passed * 100 >= total * 30;

    internal static Func<bool> AnyWinwsRunning { get; set; } = () => ProcessQuery.AnyAlive("winws");

    internal static Func<HttpMessageHandler> CreateHandler { get; set; } = CreateDirectHandler;

    internal static SocketsHttpHandler CreateDirectHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
    };

    // Each candidate needs new TLS connections. A pool shared across strategies can mask a failed candidate.
    internal static async Task<ProbeOutcome[]> CheckTargetsAsync(ILogger? logger, CancellationToken ct)
    {
        using var client = new HttpClient(CreateHandler());
        return await Task.WhenAll(DefaultProbeTargets.Select(url => ProbeOneTargetAsync(url, client, logger, ct)))
            .ConfigureAwait(false);
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
            req.Headers.CacheControl = new CacheControlHeaderValue
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
            throw;
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
            logger?.Debug("[ZapretAutoStrategy] Probe failed for {Url} ({Error})", CanaryPolicy.RedactUrl(url), ex.GetType().Name);
            return ProbeOutcome.Failed;
        }
    }

    public enum ProbeOutcome
    {
        Success,
        Failed,
        NoSignal,
    }

    public sealed record StrategySweepResult(
        string? Winner,
        int TestedCount,
        int TotalCount,
        string? Diagnostic = null,
        IReadOnlyDictionary<string, ZapretStrategyTestResult>? PerStrategyResults = null,
        string? ProbeLogPath = null,
        bool EarlyWinner = false);

    internal static string? BestStrategyByScore(
        IReadOnlyDictionary<string, ZapretStrategyTestResult> perStrategy)
    {
        string? best = null;
        int bestPassed = 0;
        double bestRatio = -1;
        foreach (var kv in perStrategy)
        {
            if (!IsUsable(kv.Value.Passed, kv.Value.Total)) continue;
            double ratio = (double)kv.Value.Passed / kv.Value.Total;
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

    internal static Task<StrategySweepResult> RunInstalledProbeAsync(
        ZapretManager manager, IProgress<StrategyProgress>? progress, ILogger? logger, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !IsRunningAsAdmin())
            return Task.FromResult(new StrategySweepResult(null, 0, 0,
                Diagnostic: OperatingSystem.IsWindows() ? "not_admin" : "not_windows"));

        return ProbeInstalledStrategiesAsync(manager, progress, logger, StartupDelay, ct);
    }

    internal static async Task<StrategySweepResult> ProbeInstalledStrategiesAsync(
        ZapretManager manager, IProgress<StrategyProgress>? progress, ILogger? logger,
        TimeSpan startupDelay, CancellationToken ct)
    {
        var candidates = ZapretUpdater.ParseStrategies();
        if (!ZapretUpdater.IsInstalled() || candidates.Count == 0)
            return new StrategySweepResult(null, 0, candidates.Count, Diagnostic: "missing_package");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SweepTimeout);
        var token = deadline.Token;
        var scores = new Dictionary<string, ZapretStrategyTestResult>(StringComparer.Ordinal);
        string? diagnostic = null;
        int tested = 0;
        bool anyStarted = false;
        try
        {
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (manager.IsRunning || AnyWinwsRunning())
                {
                    diagnostic = "helper_active";
                    break;
                }
                tested++;
                progress?.Report(new StrategyProgress(tested, candidates.Count, candidate.Name));
                int passed = 0;
                try
                {
                    manager.StartFromBat(candidate.BatPath!, candidate.Arguments);
                    await Task.Delay(startupDelay, token);
                    if (manager.IsRunning)
                    {
                        anyStarted = true;
                        var outcomes = await CheckTargetsAsync(logger, token);
                        if (manager.IsRunning)
                            passed = outcomes.Count(result => result == ProbeOutcome.Success);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.Warning(ex, "[ZapretAutoStrategy] Could not test {Strategy}", candidate.Name);
                }
                finally
                {
                    manager.Stop();
                }

                token.ThrowIfCancellationRequested();
                if (manager.IsRunning || AnyWinwsRunning())
                {
                    diagnostic = "helper_active";
                    break;
                }
                int total = DefaultProbeTargets.Count;
                scores[candidate.Name] = new ZapretStrategyTestResult { Passed = passed, Total = total };
                progress?.Report(new StrategyProgress(tested, candidates.Count, string.Empty, passed, total));
                if (passed == total) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            diagnostic = ct.IsCancellationRequested ? "canceled" : "sweep_timeout";
        }

        if (!anyStarted && diagnostic == null) diagnostic = "missing_package";
        var winner = diagnostic == null ? BestStrategyByScore(scores) : null;
        var logPath = WriteProbeLog(scores, winner, diagnostic, logger);
        return new StrategySweepResult(winner, tested, candidates.Count, diagnostic,
            PerStrategyResults: scores, ProbeLogPath: logPath, EarlyWinner: winner != null && tested < candidates.Count);
    }

    private static string? WriteProbeLog(Dictionary<string, ZapretStrategyTestResult> scores,
        string? winner, string? diagnostic, ILogger? logger)
    {
        try
        {
            var path = Path.Combine(ZapretRuntimeDirectory.PrepareRoot(), $"zapret-probe-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var log = new StreamWriter(stream);
            log.WriteLine($"VPNRouter HTTPS probe {DateTime.UtcNow:O}");
            foreach (var (name, score) in scores)
                log.WriteLine($"{name}: {score.Passed}/{score.Total}");
            log.WriteLine($"Result: {winner ?? diagnostic ?? "no winner"}");
            return path;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretAutoStrategy] Could not save the probe log");
            return null;
        }
    }

    public sealed record StrategyProgress(
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
}
