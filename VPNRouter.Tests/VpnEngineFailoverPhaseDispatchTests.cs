// P02 FAIL-1 (2026-07-29) — phase-aware failover dispatch regression tests.
//
// Pins the fix for the single-slot AutoFailoverEngine delegate collision:
// pre-start WireFailover installed an UNSAFE restart delegate (no teardown,
// no gate, no session guard) and the ??= slot meant the later post-start
// WireFailoverWithStop was a no-op — every post-start failover reused the
// unsafe delegate. The fix makes the single stored delegate phase-aware:
// a volatile bool (_postStartPhase) set by OnSingBoxStarted dispatches to
// the safe gated path (ExecuteProbeFailoverRestartAsync) post-start and
// the gate-free direct path pre-start.
//
// Two pins, matching the brief's fallback shape (the wire methods live on the
// private nested VpnEngineStartupHost and are only reachable through a full
// StartAsync — sing-box + network + OS — so the wiring itself cannot be
// exercised behaviorally cross-platform):
//
//   1. Behavioral dispatcher pin: invoking the phase-aware delegate post-start
//      routes through the safe teardown path. Discriminator:
//      NullWindowsDnsHardening.RestoreCount — TeardownInternal calls
//      _dnsHardening.Restore, so RestoreCount >= 1 proves the gated path ran.
//
//   2. Source-shape wiring pin: both WireFailover and WireFailoverWithStop
//      forward to the SAME WireFailoverCore, and the single stored delegate
//      routes through ExecuteFailoverRestartAsync (not StartAsyncInternal
//      directly) — so the ??= collision is harmless. This is the pin that
//      fails if the stale pre-start lambda is restored.
//
// Brief: plans/phase1-audit-p02-failover-wiring-2026-07-29.md.

#nullable enable

using System.IO;
using System.Text.RegularExpressions;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Regression tests for the P02 FAIL-1 phase-aware failover dispatch.
/// Cross-platform (no sing-box binary, no OS mutation).
/// </summary>
public sealed class VpnEngineFailoverPhaseDispatchTests
{
    // ─── Inline stubs (mirrors VpnEngineStartAsyncSeamTests pattern) ─────

    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(NullWindowsDnsHardening dns) =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: dns);
#pragma warning restore CS0618

    /// <summary>
    /// Settings that trigger an early phase-2 throw (empty servers in
    /// subscribe mode) — cross-platform safe, no OS shell-outs.
    /// </summary>
    private static AppSettings BuildEmptyServersSettings() =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
            },
            Vless = new VlessConfig(),
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    // ─── 1. Behavioral pin: post-start routes through safe teardown ──────

    /// <summary>
    /// Post-start phase must route through ExecuteProbeFailoverRestartAsync —
    /// the safe gated path that calls TeardownInternal. Pre-fix, the stored
    /// delegate was the unsafe pre-start one (no teardown) — RestoreCount
    /// stayed 0. On a fresh idle engine (no session), the safe path runs
    /// teardown then aborts (session null) → returns false.
    /// </summary>
    [Fact]
    public async Task ExecuteFailoverRestart_PostStartPhase_RoutesThroughSafeTeardownPath()
    {
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settings = BuildEmptyServersSettings();

        engine.EnterPostStartPhase();

        var result = await engine.ExecuteFailoverRestartAsync(settings, CancellationToken.None);

        Assert.False(result, "fresh idle engine has no session — safe path must abort after teardown");
        Assert.False(engine.IsRunning);
        Assert.True(dns.RestoreCount >= 1,
            "TeardownInternal must run on the safe gated path (RestoreCount proves teardown executed). " +
            $"Actual RestoreCount: {dns.RestoreCount}");
    }

    [Fact]
    public async Task ExecuteProbeFailoverRestart_StaleCapturedSettings_ReturnsFalseWithoutTeardown()
    {
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settingsA = BuildEmptyServersSettings();
        var settingsB = BuildEmptyServersSettings();

        engine.EnterPostStartPhase();
        engine.ResetFailoverContext(settingsB);

        var result = await engine.ExecuteProbeFailoverRestartAsync(settingsA, CancellationToken.None);

        Assert.False(result, "stale captured settings must be rejected when active failover context differs");
        Assert.Equal(0, dns.RestoreCount);
    }

    [Fact]
    public async Task ExecuteFailoverRestart_PreStart_StaleCapturedSettings_ReturnsFalseBeforeStartAsyncInternal()
    {
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settingsA = BuildEmptyServersSettings();
        var settingsB = BuildEmptyServersSettings();

        // pre-start phase: _postStartPhase is false
        engine.ResetFailoverContext(settingsB);

        var result = await engine.ExecuteFailoverRestartAsync(settingsA, CancellationToken.None);

        Assert.False(result, "pre-start failover restart must be rejected when captured settings do not match active context");
        Assert.False(engine.IsRunning);
    }

    // ─── 2. Source-shape pin: both wire methods install the same delegate ─

    /// <summary>
    /// Pins the FAIL-1 root cause at the wiring level. The wire methods are on
    /// the private nested VpnEngineStartupHost (reachable only via a full
    /// StartAsync), so this is a source-string pin — matching the
    /// CoreAuditPhaseCTests precedent for paths that aren't cleanly unit-testable.
    /// Two invariants make the single <c>??=</c> slot safe:
    /// <list type="number">
    ///   <item>Both <c>WireFailover</c> (pre-start) and <c>WireFailoverWithStop</c>
    ///   (post-start) forward to the SAME <c>WireFailoverCore</c> — the
    ///   <c>??=</c> collision is harmless because both install identical code.</item>
    ///   <item>The single stored restart delegate routes through the phase-aware
    ///   <c>ExecuteFailoverRestartAsync</c> dispatcher — NOT directly through
    ///   <c>StartAsyncInternal</c> (the stale pre-start path that skipped
    ///   teardown/gate/session-guard).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void FailoverWiring_BothMethods_InstallSamePhaseAwareDelegate()
    {
        var src = LoadVpnEngineSource();

        // 1. Both wire methods forward to the same core. If the old collision is
        //    restored (WireFailover inlines its own `new AutoFailoverEngine(...)`
        //    with an unsafe lambda and the post-start wire becomes a ??= no-op),
        //    WireFailover no longer matches this forward shape → fails here.
        Assert.True(
            Regex.IsMatch(src,
                @"public\s+AutoFailoverEngine\s+WireFailover\s*\(\s*ConfigSanityCheck\s+sanityCheck\s*\)\s*=>\s*WireFailoverCore\s*\(\s*sanityCheck\s*\)\s*;"),
            "WireFailover (pre-start) must forward to WireFailoverCore so the ??= slot installs the " +
            "SAME delegate as the post-start wire (P02 FAIL-1: a pre-start-only lambda survived the " +
            "??= collision and every post-start failover reused the unsafe no-teardown restart).");
        Assert.True(
            Regex.IsMatch(src,
                @"public\s+AutoFailoverEngine\s+WireFailoverWithStop\s*\(\s*ConfigSanityCheck\s+sanityCheck\s*\)\s*=>\s*WireFailoverCore\s*\(\s*sanityCheck\s*\)\s*;"),
            "WireFailoverWithStop (post-start) must forward to the same WireFailoverCore as WireFailover " +
            "(P02 FAIL-1: both wire methods must install one shared phase-aware delegate).");

        // 2. The shared delegate is phase-aware: it routes through
        //    ExecuteFailoverRestartAsync (which reads the live _postStartPhase
        //    flag) and does NOT call StartAsyncInternal directly. If a stale
        //    pre-start lambda `(ct) => StartAsyncInternal(...)` is restored inside
        //    the core, this fails — that lambda bypasses the phase dispatch and
        //    re-introduces the no-teardown post-start failover.
        var core = ExtractWireFailoverCore(src);
        var cleanCore = StripComments(core);
        Assert.Contains("ExecuteFailoverRestartAsync", cleanCore);
        Assert.DoesNotContain("StartAsyncInternal", cleanCore);

        // 3. NIGHT-06: The restart closure and AutoFailoverEngine constructor must share ONE
        //    captured settings local variable (e.g. `settings`) rather than re-evaluating or calling CapturedSettings()
        //    inside the closure. Stripped of comments so dummy comments cannot satisfy the pin.
        var match = Regex.Match(cleanCore, @"new\s+AutoFailoverEngine\s*\(\s*(?<settingsVar>[A-Za-z0-9_]+)\s*,");
        Assert.True(match.Success, "WireFailoverCore must construct AutoFailoverEngine with a settings local variable.");
        var settingsVarName = match.Groups["settingsVar"].Value;
        Assert.False(string.IsNullOrWhiteSpace(settingsVarName), "Settings variable name must not be empty.");
        Assert.DoesNotContain("CapturedSettings()", match.Value);

        Assert.True(
            Regex.IsMatch(cleanCore,
                $@"\bExecuteFailoverRestartAsync\s*\(\s*{Regex.Escape(settingsVarName)}\s*,\s*[A-Za-z0-9_]+(?:\s*,\s*[A-Za-z0-9_]+)?\s*\)"),
            $"Restart delegate must pass the same '{settingsVarName}' local instance to ExecuteFailoverRestartAsync " +
            "so pool and restart closure share the exact same object reference.");
    }

    // ─── source-shape helpers (mirrors CoreAuditPhaseCTests) ─────────────

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var noLine = Regex.Replace(noBlock, @"//.*", "");
        return noLine;
    }

    private static string LoadVpnEngineSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("Could not locate VPNRouter.Core/Services/VpnEngine.cs");
    }

    private static string ExtractWireFailoverCore(string src)
    {
        // Target the DECLARATION (preceded by its return type) so the two
        // `=> WireFailoverCore(sanityCheck)` forwarders aren't mistaken for it.
        var m = Regex.Match(src, @"AutoFailoverEngine\s+WireFailoverCore\s*\(");
        Assert.True(m.Success,
            "WireFailoverCore declaration not found — both wire methods must share one core so the " +
            "??= slot installs a single phase-aware delegate (P02 FAIL-1).");
        var brace = src.IndexOf('{', m.Index + m.Length);
        Assert.True(brace >= 0, "WireFailoverCore must have a body installing the shared delegate.");
        int depth = 1, i = brace + 1;
        while (i < src.Length && depth > 0) { if (src[i] == '{') depth++; else if (src[i] == '}') depth--; i++; }
        return src.Substring(brace, i - brace);
    }
}
