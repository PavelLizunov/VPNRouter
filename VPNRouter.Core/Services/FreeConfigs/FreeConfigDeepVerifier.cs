using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

public sealed class FreeConfigDeepVerifier
{
    private readonly ILogger _logger;
    private readonly string _singBoxPath;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan WarmupPerConcurrencySlack = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan OverallTimeout = DeepVerifyConstants.OverallTimeout;

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    public int MaxConcurrency { get; set; } = 5;

    internal TimeSpan EffectiveSocksBindWait =>
        SingBoxWarmup + WarmupPerConcurrencySlack * Math.Max(0, MaxConcurrency - 1);

    public bool MeasureBandwidth { get; set; } = false;

    public FreeConfigDeepVerifier(ILogger logger)
    {
        _logger = logger;
        _policy = SingBoxRuntimePolicy.Current;
        _singBoxPath = _policy?.SelectedExecutablePath ?? AppPaths.SingBoxExePath;
    }

    public async Task VerifyBatchAsync(
        IReadOnlyCollection<FreeConfigEntry> configs,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var policy = EffectivePolicy;
        using var policyScope = SingBoxRuntimePolicy.EnterScope(policy);
        if (policy != null)
        {
            if (!policy.IsAvailable)
            {
                _logger.Warning("DeepVerify: sing-box binary unavailable under runtime policy");
                return;
            }
        }
        else if (!File.Exists(_singBoxPath))
        {
            _logger.Warning("DeepVerify: sing-box binary not found at {path}", _singBoxPath);
            return;
        }

        var sem = new SemaphoreSlim(MaxConcurrency);
        var total = configs.Count;
        var done = 0;

        var tasks = configs.Select(async cfg =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await VerifyOneAsync(cfg, ct);
            }
            finally
            {
                sem.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Report((n, total));
            }
        });

        await Task.WhenAll(tasks);
    }

    public async Task VerifyOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        var policy = EffectivePolicy;
        using var policyScope = SingBoxRuntimePolicy.EnterScope(policy);
        if (policy != null)
            policy.Authorize(SingBoxRuntimeOperation.Verify);

        cfg.LastTestedAt = DateTime.UtcNow;

        using var probeScope = DeepVerifyProbe.BeginProbeScope();

        var socksPort = NetPortUtil.FindFreePort();
        var clashPort = NetPortUtil.FindFreePort();
        string? tmpConfigPath = null;
        Process? process = null;
        var stderrBuffer = new System.Text.StringBuilder(
            DeepVerifyProbe.MaxDiagnosticBufferChars);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);

        var sw = Stopwatch.StartNew();
        var cc = cfg.CountryCode ?? "??";

        try
        {
            var vless = ServerUriParser.Parse(cfg.RawUri);
            var configJson = BuildSingleOutboundConfig(vless, socksPort, clashPort);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-verify-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tmpConfigPath, configJson, overallCts.Token);

            var startInfo = new ProcessStartInfo
            {
                FileName = policy != null ? policy.SelectedExecutablePath! : _singBoxPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(tmpConfigPath);

            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false,
            };
            process.ErrorDataReceived += (_, e) =>
                DeepVerifyProbe.AppendSanitizedLine(
                    stderrBuffer,
                    e.Data,
                    DeepVerifyProbe.MaxDiagnosticBufferChars);

            if (!process.Start())
            {
                cfg.LastError = "sing-box spawn failed";
                _logger.Warning("[DV] {host}:{port} [{cc}] → spawn failed", cfg.Host, cfg.Port, cc);
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!await DeepVerifyProbe.WaitForPortBoundAsync(socksPort, EffectiveSocksBindWait, overallCts.Token))
            {
                var stderrSnip = DeepVerifyProbe.ReadSanitizedSnippet(stderrBuffer, 300);
                cfg.LastError = $"sing-box didn't bind: {DeepVerifyProbe.TrimSnippet(stderrSnip, 80)}";
                _logger.Warning("[DV] {host}:{port} [{cc}] → didn't bind. stderr: {err}",
                    cfg.Host, cfg.Port, cc, stderrSnip);
                return;
            }

            var (httpOk, httpLatencyMs, httpErr) = await DeepVerifyProbe.ProbeViaSocksAsync(socksPort, HttpTimeout, overallCts.Token);

            if (httpOk)
            {
                cfg.Status = FreeConfigStatus.Verified;
                cfg.LastDeepVerifyAt = DateTime.UtcNow;
                if (cfg.LatencyMs == 0)
                {
                    cfg.LatencyMs = httpLatencyMs;
                }
                cfg.LastError = null;

                if (MeasureBandwidth)
                {
                    var (bwOk, mbps, bwErr) = await DeepVerifyProbe.MeasureBandwidthViaSocksAsync(socksPort, overallCts.Token);
                    if (bwOk)
                    {
                        cfg.MeasuredBandwidthMbps = (int)Math.Round(mbps);
                        cfg.BandwidthTestedAt = DateTime.UtcNow;
                        _logger.Information("[DV] {host}:{port} [{cc}] ✓✓ VERIFIED in {ms}ms · {mbps} Mbps",
                            cfg.Host, cfg.Port, cc, httpLatencyMs, cfg.MeasuredBandwidthMbps);
                    }
                    else
                    {
                        _logger.Information("[DV] {host}:{port} [{cc}] ✓✓ VERIFIED in {ms}ms · bw test failed: {err}",
                            cfg.Host, cfg.Port, cc, httpLatencyMs, bwErr);
                    }
                }
                else
                {
                    _logger.Information("[DV] {host}:{port} [{cc}] ✓✓ VERIFIED in {ms}ms",
                        cfg.Host, cfg.Port, cc, httpLatencyMs);
                }
            }
            else
            {
                if (cfg.Status == FreeConfigStatus.Ok || cfg.Status == FreeConfigStatus.Slow
                    || cfg.Status == FreeConfigStatus.Verified)
                    cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LastError = httpErr ?? "http failed";

                var stderrSnip = DeepVerifyProbe.ReadSanitizedSnippet(stderrBuffer, 200);
                _logger.Information("[DV] {host}:{port} [{cc}] ✗ {err} (total {total}ms){sbErr}",
                    cfg.Host, cfg.Port, cc, httpErr, sw.ElapsedMilliseconds,
                    string.IsNullOrWhiteSpace(stderrSnip) ? "" : $" | sb-err: {stderrSnip}");
            }
        }
        catch (OperationCanceledException)
        {
            cfg.LastError = "deep verify timeout";
            _logger.Warning("[DV] {host}:{port} [{cc}] → TIMEOUT after {ms}ms",
                cfg.Host, cfg.Port, cc, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[DV] {host}:{port} [{cc}] → THREW {type}",
                cfg.Host, cfg.Port, cc, ex.GetType().Name);
            cfg.LastError = ex.GetType().Name;
        }
        finally
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                process?.Dispose();
            }
            catch { }

            if (tmpConfigPath != null)
            {
                try { File.Delete(tmpConfigPath); } catch { }
            }
        }
    }

    internal static string BuildSingleOutboundConfig(VlessServerEntry s, int socksPort, int? clashPort) =>
        VlessDeepVerifier.BuildSingleOutboundConfig(s, socksPort, clashPort);
}
