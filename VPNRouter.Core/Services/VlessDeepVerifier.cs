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

    private const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    public int MaxConcurrency { get; set; } = 5;

    public VlessDeepVerifier(ILogger logger)
    {
        _logger = logger;
        _singBoxPath = AppPaths.SingBoxExePath;
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
        if (!IsAvailable)
            return DeepVerifyResult.Failed("sing-box binary missing");

        var socksPort = FindFreePort();
        var clashPort = FindFreePort();
        string? tmpConfigPath = null;
        Process? process = null;
        var stderrBuffer = new StringBuilder(capacity: 2048);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);

        try
        {
            var configJson = BuildSingleOutboundConfig(entry, socksPort, clashPort);
            tmpConfigPath = Path.Combine(Path.GetTempPath(), $"sb-dv-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tmpConfigPath, configJson, overallCts.Token);

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
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrBuffer.Append(e.Data).Append('\n');
            };

            if (!process.Start())
                return DeepVerifyResult.Failed("sing-box spawn failed");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!await WaitForPortBoundAsync(socksPort, SingBoxWarmup, overallCts.Token))
            {
                var snip = TrimSnippet(stderrBuffer.ToString(), 80);
                return DeepVerifyResult.Failed(string.IsNullOrWhiteSpace(snip)
                    ? "sing-box didn't bind"
                    : $"sing-box: {snip}");
            }

            var (httpOk, httpLatencyMs, httpErr) = await ProbeViaSocksAsync(socksPort, overallCts.Token);
            if (!httpOk)
                return DeepVerifyResult.Failed(httpErr ?? "http failed");

            double? mbps = null;
            if (measureBandwidth)
            {
                var (bwOk, measuredMbps, _) = await MeasureBandwidthViaSocksAsync(socksPort, overallCts.Token);
                if (bwOk) mbps = measuredMbps;
            }

            return new DeepVerifyResult(true, httpLatencyMs, mbps, null);
        }
        catch (OperationCanceledException)
        {
            return DeepVerifyResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            return DeepVerifyResult.Failed(ex.GetType().Name);
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

    // ─── Helpers (mirror FreeConfigDeepVerifier, kept here to keep this class standalone) ─────

    private static string BuildSingleOutboundConfig(VlessServerEntry s, int socksPort, int clashPort)
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

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    private static bool IsPrivateOrLoopback(IPAddress ip)
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

    private static string TrimSnippet(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;
}
