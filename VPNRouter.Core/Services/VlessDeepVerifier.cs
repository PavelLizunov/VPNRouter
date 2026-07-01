using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>Outcome of a deep verification pass through a spawned sing-box.</summary>
public sealed record DeepVerifyResult(
    bool Ok,
    int HttpLatencyMs,
    double? BandwidthMbps,
    string? Error)
{
    public static DeepVerifyResult Failed(string error) => new(false, 0, null, error);
}

/// <summary>
/// Generic VLESS deep verifier for Servers/Subscriptions tabs (v2.15.3).
/// Spawns a temporary sing-box with a single VLESS outbound + local SOCKS
/// inbound, then performs HTTP GET through it (optionally followed by a
/// 5 MB bandwidth probe). Returns structured <see cref="DeepVerifyResult"/>.
///
/// Duplicates the sing-box spawn logic of <c>FreeConfigDeepVerifier</c> by
/// design — FreeConfigs has its own status enum and result mutation pattern
/// that doesn't fit ServerViewModel. Consolidation possible in a future
/// refactor (v2.16+).
/// </summary>
public sealed class VlessDeepVerifier
{
    private readonly ILogger _logger;
    private readonly string _singBoxPath;
    private readonly IProcessRunner _runner;

    private const string ProbeUrl = DeepVerifyConstants.ProbeUrl;
    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan OverallTimeout = DeepVerifyConstants.OverallTimeout;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    public int MaxConcurrency { get; set; } = 5;

    // Phase 3+ (2026-05-21) IProcessRunner adoption — first long-lived spawn
    // target. The sing-box probe lifetime is ≤12s (OverallTimeout) and the
    // service doesn't subscribe to the Exited event, so the implicit
    // EnableRaisingEvents=false handling inside ProcessHandle.Dispose carries
    // the load-bearing intent (no spurious Exited callback) transitively. See
    // brief: plans/phase3-iprocessrunner-vlessdeepverifier-2026-05-21.md
    /// <summary>Test-only seam: swap in a fake. Production paths use the
    /// default <see cref="ProcessRunner"/>. Not thread-safe — assumes serial
    /// xUnit execution within the fixture; tests reset in try/finally.</summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    public VlessDeepVerifier(ILogger logger, IProcessRunner? runner = null)
    {
        _logger = logger;
        _singBoxPath = AppPaths.SingBoxExePath;
        _runner = runner ?? Runner;
    }

    /// <summary>
    /// Test-only ctor (v3.0 Phase 2G-7c-1): lets unit tests inject an
    /// alternate sing-box binary path so the "binary missing" branch can
    /// be exercised deterministically (production resolves to
    /// <see cref="AppPaths.SingBoxExePath"/>). Marked <c>internal</c> +
    /// visible to <c>VPNRouter.Tests</c> via <c>InternalsVisibleTo</c>.
    ///
    /// <para>Phase 3+ (2026-05-21): optional <paramref name="runner"/>
    /// arg lets wire-shape tests inject a <c>FakeProcessRunner</c>
    /// without depending on the static <see cref="Runner"/> seam.</para>
    /// </summary>
    internal VlessDeepVerifier(ILogger logger, string singBoxPath, IProcessRunner? runner = null)
    {
        _logger = logger;
        _singBoxPath = singBoxPath;
        _runner = runner ?? Runner;
    }

    public bool IsAvailable => File.Exists(_singBoxPath);

    /// <summary>Verify a batch of VLESS servers in parallel.</summary>
    public async Task VerifyBatchAsync(
        IReadOnlyList<VlessServerEntry> servers,
        Action<VlessServerEntry, DeepVerifyResult> onOneDone,
        bool measureBandwidth,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            _logger.Warning("[VlessDeepVerifier] sing-box not found at {Path}", _singBoxPath);
            foreach (var s in servers)
                onOneDone(s, DeepVerifyResult.Failed("sing-box binary missing"));
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
                onOneDone(entry, DeepVerifyResult.Failed("cancelled"));
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

    /// <summary>Verify a single VLESS server via sing-box spawn + SOCKS probe.</summary>
    public async Task<DeepVerifyResult> VerifyAsync(
        VlessServerEntry entry,
        bool measureBandwidth,
        CancellationToken ct = default)
    {
        // v2.31.6-r16 (iter#7 / Phase 3): structured per-probe logging.
        // User feedback: «есть ли у проверки логи?» — pre-r16 only top-level
        // batch failures showed up in vpnrouter.log; per-server outcomes
        // (sing-box stderr, HTTP error, port-bind timeout) were silent.
        var protocol = (entry.Protocol ?? "vless").Trim().ToLowerInvariant();
        var label = string.IsNullOrEmpty(entry.Name) ? entry.Server : entry.Name;
        _logger.Debug(
            "[VlessDeepVerifier] start: name={Name} host={Host} port={Port} protocol={Protocol} measureBw={MeasureBw}",
            label, entry.Server, entry.Port, protocol, measureBandwidth);

        // v2.32.3 (v3.0 Phase 2G): placeholder-credential gate. A subscription
        // / paste that smuggled stas-class fingerprints (see
        // PlaceholderDefense.KnownFingerprints for the literal pubkey /
        // short_id / server triple) past the upstream input gates would
        // otherwise reach sing-box and either (a) silently fail to connect,
        // or worse (b) report "verified" if the host happens to be reachable
        // on TCP/443 but the Reality handshake never completes. Reject up
        // front so the verdict surface is honest. Same fingerprint list the
        // settings migrator + resolver scope guard use — single source of
        // truth at <see cref="PlaceholderDefense"/>.
        var placeholderField = PlaceholderDefense.Inspect(entry);
        if (placeholderField != null)
        {
            _logger.Warning(
                "[VlessDeepVerifier] {Name}: placeholder credential detected ({Field}) — refusing to probe",
                label, placeholderField);
            return DeepVerifyResult.Failed($"placeholder credential: {placeholderField}");
        }

        if (!IsAvailable)
        {
            _logger.Warning("[VlessDeepVerifier] {Name}: sing-box binary missing at {Path}", label, _singBoxPath);
            return DeepVerifyResult.Failed("sing-box binary missing");
        }

        // r7 #5: naive needs libcronet next to sing-box (Windows/Linux only). The
        // parser refuses naive on Cronet-less platforms, but a carried-over
        // settings.yaml could still hand us one — fail honestly instead of a
        // misleading generic error. On Win/Linux, colocate libcronet next to the
        // (maybe never-launched-yet) sing-box so the spawn below can dlopen it.
        if ("naive".Equals(entry.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            if (!ServerUriParser.NaiveRuntimeAvailable)
            {
                _logger.Warning("[VlessDeepVerifier] {Name}: naive unsupported on this platform (needs libcronet)", label);
                return DeepVerifyResult.Failed("naive needs libcronet (Windows/Linux only)");
            }
            SingBoxManager.TryColocateCronet(_singBoxPath, AppContext.BaseDirectory, _logger);
        }

        var socksPort = NetPortUtil.FindFreePort();
        var clashPort = NetPortUtil.FindFreePort();
        string? tmpConfigPath = null;
        IProcessHandle? handle = null;
        var stderrBuffer = new StringBuilder(capacity: 2048);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);

        try
        {
            var configJson = BuildSingleOutboundConfig(entry, socksPort, clashPort);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-dv-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tmpConfigPath, configJson, overallCts.Token);

            // Phase 3+ (2026-05-21): route the sing-box spawn through
            // IProcessRunner so wire-shape tests can pin the argv +
            // CaptureStderr without invoking the real binary. Drop the
            // explicit `EnableRaisingEvents = false` — this service never
            // subscribed to Exited, and ProcessHandle.Dispose disables the
            // flag before Kill anyway (ProcessRunner.cs lines 280-293), so
            // the load-bearing intent (no spurious Exited callback) is
            // preserved transitively.
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
                return DeepVerifyResult.Failed("sing-box spawn failed");
            }

            handle.ErrorLine += (_, line) =>
            {
                if (line != null) stderrBuffer.Append(line).Append('\n');
            };

            _logger.Debug("[VlessDeepVerifier] {Name}: sing-box spawned pid={Pid} socks={SocksPort}", label, handle.Pid, socksPort);

            if (!await WaitForPortBoundAsync(socksPort, SingBoxWarmup, overallCts.Token))
            {
                var snip = TrimSnippet(stderrBuffer.ToString(), 80);
                _logger.Warning("[VlessDeepVerifier] {Name}: SOCKS port {Port} never bound. stderr: {Stderr}", label, socksPort, snip);
                return DeepVerifyResult.Failed(string.IsNullOrWhiteSpace(snip)
                    ? "sing-box didn't bind"
                    : $"sing-box: {snip}");
            }

            var (httpOk, httpLatencyMs, httpErr) = await ProbeViaSocksAsync(socksPort, overallCts.Token);
            if (!httpOk)
            {
                _logger.Information("[VlessDeepVerifier] {Name}: HTTP probe FAILED — {Err}", label, httpErr);
                return DeepVerifyResult.Failed(httpErr ?? "http failed");
            }

            double? mbps = null;
            if (measureBandwidth)
            {
                var (bwOk, measuredMbps, _) = await MeasureBandwidthViaSocksAsync(socksPort, overallCts.Token);
                if (bwOk) mbps = measuredMbps;
            }

            _logger.Information(
                "[VlessDeepVerifier] {Name}: PASS http={HttpMs}ms bw={BwMbps}",
                label, httpLatencyMs, mbps?.ToString("F1") ?? "-");
            return new DeepVerifyResult(true, httpLatencyMs, mbps, null);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("[VlessDeepVerifier] {Name}: TIMEOUT (overall {Sec}s)", label, OverallTimeout.TotalSeconds);
            return DeepVerifyResult.Failed("timeout");
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

    // ─── Helpers (mirror FreeConfigDeepVerifier, kept here to keep this class standalone) ─────

    /// <summary>
    /// v2.31.6-r16 (iter#7 / Phase 2): protocol-aware outbound dispatcher.
    /// Pre-r16 hard-coded <c>["type"] = "vless"</c> for every entry, so
    /// Hysteria2/TUIC/Shadowsocks deep-verify always failed (sing-box
    /// rejected the spawned config because protocol vs. credentials
    /// didn't match). Now dispatches to the protocol-specific builder
    /// in parallel with <see cref="ConfigGenerator.BuildVlessOutbound"/>'s
    /// dispatch pattern (see ConfigGenerator.cs:858–869).
    /// </summary>
    internal static string BuildSingleOutboundConfig(VlessServerEntry s, int socksPort, int clashPort)
    {
        var protocol = (s.Protocol ?? "vless").Trim().ToLowerInvariant();
        var outbound = protocol switch
        {
            "hysteria2"   => BuildHysteria2Outbound(s),
            "hy2"         => BuildHysteria2Outbound(s),
            "tuic"        => BuildTuicOutbound(s),
            "shadowsocks" => BuildShadowsocksOutbound(s),
            "ss"          => BuildShadowsocksOutbound(s),
            "naive"       => BuildNaiveOutbound(s),   // r7 #5: was falling to vless → false-fail for valid naive
            _             => BuildVlessOutbound(s),
        };

        // Phase 6 — Wave 31b: cast every JsonArray element to (JsonNode?)
        // so the desugared .Add calls pick JsonArray.Add(JsonNode?) instead
        // of Add<T>(T) (IL3050). Same wire-format output, zero behaviour
        // change — just helps the AOT analyser.
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    (JsonNode?)new JsonObject { ["type"] = "udp", ["tag"] = "dns-google", ["server"] = "1.1.1.1", ["detour"] = "dns-direct-out" },
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
            ["outbounds"] = new JsonArray
            {
                (JsonNode?)outbound,
                (JsonNode?)new JsonObject { ["type"] = "direct", ["tag"] = "dns-direct-out", ["udp_fragment"] = true },
            },
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
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{clashPort}",
                },
            },
        };

        // Pass null (uses JsonSerializerOptions.Default with reflection-based resolver).
        // Custom `new JsonSerializerOptions { WriteIndented = false }` lacks a TypeInfoResolver
        // and triggers "JsonSerializerOptions instance must specify a TypeInfoResolver" on
        // some .NET 8 runtimes (notably ubuntu-latest CI) when JsonValueCustomized<string>
        // tries to serialize the alpn array entries (TUIC). Defaults are already
        // WriteIndented=false so behaviour is identical.
        return root.ToJsonString();
    }

    private static async Task<bool> WaitForPortBoundAsync(int port, TimeSpan maxWait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var c = new TcpClient();
                var connectTask = c.ConnectAsync(IPAddress.Loopback, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(200, ct));
                if (completed == connectTask && c.Connected) return true;
            }
            catch { /* keep polling */ }
            await Task.Delay(100, ct);
        }
        return false;
    }

    private static async Task<(bool ok, int latencyMs, string? err)> ProbeViaSocksAsync(int socksPort, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = HttpTimeout };

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(ProbeUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, 0, $"http {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!body.Contains("ip=", StringComparison.Ordinal))
                return (false, 0, "bad response");

            var ipLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("ip=", StringComparison.Ordinal));
            if (ipLine != null)
            {
                var ipStr = ipLine[3..].Trim();
                if (IPAddress.TryParse(ipStr, out var ip))
                {
                    if (IsPrivateOrLoopback(ip))
                        return (false, 0, "local ip in response");
                }
            }

            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (TaskCanceledException)
        {
            return (false, 0, "http timeout");
        }
        catch (HttpRequestException hx)
        {
            return (false, 0, $"http: {Short(hx.Message)}");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    private static async Task<(bool ok, double mbps, string? err)> MeasureBandwidthViaSocksAsync(
        int socksPort, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var urls = new[]
        {
            "https://speed.cloudflare.com/__down?bytes=5242880",
            "https://proof.ovh.net/files/10Mb.dat",
            "https://ash-speed.hetzner.com/100MB.bin",
        };

        foreach (var url in urls)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[8192];
                long total = 0;
                const long target = 5_242_880L;
                while (total < target)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    total += n;
                }
                sw.Stop();

                if (total < 1_000_000) continue;
                if (sw.ElapsedMilliseconds < 100) continue;

                var mbps = (total * 8.0 / 1_000_000.0) / (sw.ElapsedMilliseconds / 1000.0);
                return (true, mbps, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* try next URL */ }
        }
        return (false, 0, "all bandwidth URLs failed");
    }

    internal static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        return false;
    }

    internal static string TrimSnippet(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;

    // ─── Protocol-specific outbound builders (v2.31.6-r16, Phase 2) ──────────
    // Mirror ConfigGenerator.BuildVlessOutbound dispatcher (ConfigGenerator.cs
    // lines 858–869). Kept here as JsonObject builders to match the existing
    // VlessDeepVerifier style (rest of BuildSingleOutboundConfig uses JsonNode).

    /// <summary>VLESS outbound (Reality / TLS / plain). Pre-r16 logic, extracted into a builder.</summary>
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

        if (s.Reality?.Enabled == true)
        {
            outbound["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = s.Reality.ServerName ?? s.Server,
                ["utls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["fingerprint"] = string.IsNullOrWhiteSpace(s.Reality.Fingerprint) ? "chrome" : s.Reality.Fingerprint,
                },
                ["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = s.Reality.PublicKey ?? "",
                    ["short_id"]  = s.Reality.ShortId ?? "",
                },
            };
        }
        else if (s.Tls?.Enabled == true)
        {
            outbound["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = s.Tls.ServerName ?? s.Server,
                ["insecure"] = s.Tls.Insecure,
            };
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
            outbound["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = s.Transport?.Path ?? "/",
            };
        }

        return outbound;
    }

    /// <summary>
    /// Hysteria2 outbound (UDP+QUIC). ALPN forced to ["h3"] per Hysteria2
    /// spec. Optional Salamander obfs from <see cref="VlessServerEntry.ObfsType"/>.
    /// Mirrors <c>ConfigGenerator.BuildHysteria2Outbound</c>.
    /// </summary>
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

    /// <summary>
    /// TUIC v5 outbound (UDP+QUIC). ALPN ["h3"] default, BBR congestion
    /// control default. Mirrors <c>ConfigGenerator.BuildTuicOutbound</c>.
    /// </summary>
    internal static JsonObject BuildTuicOutbound(VlessServerEntry s)
    {
        // Phase 6 — Wave 31b: wrap strings in JsonValue.Create() + cast to
        // (JsonNode?) so .Add picks the non-generic overload (IL3050).
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

    /// <summary>
    /// Shadowsocks outbound (TCP, optional plugin like shadow-tls v3).
    /// Supports SS 2022 ciphers natively via <see cref="VlessServerEntry.Method"/>.
    /// Mirrors <c>ConfigGenerator.BuildShadowsocksOutbound</c>.
    /// </summary>
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

    /// <summary>
    /// r7 #5: NaiveProxy outbound (HTTP/2 CONNECT, or HTTP/3 when NaiveQuic).
    /// Needs libcronet next to sing-box at runtime — VerifyAsync colocates it
    /// before spawning. Mirrors <c>ConfigGenerator.BuildNaiveOutbound</c>.
    /// </summary>
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
