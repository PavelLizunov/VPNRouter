using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

public sealed class FreeConfigDeepVerifier
{
    private readonly ILogger _logger;
    private readonly string _singBoxPath;

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
        _singBoxPath = AppPaths.SingBoxExePath;
    }

    public async Task VerifyBatchAsync(
        IReadOnlyCollection<FreeConfigEntry> configs,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(_singBoxPath))
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
                FileName = _singBoxPath,
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

    internal static string BuildSingleOutboundConfig(VlessServerEntry s, int socksPort, int? clashPort)
    {
        var protocol = (s.Protocol ?? "vless").Trim().ToLowerInvariant();
        JsonObject? outbound = null;
        JsonNode? awgEndpoint = null;

        if (protocol is "amneziawg" or "awg" or "awg3" or "amneziawg3")
        {
            awgEndpoint = System.Text.Json.JsonSerializer.SerializeToNode(
                ConfigGenerator.BuildAmneziaWgEndpoint(s, "proxy"));
        }
        else
        {
            outbound = protocol switch
            {
                "hysteria2" or "hy2"  => VlessDeepVerifier.BuildHysteria2Outbound(s),
                "tuic"                => VlessDeepVerifier.BuildTuicOutbound(s),
                "shadowsocks" or "ss" => VlessDeepVerifier.BuildShadowsocksOutbound(s),
                "naive"               => VlessDeepVerifier.BuildNaiveOutbound(s),
                _                     => VlessDeepVerifier.BuildVlessOutbound(s),
            };
        }

        var outboundsArray = new JsonArray();
        if (outbound != null)
        {
            outboundsArray.Add((JsonNode?)outbound);
        }
        outboundsArray.Add((JsonNode?)new JsonObject { ["type"] = "direct", ["tag"] = "dns-direct-out", ["udp_fragment"] = true });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    (JsonNode?)new JsonObject { ["type"] = "https", ["tag"] = "dns-google", ["server"] = "8.8.8.8", ["path"] = "/dns-query", ["detour"] = "dns-direct-out" },
                },
                ["final"] = "dns-google",
            },
            ["inbounds"] = new JsonArray
            {
                (JsonNode?)new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = socksPort,
                    ["sniff"] = false,
                },
            },
            ["outbounds"] = outboundsArray,
            ["route"] = new JsonObject
            {
                ["final"] = "proxy",
                ["default_domain_resolver"] = new JsonObject { ["server"] = "dns-google" },
                ["rules"] = new JsonArray
                {
                    (JsonNode?)new JsonObject { ["action"] = "sniff" },
                    (JsonNode?)new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                },
            },
        };

        if (awgEndpoint != null)
        {
            root["endpoints"] = new JsonArray { awgEndpoint };
        }

        if (clashPort is int port)
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{port}",
                },
            };
        }

        return root.ToJsonString();
    }

}
