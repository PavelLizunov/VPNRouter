using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum DeepVerifyFailurePhase
{
    None = 0,
    Precondition,
    LocalSpawn,
    SocksBind,
    ProxiedHttp,
    Timeout,
    Cancelled,
    UnsupportedByVerifier,
}

public sealed record DeepVerifyResult(
    bool Ok,
    int HttpLatencyMs,
    double? BandwidthMbps,
    string? Error,
    DeepVerifyFailurePhase FailurePhase = DeepVerifyFailurePhase.None,
    PhaseOutcome BlockedCanary = PhaseOutcome.Unknown)
{
    public static DeepVerifyResult Failed(string error) => new(false, 0, null, error);

    public static DeepVerifyResult Failed(string error, DeepVerifyFailurePhase phase)
        => new(false, 0, null, error, phase);
}

public sealed class VlessDeepVerifier
{
    private readonly ILogger _logger;
    private readonly string _singBoxPath;
    private readonly IProcessRunner _runner;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan OverallTimeout = DeepVerifyConstants.OverallTimeout;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan WarmupPerConcurrencySlack = TimeSpan.FromMilliseconds(300);

    public int MaxConcurrency { get; set; } = 5;

    internal TimeSpan EffectiveSocksBindWait =>
        SingBoxWarmup + WarmupPerConcurrencySlack * Math.Max(0, MaxConcurrency - 1);

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    public VlessDeepVerifier(ILogger logger, IProcessRunner? runner = null)
    {
        _logger = logger;
        _policy = SingBoxRuntimePolicy.Current;
        _singBoxPath = _policy?.SelectedExecutablePath ?? AppPaths.SingBoxExePath;
        _runner = runner ?? Runner;
    }

    internal VlessDeepVerifier(ILogger logger, string singBoxPath, IProcessRunner? runner = null)
    {
        _logger = logger;
        _policy = SingBoxRuntimePolicy.Current;
        _singBoxPath = _policy?.SelectedExecutablePath ?? singBoxPath;
        _runner = runner ?? Runner;
    }

    public bool IsAvailable => EffectivePolicy != null ? EffectivePolicy.IsAvailable : File.Exists(_singBoxPath);

    public async Task VerifyBatchAsync(
        IReadOnlyList<VlessServerEntry> servers,
        Action<VlessServerEntry, DeepVerifyResult> onOneDone,
        bool measureBandwidth,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var policy = EffectivePolicy;
        using var policyScope = SingBoxRuntimePolicy.EnterScope(policy);
        if (policy != null)
        {
            if (!policy.IsAvailable)
            {
                _logger.Warning("[VlessDeepVerifier] sing-box binary unavailable under runtime policy");
                foreach (var s in servers)
                    onOneDone(s, DeepVerifyResult.Failed("sing-box runtime is unavailable or untrusted", DeepVerifyFailurePhase.LocalSpawn));
                return;
            }
        }
        else if (!IsAvailable)
        {
            _logger.Warning("[VlessDeepVerifier] sing-box not found at {Path}", _singBoxPath);
            foreach (var s in servers)
                onOneDone(s, DeepVerifyResult.Failed("sing-box binary missing", DeepVerifyFailurePhase.LocalSpawn));
            return;
        }

        var sem = new SemaphoreSlim(MaxConcurrency);
        var total = servers.Count;
        var done = 0;

        var tasks = servers.Select(async entry =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var result = await VerifyAsync(entry, measureBandwidth, ct);
                onOneDone(entry, result);
            }
            catch (OperationCanceledException)
            {
                onOneDone(entry, DeepVerifyResult.Failed("cancelled", DeepVerifyFailurePhase.Cancelled));
            }
            catch (Exception ex)
            {
                onOneDone(entry, DeepVerifyResult.Failed(ex.GetType().Name));
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

    public async Task<DeepVerifyResult> VerifyAsync(
        VlessServerEntry entry,
        bool measureBandwidth,
        CancellationToken ct = default)
    {
        var protocol = (entry.Protocol ?? "vless").Trim().ToLowerInvariant();
        var label = string.IsNullOrEmpty(entry.Name) ? entry.Server : entry.Name;
        _logger.Debug(
            "[VlessDeepVerifier] start: name={Name} host={Host} port={Port} protocol={Protocol} measureBw={MeasureBw}",
            label, entry.Server, entry.Port, protocol, measureBandwidth);

        var placeholderField = PlaceholderDefense.Inspect(entry);
        if (placeholderField != null)
        {
            _logger.Warning(
                "[VlessDeepVerifier] {Name}: placeholder credential detected ({Field}) — refusing to probe",
                label, placeholderField);
            return DeepVerifyResult.Failed($"placeholder credential: {placeholderField}",
                DeepVerifyFailurePhase.Precondition);
        }

        var isAwg = protocol is "amneziawg" or "awg";
        var isXhttp = "xhttp".Equals(entry.Transport?.Type, StringComparison.OrdinalIgnoreCase);
        if (isAwg && !SingBoxFeatures.AwgAvailable)
        {
            _logger.Information("[VlessDeepVerifier] {Name}: AWG deep verify unsupported (core lacks with_awg)", label);
            return DeepVerifyResult.Failed("deep verify: AmneziaWG needs the lx core (with_awg)",
                DeepVerifyFailurePhase.UnsupportedByVerifier);
        }
        if (isXhttp && !SingBoxFeatures.XhttpAvailable)
        {
            _logger.Information("[VlessDeepVerifier] {Name}: xhttp deep verify unsupported (core lacks with_xhttp)", label);
            return DeepVerifyResult.Failed("deep verify: xhttp needs the lx core (with_xhttp)",
                DeepVerifyFailurePhase.UnsupportedByVerifier);
        }

        if (entry.IsDnsTunnel)
        {
            _logger.Information("[VlessDeepVerifier] {Name}: dns-tunnel deep verify unsupported (needs slipstream sidecar)", label);
            return DeepVerifyResult.Failed("deep verify: dns-tunnel needs the slipstream sidecar",
                DeepVerifyFailurePhase.UnsupportedByVerifier);
        }

        var policy = EffectivePolicy;
        using var policyScope = SingBoxRuntimePolicy.EnterScope(policy);
        if (policy != null)
        {
            try
            {
                policy.Authorize(SingBoxRuntimeOperation.Verify);
            }
            catch (SingBoxRuntimePolicyException)
            {
                return DeepVerifyResult.Failed("sing-box runtime is unavailable or untrusted", DeepVerifyFailurePhase.LocalSpawn);
            }
        }
        else if (!IsAvailable)
        {
            _logger.Warning("[VlessDeepVerifier] {Name}: sing-box binary missing at {Path}", label, _singBoxPath);
            return DeepVerifyResult.Failed("sing-box binary missing", DeepVerifyFailurePhase.LocalSpawn);
        }

        if ("naive".Equals(entry.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            if (!ServerUriParser.NaiveRuntimeAvailable)
            {
                _logger.Warning("[VlessDeepVerifier] {Name}: naive unsupported on this platform (needs libcronet)", label);
                return DeepVerifyResult.Failed("naive needs libcronet (Windows/Linux only)",
                    DeepVerifyFailurePhase.UnsupportedByVerifier);
            }
            if (policy == null)
                SingBoxManager.TryColocateCronet(_singBoxPath, AppContext.BaseDirectory, _logger);
        }

        using var probeScope = DeepVerifyProbe.BeginProbeScope();

        var socksPort = NetPortUtil.FindFreePort();
        var clashPort = NetPortUtil.FindFreePort();
        string? tmpConfigPath = null;
        IProcessHandle? handle = null;
        var stderrBuffer = new StringBuilder(DeepVerifyProbe.MaxDiagnosticBufferChars);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);

        try
        {
            var configJson = BuildSingleOutboundConfig(entry, socksPort, clashPort);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-dv-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tmpConfigPath, configJson, overallCts.Token);

            var request = new ProcessRequest(
                ExecutablePath: _singBoxPath,
                Arguments: new[] { "run", "-c", tmpConfigPath },
                CaptureStdout: true,
                CaptureStderr: true);

            try
            {
                handle = _runner.Start(request);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VlessDeepVerifier] {Name}: sing-box spawn failed", label);
                return DeepVerifyResult.Failed("sing-box spawn failed", DeepVerifyFailurePhase.LocalSpawn);
            }

            handle.ErrorLine += (_, line) =>
                DeepVerifyProbe.AppendSanitizedLine(
                    stderrBuffer,
                    line,
                    DeepVerifyProbe.MaxDiagnosticBufferChars);

            _logger.Debug("[VlessDeepVerifier] {Name}: sing-box spawned pid={Pid} socks={SocksPort}", label, handle.Pid, socksPort);

            if (!await DeepVerifyProbe.WaitForPortBoundAsync(socksPort, EffectiveSocksBindWait, overallCts.Token))
            {
                var snip = DeepVerifyProbe.ReadSanitizedSnippet(stderrBuffer, 80);
                _logger.Warning("[VlessDeepVerifier] {Name}: SOCKS port {Port} never bound. stderr: {Stderr}", label, socksPort, snip);
                return DeepVerifyResult.Failed(string.IsNullOrWhiteSpace(snip)
                    ? "sing-box didn't bind"
                    : $"sing-box: {snip}", DeepVerifyFailurePhase.SocksBind);
            }

            var (httpOk, httpLatencyMs, httpErr) = await DeepVerifyProbe.ProbeViaSocksAsync(socksPort, HttpTimeout, overallCts.Token);
            if (!httpOk)
            {
                _logger.Information("[VlessDeepVerifier] {Name}: HTTP probe FAILED — {Err}", label, httpErr);
                return DeepVerifyResult.Failed(httpErr ?? "http failed", DeepVerifyFailurePhase.ProxiedHttp);
            }

            var canary = await ProbeCanariesViaSocksAsync(socksPort, label, overallCts.Token);

            double? mbps = null;
            if (measureBandwidth)
            {
                try
                {
                    var (bwOk, measuredMbps, _) = await DeepVerifyProbe.MeasureBandwidthViaSocksAsync(socksPort, overallCts.Token);
                    if (bwOk) mbps = measuredMbps;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.Debug("[VlessDeepVerifier] {Name}: bandwidth probe hit the overall budget — reporting unmeasured", label);
                }
            }

            _logger.Information(
                "[VlessDeepVerifier] {Name}: PASS http={HttpMs}ms bw={BwMbps} canary={Canary}",
                label, httpLatencyMs, mbps?.ToString("F1") ?? "-", canary);
            return new DeepVerifyResult(true, httpLatencyMs, mbps, null, BlockedCanary: canary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Information("[VlessDeepVerifier] {Name}: cancelled", label);
            return DeepVerifyResult.Failed("cancelled", DeepVerifyFailurePhase.Cancelled);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("[VlessDeepVerifier] {Name}: TIMEOUT (overall {Sec}s)", label, OverallTimeout.TotalSeconds);
            return DeepVerifyResult.Failed("timeout", DeepVerifyFailurePhase.Timeout);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VlessDeepVerifier] {Name}: unexpected error", label);
            return DeepVerifyResult.Failed(ex.GetType().Name);
        }
        finally
        {
            try
            {
                if (handle != null)
                {
                    if (!handle.HasExited)
                    {
                        handle.Kill(entireProcessTree: true);
                    }
                    handle.Dispose();
                }
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
                "hysteria2" or "hy2"  => BuildHysteria2Outbound(s),
                "tuic"                => BuildTuicOutbound(s),
                "shadowsocks" or "ss" => BuildShadowsocksOutbound(s),
                "naive"               => BuildNaiveOutbound(s),
                _                     => BuildVlessOutbound(s),
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

    private async Task<PhaseOutcome> ProbeCanariesViaSocksAsync(int socksPort, string label, CancellationToken ct)
    {
        try
        {
            var targets = CanaryTargets.Load();
            var now = DateTimeOffset.UtcNow;
            var results = new List<(bool Passed, bool Stale)>();

            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(4),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };

            var probes = targets.Select(async t =>
            {
                var stale = CanaryPolicy.IsStale(t, now, CanaryTargets.ReviewTtl);
                bool passed;
                try
                {
                    using var resp = await http.GetAsync(t.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    passed = (int)resp.StatusCode < 500;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { passed = false; }
                _logger.Debug("[VlessDeepVerifier] {Name}: canary {Url} passed={Passed} stale={Stale}",
                    label, CanaryPolicy.RedactUrl(t.Url), passed, stale);
                return (passed, stale);
            }).ToList();

            results.AddRange(await Task.WhenAll(probes));

            var agg = CanaryPolicy.Evaluate(controlPassed: true, results);
            if (agg.BlockedTargetCanary == PhaseOutcome.Fail)
                _logger.Information("[VlessDeepVerifier] {Name}: canary FAIL — {Reason}", label, agg.Reason);
            return agg.BlockedTargetCanary;
        }
        catch (OperationCanceledException)
        {
            return PhaseOutcome.Unknown;
        }
        catch (Exception ex)
        {
            _logger.Debug("[VlessDeepVerifier] {Name}: canary stage error {Err} — inconclusive", label, ex.GetType().Name);
            return PhaseOutcome.Unknown;
        }
    }

    internal static JsonObject BuildVlessOutbound(VlessServerEntry s)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = s.Server,
            ["server_port"] = s.Port,
            ["uuid"] = s.Uuid,
            ["flow"] = string.IsNullOrWhiteSpace(s.Flow) ? null : s.Flow,
            ["packet_encoding"] = "xudp",
        };

        var isReality = string.Equals(s.Security, "reality", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(s.Reality?.PublicKey);

        if (isReality)
        {
            outbound["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrWhiteSpace(s.Reality?.ServerName) ? s.Server : s.Reality.ServerName,
                ["utls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["fingerprint"] = string.IsNullOrWhiteSpace(s.Reality?.Fingerprint) ? "chrome" : s.Reality.Fingerprint,
                },
                ["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = s.Reality!.PublicKey,
                    ["short_id"]  = s.Reality.ShortId ?? "",
                },
            };
        }
        else if (string.Equals(s.Security, "tls", StringComparison.OrdinalIgnoreCase) || s.Tls?.Enabled == true)
        {
            var sni = !string.IsNullOrWhiteSpace(s.Tls?.ServerName)
                ? s.Tls.ServerName
                : (!string.IsNullOrWhiteSpace(s.Reality?.ServerName) ? s.Reality.ServerName : s.Server);

            var tlsObj = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = sni,
                ["insecure"] = s.Tls?.Insecure ?? false,
            };

            var fp = !string.IsNullOrWhiteSpace(s.Tls?.Fingerprint)
                ? s.Tls.Fingerprint
                : (!string.IsNullOrWhiteSpace(s.Reality?.Fingerprint) ? s.Reality.Fingerprint : null);

            if (!string.IsNullOrWhiteSpace(fp))
            {
                tlsObj["utls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["fingerprint"] = fp,
                };
            }

            outbound["tls"] = tlsObj;
        }

        var transportType = s.Transport?.Type?.ToLowerInvariant() ?? "tcp";
        if (transportType == "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"] = "grpc",
                ["service_name"] = s.Transport?.Path ?? "",
            };
        }
        else if (transportType == "ws")
        {
            var wsObj = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = s.Transport?.Path ?? "/",
            };
            var hostHeader = s.Transport?.Host;
            if (string.IsNullOrWhiteSpace(hostHeader) && s.Transport?.Headers != null && s.Transport.Headers.TryGetValue("Host", out var h))
            {
                hostHeader = h;
            }
            if (string.IsNullOrWhiteSpace(hostHeader))
            {
                hostHeader = !string.IsNullOrWhiteSpace(s.Tls?.ServerName) ? s.Tls.ServerName : s.Reality?.ServerName;
            }
            if (!string.IsNullOrWhiteSpace(hostHeader))
            {
                wsObj["headers"] = new JsonObject
                {
                    ["Host"] = hostHeader,
                };
            }
            outbound["transport"] = wsObj;
        }
        else if (transportType == "xhttp")
        {
            var t = new JsonObject
            {
                ["type"] = "xhttp",
                ["mode"] = string.IsNullOrEmpty(s.Transport?.Mode) ? "auto" : s.Transport!.Mode,
                ["path"] = string.IsNullOrEmpty(s.Transport?.Path) ? "/" : s.Transport!.Path,
            };
            if (!string.IsNullOrEmpty(s.Transport?.Host)) t["host"] = s.Transport!.Host;
            if (!string.IsNullOrEmpty(s.Transport?.XPaddingBytes)) t["x_padding_bytes"] = s.Transport!.XPaddingBytes;
            if (s.Transport?.NoGrpcHeader == true) t["no_grpc_header"] = true;
            outbound["transport"] = t;

            outbound["flow"] = null;
        }

        return outbound;
    }

    internal static JsonObject BuildHysteria2Outbound(VlessServerEntry s)
    {
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = s.Server,
            ["server_port"] = s.Port,
            ["password"] = s.Password ?? string.Empty,
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrEmpty(s.Tls?.ServerName) ? s.Server : s.Tls!.ServerName,
                ["insecure"] = s.Tls?.Insecure ?? false,
                ["alpn"] = new JsonArray("h3"),
            },
        };

        if (!string.IsNullOrWhiteSpace(s.ObfsType))
        {
            outbound["obfs"] = new JsonObject
            {
                ["type"] = s.ObfsType,
                ["password"] = s.ObfsPassword ?? string.Empty,
            };
        }

        return outbound;
    }

    internal static JsonObject BuildTuicOutbound(VlessServerEntry s)
    {
        var alpn = new JsonArray();
        if (!string.IsNullOrWhiteSpace(s.Tls?.Alpn))
        {
            foreach (var part in s.Tls!.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                alpn.Add((JsonNode?)JsonValue.Create(part));
        }
        if (alpn.Count == 0) alpn.Add((JsonNode?)JsonValue.Create("h3"));

        return new JsonObject
        {
            ["type"] = "tuic",
            ["tag"] = "proxy",
            ["server"] = s.Server,
            ["server_port"] = s.Port,
            ["uuid"] = s.Uuid,
            ["password"] = s.Password ?? string.Empty,
            ["congestion_control"] = string.IsNullOrEmpty(s.CongestionControl) ? "bbr" : s.CongestionControl,
            ["udp_relay_mode"] = string.IsNullOrEmpty(s.UdpRelayMode) ? "native" : s.UdpRelayMode,
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrEmpty(s.Tls?.ServerName) ? s.Server : s.Tls!.ServerName,
                ["insecure"] = s.Tls?.Insecure ?? false,
                ["alpn"] = alpn,
            },
        };
    }

    internal static JsonObject BuildShadowsocksOutbound(VlessServerEntry s)
    {
        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["tag"] = "proxy",
            ["server"] = s.Server,
            ["server_port"] = s.Port,
            ["method"] = s.Method ?? string.Empty,
            ["password"] = s.Password ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(s.Plugin))
            outbound["plugin"] = s.Plugin;
        if (!string.IsNullOrWhiteSpace(s.PluginOpts))
            outbound["plugin_opts"] = s.PluginOpts;

        return outbound;
    }

    internal static JsonObject BuildNaiveOutbound(VlessServerEntry s)
    {
        var outbound = new JsonObject
        {
            ["type"] = "naive",
            ["tag"] = "proxy",
            ["server"] = s.Server,
            ["server_port"] = s.Port,
            ["username"] = s.Username ?? string.Empty,
            ["password"] = s.Password ?? string.Empty,
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrEmpty(s.Tls?.ServerName) ? s.Server : s.Tls!.ServerName,
            },
        };
        if (s.NaiveQuic) outbound["quic"] = true;
        return outbound;
    }
}
