using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Typed failure phase of a deep verification pass (audit batch-1 R1). Replaces the
/// error-string heuristics consumers used to parse: local/infra phases say NOTHING
/// about the server; only <see cref="ProxiedHttp"/>/<see cref="Timeout"/> are
/// server-meaningful. <see cref="None"/> = legacy/unset — consumers fall back to
/// the <see cref="DeepVerifyResult.Error"/> string heuristic.
/// </summary>
public enum DeepVerifyFailurePhase
{
    /// <summary>Unset (legacy results / unexpected errors) — fall back to Error-string heuristics.</summary>
    None = 0,
    /// <summary>Refused before any probe (placeholder credentials) — not a server verdict.</summary>
    Precondition,
    /// <summary>sing-box binary missing or the spawn itself failed — local infra.</summary>
    LocalSpawn,
    /// <summary>sing-box started but the local SOCKS port never bound (config rejected / crash) — local infra.</summary>
    SocksBind,
    /// <summary>Tunnel came up locally but the control HTTP request through it failed — server-meaningful.</summary>
    ProxiedHttp,
    /// <summary>Overall verify budget exhausted — treated as a proxied-path failure.</summary>
    Timeout,
    /// <summary>Caller cancelled — inconclusive.</summary>
    Cancelled,
    /// <summary>This build/verifier cannot test the protocol (AWG or xhttp without the lx core,
    /// naive without libcronet) — explicitly untestable, NEVER a server failure.</summary>
    UnsupportedByVerifier,
}

/// <summary>Outcome of a deep verification pass through a spawned sing-box.</summary>
public sealed record DeepVerifyResult(
    bool Ok,
    int HttpLatencyMs,
    double? BandwidthMbps,
    string? Error,
    DeepVerifyFailurePhase FailurePhase = DeepVerifyFailurePhase.None,
    // R4: blocked-target canary conclusion, probed THROUGH the same spawned
    // sing-box SOCKS (via-VPN by construction). Unknown when canaries didn't
    // run (no budget / stale list / control failed) — additive, back-compat.
    PhaseOutcome BlockedCanary = PhaseOutcome.Unknown)
{
    public static DeepVerifyResult Failed(string error) => new(false, 0, null, error);

    public static DeepVerifyResult Failed(string error, DeepVerifyFailurePhase phase)
        => new(false, 0, null, error, phase);
}

/// <summary>
/// Generic VLESS deep verifier for Servers/Subscriptions tabs (v2.15.3).
/// Spawns a temporary sing-box with a single VLESS outbound + local SOCKS
/// inbound, then performs HTTP GET through it (optionally followed by a
/// 5 MB bandwidth probe). Returns structured <see cref="DeepVerifyResult"/>.
///
/// The sing-box spawn/probe plumbing (port-bind wait, SOCKS HTTP probe,
/// bandwidth, IP classification) lives in the shared <see cref="DeepVerifyProbe"/>
/// (#4 cleanup 2026-07-10 — the "consolidation possible in a future refactor"
/// this doc used to promise). What stays here is this verifier's OWN result
/// shape — <see cref="DeepVerifyResult"/> for ServerViewModel — which is the
/// part FreeConfigs deliberately mutates differently (its own status enum).
/// </summary>
public sealed class VlessDeepVerifier
{
    private readonly ILogger _logger;
    private readonly string _singBoxPath;
    private readonly IProcessRunner _runner;

    private static readonly TimeSpan SingBoxWarmup = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan OverallTimeout = DeepVerifyConstants.OverallTimeout;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    // P2 (2026-07-10): per-extra-concurrent-spawn slack added to the SOCKS-bind
    // wait. N sing-box processes spawned at once contend for CPU, so on a slow
    // VM (brat, live 2026-07-09) the SOCKS port doesn't bind inside the flat
    // 1500ms warmup and the verify false-reports "port never bound" → "untested"
    // for otherwise-fine servers. Scaling the wait by concurrency (the load
    // signal we already have) fixes the under-report; still bounded by the 12s
    // OverallTimeout, so a genuinely-dead spawn never hangs the pass.
    private static readonly TimeSpan WarmupPerConcurrencySlack = TimeSpan.FromMilliseconds(300);

    public int MaxConcurrency { get; set; } = 5;

    /// <summary>Effective SOCKS-bind wait: the flat warmup plus slack for each
    /// EXTRA concurrent spawn (MaxConcurrency=1 → just the flat warmup).</summary>
    internal TimeSpan EffectiveSocksBindWait =>
        SingBoxWarmup + WarmupPerConcurrencySlack * Math.Max(0, MaxConcurrency - 1);

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
                onOneDone(entry, DeepVerifyResult.Failed(ex.GetType().Name));   // phase None — unexpected, fall back to heuristics
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
            return DeepVerifyResult.Failed($"placeholder credential: {placeholderField}",
                DeepVerifyFailurePhase.Precondition);
        }

        // R1 (audit batch-1, OPEN-DEFECTS AWG/XHTTP parity): pre-R1 an AWG entry fell
        // into BuildVlessOutbound (garbage config → bind fail) and an xhttp entry was
        // probed over plain TCP (transport silently dropped → false ProtocolBlocked).
        // When the bundled core carries the tag we now verify them for real (endpoint /
        // xhttp transport below); when it doesn't, return an explicit typed
        // UnsupportedByVerifier — never condemn the server for our own gap.
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

        if (!IsAvailable)
        {
            _logger.Warning("[VlessDeepVerifier] {Name}: sing-box binary missing at {Path}", label, _singBoxPath);
            return DeepVerifyResult.Failed("sing-box binary missing", DeepVerifyFailurePhase.LocalSpawn);
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
                return DeepVerifyResult.Failed("naive needs libcronet (Windows/Linux only)",
                    DeepVerifyFailurePhase.UnsupportedByVerifier);
            }
            SingBoxManager.TryColocateCronet(_singBoxPath, AppContext.BaseDirectory, _logger);
        }

        // r9 P2: flag the probe window so RuntimeStatusDetector doesn't read our
        // own spawned sing-box as a live tunnel (false "Connected via service").
        using var probeScope = DeepVerifyProbe.BeginProbeScope();

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
                return DeepVerifyResult.Failed("sing-box spawn failed", DeepVerifyFailurePhase.LocalSpawn);
            }

            handle.ErrorLine += (_, line) =>
            {
                if (line != null) stderrBuffer.Append(line).Append('\n');
            };

            _logger.Debug("[VlessDeepVerifier] {Name}: sing-box spawned pid={Pid} socks={SocksPort}", label, handle.Pid, socksPort);

            if (!await DeepVerifyProbe.WaitForPortBoundAsync(socksPort, EffectiveSocksBindWait, overallCts.Token))
            {
                var snip = DeepVerifyProbe.TrimSnippet(stderrBuffer.ToString(), 80);
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

            // Control HTTP passed → the server WORKS. Everything below (canary,
            // bandwidth) is best-effort enrichment: a budget-timeout inside it must
            // NEVER downgrade this success to a Timeout failure (that false-fails a
            // working-but-slow server — live-caught on brat 2026-07-09, Germany AWG:
            // canary ran, then bandwidth hit the 12s budget and discarded the pass).

            // R4: blocked-target canaries through the SAME tunnel (via-VPN — the ISP
            // only ever sees the tunnel). ProbeCanariesViaSocksAsync swallows a
            // budget-timeout and returns Unknown, so it can't throw out of here.
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
                    // Our overall budget drained during the OPTIONAL bandwidth probe.
                    // Report bandwidth unmeasured; the HTTP+canary success stands.
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
            // Caller cancellation, not the overall budget — inconclusive, not a verdict.
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
            return DeepVerifyResult.Failed(ex.GetType().Name);   // phase None — fall back to heuristics
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

        // R1 AWG parity: an AWG server is an ENDPOINT (top-level "endpoints", lx core),
        // not an outbound — pre-R1 it fell into BuildVlessOutbound and produced a config
        // sing-box rejects. Reuse the exact shipped builder (ConfigGenerator) so the
        // deep-verify config matches what a real connect would run; route.final="proxy"
        // resolves the endpoint tag the same way the live config's routes do.
        JsonNode? awgEndpoint = null;
        JsonObject? outbound = null;
        if (protocol is "amneziawg" or "awg")
        {
            awgEndpoint = System.Text.Json.JsonSerializer.SerializeToNode(
                ConfigGenerator.BuildAmneziaWgEndpoint(s, "proxy"));
        }
        else
        {
            outbound = protocol switch
            {
                "hysteria2"   => BuildHysteria2Outbound(s),
                "hy2"         => BuildHysteria2Outbound(s),
                "tuic"        => BuildTuicOutbound(s),
                "shadowsocks" => BuildShadowsocksOutbound(s),
                "ss"          => BuildShadowsocksOutbound(s),
                "naive"       => BuildNaiveOutbound(s),   // r7 #5: was falling to vless → false-fail for valid naive
                _             => BuildVlessOutbound(s),
            };
        }

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
            ["outbounds"] = outbound != null
                ? new JsonArray
                {
                    (JsonNode?)outbound,
                    (JsonNode?)new JsonObject { ["type"] = "direct", ["tag"] = "dns-direct-out", ["udp_fragment"] = true },
                }
                : new JsonArray
                {
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

        // R1 AWG parity: the AWG proxy is a top-level endpoint (tag "proxy"), consumed
        // by route.final exactly like the live config's routes consume it.
        if (awgEndpoint != null)
            root["endpoints"] = new JsonArray { awgEndpoint };

        // Pass null (uses JsonSerializerOptions.Default with reflection-based resolver).
        // Custom `new JsonSerializerOptions { WriteIndented = false }` lacks a TypeInfoResolver
        // and triggers "JsonSerializerOptions instance must specify a TypeInfoResolver" on
        // some .NET 8 runtimes (notably ubuntu-latest CI) when JsonValueCustomized<string>
        // tries to serialize the alpn array entries (TUIC). Defaults are already
        // WriteIndented=false so behaviour is identical.
        return root.ToJsonString();
    }

    /// <summary>
    /// R4: probe the blocked-target canaries through the tunnel's SOCKS. A target
    /// "passes" on ANY HTTP response (even 403/404 — bytes flowed through to the
    /// blocked host, bypass proven); timeout/reset/connect failure = failed (the
    /// RU block signature via a non-working transport). Per-target 4s cap, run in
    /// parallel; an overall-budget cancellation mid-canary is swallowed and
    /// reported Unknown (inconclusive), never a verdict. URLs are logged
    /// redacted (scheme+host).
    /// </summary>
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
                    passed = (int)resp.StatusCode < 500;   // any real answer = bytes flowed through
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
            // Overall budget drained mid-canary — inconclusive, never a verdict.
            return PhaseOutcome.Unknown;
        }
        catch (Exception ex)
        {
            _logger.Debug("[VlessDeepVerifier] {Name}: canary stage error {Err} — inconclusive", label, ex.GetType().Name);
            return PhaseOutcome.Unknown;
        }
    }

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
        else if (transportType == "xhttp")
        {
            // R1 xhttp parity — mirrors ConfigGenerator.BuildTransportConfig's xhttp
            // branch (host is TOP-LEVEL, schema verified vs `sing-box-lx check`).
            // Pre-R1 this fell through: the xhttp server got probed over plain TCP
            // and false-failed. Callers gate on SingBoxFeatures.XhttpAvailable.
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

            // XHTTP is incompatible with XTLS-Vision — drop a stray flow so the
            // config stays valid (same rule as ConfigGenerator.BuildVlessOutbound).
            outbound["flow"] = null;
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
