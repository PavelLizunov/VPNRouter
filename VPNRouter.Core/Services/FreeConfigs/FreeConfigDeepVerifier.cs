using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Deep verification: spawn a temporary sing-box instance with a single VLESS outbound
/// and a SOCKS inbound on a free local port, then attempt an actual HTTP GET through it.
///
/// This is the only reliable way to know a config actually carries traffic — TCP+TLS
/// tests can pass for many dead/fake endpoints because the user's active VPN transparently
/// proxies handshakes.
///
/// Cost: ~3-5 seconds per config (sing-box spin-up + HTTP round-trip + teardown).
/// Concurrency: CAN run in parallel (each spawn uses its own SOCKS port — no TUN involved).
/// </summary>
public sealed class FreeConfigDeepVerifier
{
    private readonly ILogger _logger;
    private readonly string _singBoxPath;

    /// <summary>URL probed for verification. Cloudflare's trace endpoint — small, fast, globally distributed.</summary>
    private const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    /// <summary>Time to wait for sing-box to bind SOCKS before we attempt HTTP.</summary>
    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);

    /// <summary>Overall per-config timeout.</summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);

    /// <summary>HTTP request timeout (through SOCKS proxy).</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How many configs to verify in parallel.</summary>
    public int MaxConcurrency { get; set; } = 5;

    /// <summary>v2.14.3: if true, after HTTP trace also measure download throughput
    /// via a 5 MB file from cloudflare/hetzner/ovh. Adds 3-8s per config.</summary>
    public bool MeasureBandwidth { get; set; } = false;

    public FreeConfigDeepVerifier(ILogger logger)
    {
        _logger = logger;
        _singBoxPath = AppPaths.SingBoxExePath;
    }

    /// <summary>
    /// Verify a batch of configs. Mutates entries in place:
    ///   success → Status = Verified, LastError = null, LatencyMs = HTTP RTT
    ///   failure → Status unchanged (or TlsFailed if TLS actually failed), LastError = reason
    /// </summary>
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

        var socksPort = FindFreePort();
        var clashPort = FindFreePort();
        string? tmpConfigPath = null;
        Process? process = null;
        var stderrBuffer = new System.Text.StringBuilder(capacity: 2048);
        var stdoutBuffer = new System.Text.StringBuilder(capacity: 512);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);

        var sw = Stopwatch.StartNew();
        var cc = cfg.CountryCode ?? "??";

        try
        {
            // 1. Build minimal sing-box config.
            var vless = VlessUriParser.Parse(cfg.RawUri);
            var configJson = BuildSingleOutboundConfig(vless, socksPort, clashPort);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-verify-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tmpConfigPath, configJson, overallCts.Token);

            // 2. Launch sing-box with stdout/stderr capture for diagnostics.
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _singBoxPath,
                    Arguments = $"run -c \"{tmpConfigPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = false,
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdoutBuffer.Append(e.Data).Append('\n');
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrBuffer.Append(e.Data).Append('\n');
            };

            if (!process.Start())
            {
                cfg.LastError = "sing-box spawn failed";
                _logger.Warning("[DV] {host}:{port} [{cc}] → spawn failed", cfg.Host, cfg.Port, cc);
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 3. Wait for sing-box to bind. Poll the SOCKS port.
            if (!await WaitForPortBoundAsync(socksPort, SingBoxWarmup, overallCts.Token))
            {
                var stderrSnip = TrimSnippet(stderrBuffer.ToString(), 300);
                cfg.LastError = $"sing-box didn't bind: {TrimSnippet(stderrSnip, 80)}";
                _logger.Warning("[DV] {host}:{port} [{cc}] → didn't bind. stderr: {err}",
                    cfg.Host, cfg.Port, cc, stderrSnip);
                return;
            }

            // 4. HTTP GET through SOCKS proxy.
            var (httpOk, httpLatencyMs, httpErr) = await ProbeViaSocksAsync(socksPort, overallCts.Token);

            if (httpOk)
            {
                cfg.Status = FreeConfigStatus.Verified;
                // v2.28.6-r5: do NOT overwrite cfg.LatencyMs with httpLatencyMs.
                // HTTP RTT through the proxy includes 5-7 round-trips:
                //   1. local TCP+SOCKS handshake
                //   2. TCP connect to proxy server
                //   3. VLESS+Reality TLS-like handshake
                //   4. TCP connect from proxy to target (cloudflare.com)
                //   5. TLS handshake to target
                //   6. HTTP request/response
                // Even on a 30 ms link to the proxy, this stacks up to
                // 200-500 ms — what the user sees as "ping > 300 ms".
                // The user's mental model of "ping" is raw TCP RTT to the
                // proxy server. That value was already measured by
                // FreeConfigTester.TestOneAsync before this method ran.
                // We keep cfg.LatencyMs = TCP ping; httpLatencyMs is used
                // only for logging and as the "did the proxy actually
                // pass traffic" gate above.
                if (cfg.LatencyMs == 0)
                {
                    // Defensive fallback for recheck-only flows where the
                    // verifier ran without a fresh TCP ping (e.g. legacy
                    // call site). Still better than showing 0 ms.
                    cfg.LatencyMs = httpLatencyMs;
                }
                cfg.LastError = null;

                // v2.14.3: optional bandwidth measurement via 5 MB download through same SOCKS proxy.
                if (MeasureBandwidth)
                {
                    var (bwOk, mbps, bwErr) = await MeasureBandwidthViaSocksAsync(socksPort, overallCts.Token);
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
                // Don't clobber a working TCP+TLS status — only downgrade if it was Ok.
                if (cfg.Status == FreeConfigStatus.Ok || cfg.Status == FreeConfigStatus.Slow)
                    cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LastError = httpErr ?? "http failed";

                var stderrSnip = TrimSnippet(stderrBuffer.ToString(), 200);
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
            // Cleanup: kill process + delete temp config.
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

    private static string TrimSnippet(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>
    /// v2.14.3: Measure download throughput via 5 MB file over SOCKS proxy.
    /// Tries Cloudflare → Hetzner → OVH in sequence. Returns (ok, mbps, err).
    /// Adds ~3-8s per config depending on pipe bandwidth.
    /// </summary>
    private static async Task<(bool ok, double mbps, string? err)> MeasureBandwidthViaSocksAsync(
        int socksPort, CancellationToken ct)
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        // Test URLs in priority order. Each returns ≥5 MB (we read exactly 5 MB and close).
        var urls = new[]
        {
            "https://speed.cloudflare.com/__down?bytes=5242880",  // Cloudflare, global
            "https://proof.ovh.net/files/10Mb.dat",               // OVH, EU
            "https://ash-speed.hetzner.com/100MB.bin",            // Hetzner, EU (reads only 5 MB)
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
                const long target = 5_242_880L; // 5 MB
                while (total < target)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    total += n;
                }
                sw.Stop();

                if (total < 1_000_000) continue; // too little data, probably cached/error
                if (sw.ElapsedMilliseconds < 100) continue; // too fast, likely local cache hit

                var mbps = (total * 8.0 / 1_000_000.0) / (sw.ElapsedMilliseconds / 1000.0);
                return (true, mbps, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* try next URL */ }
        }
        return (false, 0, "all bandwidth URLs failed");
    }

    /// <summary>
    /// Build a minimal sing-box JSON: SOCKS inbound on loopback + single VLESS outbound.
    /// Route everything through the VLESS outbound (no split tunneling, no profiles).
    /// </summary>
    private static string BuildSingleOutboundConfig(VlessServerEntry s, int socksPort, int clashPort)
    {
        // Use JsonNode to build the config cleanly.
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

        // TLS / Reality
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

        // Transport (tcp is implicit, grpc/ws need explicit block).
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

        // sing-box 1.13.3 quirk: DNS server with detour:"direct" is FATAL if the direct
        // outbound is "empty" (just {type:direct,tag:direct}). Workaround: separate
        // 'dns-direct' outbound with udp_fragment:true so it's non-empty.
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "udp", ["tag"] = "dns-google", ["server"] = "1.1.1.1", ["detour"] = "dns-direct-out" },
                },
                ["final"] = "dns-google",
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
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
                outbound,
                // Dedicated non-empty direct outbound for DNS detour (udp_fragment:true makes it non-empty in 1.13).
                new JsonObject { ["type"] = "direct", ["tag"] = "dns-direct-out", ["udp_fragment"] = true },
            },
            ["route"] = new JsonObject
            {
                ["final"] = "proxy",
                ["default_domain_resolver"] = new JsonObject { ["server"] = "dns-google" },
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
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

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>Find a random free TCP port on loopback.</summary>
    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Poll the loopback port until something accepts a connection, or timeout.</summary>
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

    /// <summary>Make an HTTP GET through a local SOCKS5 proxy. Returns (ok, latency_ms, err).</summary>
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

            // Trace endpoint format: multiline "key=value". Look for "ip=" line with a non-local IP.
            if (!body.Contains("ip=", StringComparison.Ordinal))
                return (false, 0, "bad response");

            // Extract ip= line and verify it's a valid public-looking IP (not localhost / private).
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

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 100.64.0.0/10 — CGNAT
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        return false;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;
}
