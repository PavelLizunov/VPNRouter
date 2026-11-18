using System.Diagnostics;
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
        ct.ThrowIfCancellationRequested();
        cfg.BeginDeepRecheck();
        var entry = ServerUriParser.TryParse(cfg.RawUri);
        if (entry == null || !string.IsNullOrEmpty(entry.DetourVia))
        {
            cfg.LastError = entry == null ? "invalid server entry" : DeepVerifyConstants.DetourNote;
            return;
        }

        using var probeScope = DeepVerifyProbe.BeginProbeScope();
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);
        string? tmpConfigPath = null;
        Process? process = null;
        try
        {
            var proxy = new VerificationProxy(NetPortUtil.FindFreePort());
            var configJson = BuildSingleOutboundConfig(entry, proxy);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-verify-{Guid.NewGuid():N}.json");
            await using (var writer = new StreamWriter(AppPaths.CreatePrivateFile(tmpConfigPath, FileMode.CreateNew)))
                await writer.WriteAsync(configJson.AsMemory(), overallCts.Token);

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
                EnableRaisingEvents = true,
            };
            process.OutputDataReceived += (_, e) => proxy.ObserveChildOutput(e.Data);
            process.ErrorDataReceived += (_, e) => proxy.ObserveChildOutput(e.Data);
            process.Exited += OnVerifierExit;

            if (!process.Start())
            {
                cfg.LastError = "sing-box spawn failed";
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.HasExited || !await proxy.WaitUntilBoundAsync(EffectiveSocksBindWait, overallCts.Token))
            {
                cfg.LastError = proxy.StartupError;
                return;
            }

            var (httpOk, httpLatencyMs, httpErr) = await DeepVerifyProbe.ProbeViaProxyAsync(proxy, HttpTimeout, overallCts.Token);
            if (!httpOk)
            {
                cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LastError = httpErr ?? "HTTP verification failed";
                return;
            }

            if (MeasureBandwidth)
            {
                try
                {
                    var (bwOk, mbps, _) = await DeepVerifyProbe.MeasureBandwidthViaProxyAsync(proxy, overallCts.Token);
                    if (bwOk)
                    {
                        cfg.MeasuredBandwidthMbps = (int)Math.Round(mbps);
                        cfg.BandwidthTestedAt = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !process.HasExited)
                {
                    _logger.Debug("[DV] Optional bandwidth measurement timed out");
                }
            }

            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                cfg.LastError = "verification engine stopped";
                return;
            }
            cfg.Status = FreeConfigStatus.Verified;
            cfg.LastDeepVerifyAt = DateTime.UtcNow;
            cfg.LastVerifyFailedAt = null;
            if (cfg.LatencyMs == 0) cfg.LatencyMs = httpLatencyMs;
            cfg.LastError = null;
            _logger.Information("[DV] Verified in {LatencyMs} ms", httpLatencyMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            cfg.LastError = process?.HasExited == true ? "verification engine stopped" : "deep verify timeout";
        }
        catch (Exception ex)
        {
            _logger.Warning("[DV] Verification failed ({Type})", ex.GetType().Name);
            cfg.LastError = "verification failed";
        }
        finally
        {
            try
            {
                if (process != null) process.Exited -= OnVerifierExit;
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

        void OnVerifierExit(object? sender, EventArgs args)
        {
            try { overallCts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    internal static string BuildSingleOutboundConfig(VlessServerEntry s, VerificationProxy proxy) =>
        VlessDeepVerifier.BuildSingleOutboundConfig(s, proxy);
}
