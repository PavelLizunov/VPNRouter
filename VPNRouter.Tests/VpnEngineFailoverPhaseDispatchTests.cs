#nullable enable

using System.IO;
using System.Text.RegularExpressions;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VpnEngineFailoverPhaseDispatchTests
{
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
#pragma warning disable CS0067 // stub implements the interface; the events are never raised
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
#pragma warning restore CS0067
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

        engine.ResetFailoverContext(settingsB);

        var result = await engine.ExecuteFailoverRestartAsync(settingsA, CancellationToken.None);

        Assert.False(result, "pre-start failover restart must be rejected when captured settings do not match active context");
        Assert.False(engine.IsRunning);
    }

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
