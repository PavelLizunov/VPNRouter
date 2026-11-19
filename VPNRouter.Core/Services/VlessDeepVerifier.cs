using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Json;
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
                throw;
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
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(entry.DetourVia))
            return DeepVerifyResult.Failed(DeepVerifyConstants.DetourNote, DeepVerifyFailurePhase.UnsupportedByVerifier);

        var placeholderField = PlaceholderDefense.Inspect(entry);
        if (placeholderField != null)
        {
            _logger.Warning(
                "[VlessDeepVerifier] Placeholder credential detected ({Field})", placeholderField);
            return DeepVerifyResult.Failed($"placeholder credential: {placeholderField}",
                DeepVerifyFailurePhase.Precondition);
        }

        var isAwg = protocol is "amneziawg" or "awg";
        var isXhttp = "xhttp".Equals(entry.Transport?.Type, StringComparison.OrdinalIgnoreCase);
        if (isAwg && !SingBoxFeatures.AwgAvailable)
        {
            _logger.Information("[VlessDeepVerifier] AWG deep verify unsupported (core lacks with_awg)");
            return DeepVerifyResult.Failed("deep verify: AmneziaWG needs the lx core (with_awg)",
                DeepVerifyFailurePhase.UnsupportedByVerifier);
        }
        if (isXhttp && !SingBoxFeatures.XhttpAvailable)
        {
            _logger.Information("[VlessDeepVerifier] xhttp deep verify unsupported (core lacks with_xhttp)");
            return DeepVerifyResult.Failed("deep verify: xhttp needs the lx core (with_xhttp)",
                DeepVerifyFailurePhase.UnsupportedByVerifier);
        }

        if (entry.IsDnsTunnel)
        {
            _logger.Information("[VlessDeepVerifier] dns-tunnel deep verify unsupported (needs slipstream sidecar)");
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
            _logger.Warning("[VlessDeepVerifier] sing-box binary missing");
            return DeepVerifyResult.Failed("sing-box binary missing", DeepVerifyFailurePhase.LocalSpawn);
        }

        if ("naive".Equals(entry.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            if (!ServerUriParser.NaiveRuntimeAvailable)
            {
                _logger.Warning("[VlessDeepVerifier] naive unsupported on this platform (needs libcronet)");
                return DeepVerifyResult.Failed("naive needs libcronet (Windows/Linux only)",
                    DeepVerifyFailurePhase.UnsupportedByVerifier);
            }
            if (policy == null)
                SingBoxManager.TryColocateCronet(_singBoxPath, AppContext.BaseDirectory, _logger);
        }

        using var probeScope = DeepVerifyProbe.BeginProbeScope();

        var proxy = new VerificationProxy(NetPortUtil.FindFreePort());
        string? tmpConfigPath = null;
        IProcessHandle? handle = null;

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);

        try
        {
            var configJson = BuildSingleOutboundConfig(entry, proxy);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-dv-{Guid.NewGuid():N}.json");
            await using (var writer = new StreamWriter(AppPaths.CreatePrivateFile(tmpConfigPath, FileMode.CreateNew)))
                await writer.WriteAsync(configJson.AsMemory(), overallCts.Token);

            var request = new ProcessRequest(
                ExecutablePath: policy?.SelectedExecutablePath ?? _singBoxPath,
                Arguments: new[] { "run", "-c", tmpConfigPath },
                CaptureStdout: true,
                CaptureStderr: true);
            try
            {
                handle = _runner.Start(request, child =>
                {
                    child.OutputLine += (_, line) => proxy.ObserveChildOutput(line);
                    child.ErrorLine += (_, line) => proxy.ObserveChildOutput(line);
                    child.Exited += OnVerifierExit;
                });
            }
            catch (Exception ex)
            {
                _logger.Warning("[VlessDeepVerifier] Engine start failed ({Type})", ex.GetType().Name);
                return DeepVerifyResult.Failed("sing-box spawn failed", DeepVerifyFailurePhase.LocalSpawn);
            }

            if (handle.HasExited || !await proxy.WaitUntilBoundAsync(EffectiveSocksBindWait, overallCts.Token))
                return DeepVerifyResult.Failed(proxy.StartupError, DeepVerifyFailurePhase.SocksBind);

            var (httpOk, httpLatencyMs, httpErr) = await DeepVerifyProbe.ProbeViaProxyAsync(proxy, HttpTimeout, overallCts.Token);
            if (!httpOk)
            {
                _logger.Information("[VlessDeepVerifier] HTTP probe failed");
                return DeepVerifyResult.Failed(httpErr ?? "http failed", DeepVerifyFailurePhase.ProxiedHttp);
            }

            var canary = await ProbeCanariesViaProxyAsync(proxy, overallCts.Token);

            double? mbps = null;
            if (measureBandwidth)
            {
                try
                {
                    var (bwOk, measuredMbps, _) = await DeepVerifyProbe.MeasureBandwidthViaProxyAsync(proxy, overallCts.Token);
                    if (bwOk) mbps = measuredMbps;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !handle.HasExited)
                {
                    _logger.Debug("[VlessDeepVerifier] bandwidth probe hit the overall budget — reporting unmeasured");
                }
            }

            ct.ThrowIfCancellationRequested();
            if (handle.HasExited)
                return DeepVerifyResult.Failed("verification engine stopped", DeepVerifyFailurePhase.LocalSpawn);
            _logger.Information("[VlessDeepVerifier] Verified in {LatencyMs} ms", httpLatencyMs);
            return new DeepVerifyResult(true, httpLatencyMs, mbps, null, BlockedCanary: canary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (handle?.HasExited == true)
        {
            return DeepVerifyResult.Failed("verification engine stopped", DeepVerifyFailurePhase.LocalSpawn);
        }
        catch (OperationCanceledException)
        {
            return DeepVerifyResult.Failed("timeout", DeepVerifyFailurePhase.Timeout);
        }
        catch (Exception ex)
        {
            _logger.Warning("[VlessDeepVerifier] Verification failed ({Type})", ex.GetType().Name);
            return DeepVerifyResult.Failed(ex.GetType().Name);
        }
        finally
        {
            try
            {
                if (handle != null)
                {
                    handle.Exited -= OnVerifierExit;
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

        void OnVerifierExit(object? sender, int exitCode)
        {
            try { overallCts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    internal static string BuildSingleOutboundConfig(VlessServerEntry s, VerificationProxy proxy, bool embedded = false)
    {
        if (!string.IsNullOrEmpty(s.DetourVia))
            throw new InvalidOperationException(DeepVerifyConstants.DetourNote);
        var protocol = (s.Protocol ?? "vless").Trim().ToLowerInvariant();
        JsonObject? outbound = null;
        JsonNode? awgEndpoint = null;

        var outboundsArray = new JsonArray();
        if (protocol == "dnstt")
        {
            foreach (var item in ConfigGenerator.BuildDnsttProxyPair(s))
                outboundsArray.Add(JsonSerializer.SerializeToNode(item, AppJsonContext.Default.SingBoxOutbound));
        }
        else if (protocol is "amneziawg" or "awg" or "awg3" or "amneziawg3")
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

        if (outbound != null)
        {
            outboundsArray.Add((JsonNode?)outbound);
        }
        outboundsArray.Add((JsonNode?)new JsonObject { ["type"] = "direct", ["tag"] = "dns-direct-out", ["udp_fragment"] = true });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = embedded ? "error" : "info" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    (JsonNode?)new JsonObject { ["type"] = "https", ["tag"] = "dns-google", ["server"] = "8.8.8.8", ["path"] = "/dns-query", ["detour"] = "dns-direct-out" },
                },
                ["final"] = "dns-google",
            },
            ["inbounds"] = new JsonArray(proxy.CreateInbound()),
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

        return root.ToJsonString();
    }

    private async Task<PhaseOutcome> ProbeCanariesViaProxyAsync(VerificationProxy proxy, CancellationToken ct)
    {
        try
        {
            var targets = CanaryTargets.Load();
            var now = DateTimeOffset.UtcNow;
            var probes = targets.Select(async t =>
            {
                var stale = CanaryPolicy.IsStale(t, now, CanaryTargets.ReviewTtl);
                var passed = await ProbeCanaryAsync(proxy, t.Url, ct);
                return (passed, stale);
            });

            var agg = CanaryPolicy.Evaluate(controlPassed: true, await Task.WhenAll(probes));
            if (agg.BlockedTargetCanary == PhaseOutcome.Fail)
                _logger.Information("[VlessDeepVerifier] Canary check failed");
            return agg.BlockedTargetCanary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Debug("[VlessDeepVerifier] Canary stage failed ({Type})", ex.GetType().Name);
            return PhaseOutcome.Unknown;
        }
    }

    internal static async Task<bool> ProbeCanaryAsync(VerificationProxy proxy, string url, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(4);
        using var http = new HttpClient(proxy.CreateHttpHandler(timeout)) { Timeout = timeout };
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
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
