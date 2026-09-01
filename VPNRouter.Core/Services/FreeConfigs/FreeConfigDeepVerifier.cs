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

    /// <summary>Time to wait for sing-box to bind SOCKS before we attempt HTTP.</summary>
    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);

    /// <summary>r8: per-extra-concurrent-spawn slack added to the SOCKS-bind wait —
    /// same slow-hardware fix VlessDeepVerifier got in v2.47.0-r4 (N concurrent
    /// sing-box spawns contend for CPU and the flat 1500ms falsely reports
    /// "didn't bind" on a slow VM). Still bounded by the per-config OverallTimeout.</summary>
    private static readonly TimeSpan WarmupPerConcurrencySlack = TimeSpan.FromMilliseconds(300);

    /// <summary>Overall per-config timeout.</summary>
    private static readonly TimeSpan OverallTimeout = DeepVerifyConstants.OverallTimeout;

    /// <summary>HTTP request timeout (through SOCKS proxy).</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How many configs to verify in parallel.</summary>
    public int MaxConcurrency { get; set; } = 5;

    /// <summary>Effective SOCKS-bind wait: flat warmup plus slack per EXTRA concurrent spawn.</summary>
    internal TimeSpan EffectiveSocksBindWait =>
        SingBoxWarmup + WarmupPerConcurrencySlack * Math.Max(0, MaxConcurrency - 1);

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

        // r9 P2: flag the probe window so RuntimeStatusDetector doesn't read our
        // own spawned sing-box as a live tunnel (false "Connected via service").
        using var probeScope = DeepVerifyProbe.BeginProbeScope();

        var socksPort = NetPortUtil.FindFreePort();
        var clashPort = NetPortUtil.FindFreePort();
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
            if (!await DeepVerifyProbe.WaitForPortBoundAsync(socksPort, EffectiveSocksBindWait, overallCts.Token))
            {
                var stderrSnip = DeepVerifyProbe.TrimSnippet(stderrBuffer.ToString(), 300);
                cfg.LastError = $"sing-box didn't bind: {DeepVerifyProbe.TrimSnippet(stderrSnip, 80)}";
                _logger.Warning("[DV] {host}:{port} [{cc}] → didn't bind. stderr: {err}",
                    cfg.Host, cfg.Port, cc, stderrSnip);
                return;
            }

            // 4. HTTP GET through SOCKS proxy.
            var (httpOk, httpLatencyMs, httpErr) = await DeepVerifyProbe.ProbeViaSocksAsync(socksPort, HttpTimeout, overallCts.Token);

            if (httpOk)
            {
                cfg.Status = FreeConfigStatus.Verified;
                // v2.29.0 Phase 3C: stamp the successful Deep Verify time so
                // the next search session can skip re-verifying this entry
                // if it ran within the last 6 hours. Saves 5-15 s per
                // already-known-working config in the cached re-test pass.
                cfg.LastDeepVerifyAt = DateTime.UtcNow;
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
                // v2.39.0 (audit P0): also downgrade Verified on a failed HTTP
                // probe — a previously-Verified entry that now fails must not
                // keep showing Verified to non-merge callers (live search).
                // The Saved-recheck merge separately restores Verified + a
                // failed-last-check marker via LastDeepVerifyAt; this covers the
                // paths that don't run the merge.
                if (cfg.Status == FreeConfigStatus.Ok || cfg.Status == FreeConfigStatus.Slow
                    || cfg.Status == FreeConfigStatus.Verified)
                    cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LastError = httpErr ?? "http failed";

                var stderrSnip = DeepVerifyProbe.TrimSnippet(stderrBuffer.ToString(), 200);
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

    /// <summary>
    /// Build a minimal sing-box JSON: SOCKS inbound on loopback + single VLESS outbound.
    /// Route everything through the VLESS outbound (no split tunneling, no profiles).
    ///
    /// <para>v2.32.0 (Android Bug #1): exposed as <c>internal</c> and given a
    /// nullable <paramref name="clashPort"/> so the Android libbox-backed
    /// verifier can reuse the same builder. When <paramref name="clashPort"/>
    /// is null we omit the <c>experimental.clash_api</c> block — Android's
    /// verify box runs alongside the main VPN box which may already own
    /// :9090 for hot-reload, and the verify probe doesn't need the Clash
    /// RPC anyway (we kill the box at the end of <see cref="VerifyOneAsync"/>
    /// instead of hot-reloading it).</para>
    /// </summary>
    internal static string BuildSingleOutboundConfig(VlessServerEntry s, int socksPort, int? clashPort)
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
        //
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
                    (JsonNode?)new JsonObject { ["type"] = "https", ["tag"] = "dns-google", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["detour"] = "dns-direct-out" },
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
                // Dedicated non-empty direct outbound for DNS detour (udp_fragment:true makes it non-empty in 1.13).
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
        };

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

        // Phase 6 — defaults are WriteIndented=false anyway; using the
        // parameterless overload sidesteps the .NET 10 "options must
        // specify a TypeInfoResolver" throw without any wire-format change.
        return root.ToJsonString();
    }

    /// <summary>Poll the loopback port until something accepts a connection, or timeout.</summary>
}
