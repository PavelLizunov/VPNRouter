using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class StartupPipelineTests : IDisposable
{
    private readonly InMemorySettingsStore _store = new();
    private readonly bool _wasSafeMode;

    public StartupPipelineTests()
    {
        _wasSafeMode = SafeMode.Enabled;
        SafeMode.Enabled = true;
    }

    public void Dispose()
    {
        SafeMode.Enabled = _wasSafeMode;
    }

    private static VlessServerEntry MakeServer(
        string name, string host, int port = 443) =>
        new()
        {
            Name = name,
            Server = host,
            Port = port,
            Uuid = "11111111-2222-3333-4444-" + host.GetHashCode().ToString("X").PadLeft(12, '0'),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                ShortId = "d86e92a0c6dd2271"
            }
        };

    private static AppSettings BuildBaseSettings(string configMode = "generated") =>
        new()
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = configMode,
                RoutingMode = "split",
                Subscriptions = new List<SubscriptionEntry>()
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig(),
            ActiveProfile = "TestProfile"
        };

    [Fact]
    public async Task ResolveProfile_NoActiveProfileInSplitMode_ThrowsInvariantViolation()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry> { MakeServer("m", "1.2.3.4") };
        settings.Vless.ActiveServer = "m";
        settings.ActiveProfile = null;
        settings.App.RoutingMode = "split";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(
                new StartupContext(settings, StartupMode.HotReload),
                TestContext.Current.CancellationToken));

        Assert.Contains("No active profile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveServers_EmptyConfig_ThrowsWithActionableMessage()
    {
        var settings = BuildBaseSettings(configMode: "subscribe");
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(
                new StartupContext(settings, StartupMode.HotReload),
                TestContext.Current.CancellationToken));

        Assert.NotNull(ex.Message);
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public async Task GenerateConfig_PlaceholderActiveServer_FallsBackToSubscription()
    {
        const string placeholderServer = "195.135.255.216";
        const string placeholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
        const string placeholderShortId = "78ca7952";

        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("de-01", "104.194.156.93", 443)
                }
            }
        };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "khunrath_ln",
                Server = placeholderServer,
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "yahoo.com",
                    Fingerprint = "firefox",
                    PublicKey = placeholderPubkey,
                    ShortId = placeholderShortId
                }
            }
        };
        settings.Vless.ActiveServer = "khunrath_ln";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ConfigJson);

        Assert.DoesNotContain(placeholderServer, result.ConfigJson);
        Assert.Contains("104.194.156.93", result.ConfigJson);

        Assert.Equal("de-01", settings.Vless.ActiveServer);
    }

    [Fact]
    public async Task HotReload_ReturnsConfigJsonAndProfile()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.EarlyReturn);
        Assert.NotNull(result.ConfigJson);
        Assert.NotEmpty(result.ConfigJson!);
        Assert.NotNull(result.Profile);
        Assert.False(string.IsNullOrEmpty(result.Profile!.Name));

        Assert.Null(host.SetSingBox);

        Assert.Null(host.SetFirewall);
        Assert.Null(host.SetEtw);
        Assert.Null(host.SetHealth);
    }

    [Fact]
    public async Task SetupFirewall_NoBlockOnFail_SkipsRuleCreation()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";
        settings.ActiveProfile = "TestProfile";
        settings.App.RoutingMode = "split";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(host.SetFirewall);
    }

    [Fact]
    public async Task SetupFirewall_BlockOnFail_SplitRouting_PassesIsFullTunnelFalse()
    {
        var (thrown, host) = await RunFirewallWalkAsync("split", "Discord_Privacy");

        Assert.IsType<FirewallCapturedSentinelException>(thrown);
        Assert.Equal(1, host.CapturedFirewall.CreateBlockRulesCount);
        Assert.False(host.CapturedFirewall.LastIsFullTunnel);
    }

    [Fact]
    public async Task SetupFirewall_FullTunnel_SelectedProfileBlockOnFail_ArmsKillSwitch()
    {
        var (thrown, host) = await RunFirewallWalkAsync("full", "Discord_Privacy");

        Assert.IsType<FirewallCapturedSentinelException>(thrown);
        Assert.Equal(1, host.CapturedFirewall.CreateBlockRulesCount);
        Assert.True(host.CapturedFirewall.LastIsFullTunnel);
    }

    [Fact]
    public async Task SetupFirewall_FullTunnel_NoBlockIntent_DoesNotArm()
    {
        var (thrown, host) = await RunFirewallWalkAsync("full", "Browsers");

        Assert.IsNotType<FirewallCapturedSentinelException>(thrown);
        Assert.Equal(0, host.CapturedFirewall.CreateBlockRulesCount);
    }

    private async Task<(Exception? Thrown, TestStartupHost Host)> RunFirewallWalkAsync(
        string routingMode, string? activeProfile)
    {
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.FlushDnsOnStart = false;
        settings.App.BypassRussianTraffic = false;
        settings.ActiveProfile = activeProfile;
        settings.Vless.Servers = new List<VlessServerEntry> { MakeServer("main", "104.194.156.93", 443) };
        settings.Vless.ActiveServer = "main";

        var fakeBin = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), $"vpnrouter-fake-singbox-{Guid.NewGuid():N}.exe")
            : AppPaths.SingBoxExePath;
        var createdFakeBin = !File.Exists(fakeBin);
        if (createdFakeBin)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fakeBin)!);
            File.WriteAllText(fakeBin, "fake");
        }
        settings.SingBox.ExecutablePath = fakeBin;

        var host = new TestStartupHost();
        host.CapturedFirewall.AbortAfterCapture = true;
        var pipeline = new StartupPipeline(host, _store);

        var prevSafeMode = SafeMode.Enabled;
        SafeMode.Enabled = false;
        try
        {
            var thrown = await Record.ExceptionAsync(async () =>
                await pipeline.ExecuteAsync(
                    new StartupContext(settings, StartupMode.ColdStart, SkipVpnConflictCheck: true),
                    TestContext.Current.CancellationToken));
            return (thrown, host);
        }
        finally
        {
            SafeMode.Enabled = prevSafeMode;
            if (createdFakeBin)
            {
                try { File.Delete(fakeBin); } catch { }
            }
        }
    }

    [Fact]
    public async Task PreStartChecks_SkippedInHotReloadMode()
    {
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.EarlyReturn);
        Assert.False(host.AutoFailoverInvoked);
        Assert.False(host.SanityCheckEnsured);
    }

    [Fact]
    public async Task ResolveProfile_CustomApps_Injected()
    {
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.CustomApps = new List<string> { "myapp.exe" };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Profile);
        Assert.NotNull(result.Profile!.Processes);
    }

    [Fact]
    public async Task ScanProcesses_VanishedWgAdapter_DropsStaleAutoExclude_AndNeverMutatesPersistedList()
    {
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";

        settings.Tun.RouteExcludeAddress = new List<string> { "192.168.77.0/24" };
        settings.Tun.AutoDetectedExcludeAddress = new List<string> { "203.0.113.0/24" };
        var persistedSnapshot = new List<string>(settings.Tun.RouteExcludeAddress);

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ConfigJson);

        Assert.DoesNotContain("203.0.113.0/24", settings.Tun.AutoDetectedExcludeAddress);

        Assert.Equal(persistedSnapshot, settings.Tun.RouteExcludeAddress);

        Assert.Contains("192.168.77.0/24", result.ConfigJson!);
        Assert.DoesNotContain("203.0.113.0/24", result.ConfigJson!);
    }

    internal sealed class CapturingFirewall : IFirewallManager
    {
        public int CreateBlockRulesCount { get; private set; }
        public bool? LastIsFullTunnel { get; private set; }
        public bool AbortAfterCapture { get; set; }

        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
        {
            CreateBlockRulesCount++;
            LastIsFullTunnel = isFullTunnel;
            if (AbortAfterCapture)
                throw new FirewallCapturedSentinelException();
        }

        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    internal sealed class FirewallCapturedSentinelException : Exception
    {
        public FirewallCapturedSentinelException()
            : base("sentinel: firewall CreateBlockRules captured (test abort)") { }
    }

    internal sealed class TestStartupHost : StartupHostInternal
    {
        private sealed class StubProcessScanner : IProcessScanner
        {
            public ScanResult ScanForProfile(Profile profile) =>
                new() { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
        }

        public ILogger? Logger { get; } = null;
        public IProcessScanner Scanner { get; } = new StubProcessScanner();
        public CapturingFirewall CapturedFirewall { get; } = new();
        public Func<IFirewallManager> FirewallFactory { get; }
        public Func<IProcessMonitor> MonitorFactory { get; } =
            () => throw new InvalidOperationException(
                "TestStartupHost: phase 8 should not run in HotReload tests");

        public TestStartupHost()
        {
            FirewallFactory = () => CapturedFirewall;
        }

        public SingBoxManager? SingBox => SetSingBox;
        public IFirewallManager? Firewall => SetFirewall;

        public List<string> Statuses { get; } = new();
        public List<string> Warnings { get; } = new();
        public string? ActiveServerAddress { get; private set; }
        public string? ActiveConfigMode { get; private set; }
        public string? ActiveRoutingMode { get; private set; }
        public string? TunFingerprint { get; private set; }
        public Profile? ActiveProfile { get; private set; }
        public ScanResult? ScanResultRecorded { get; private set; }
        public SingBoxManager? SetSingBox { get; private set; }
        public IFirewallManager? SetFirewall { get; private set; }
        public IProcessMonitor? SetEtw { get; private set; }
        public HealthMonitor? SetHealth { get; private set; }
        public bool SanityCheckEnsured { get; private set; }
        public bool AutoFailoverInvoked { get; private set; }
        public bool PostStartProbeScheduled { get; private set; }

        public void OnStatus(string message) => Statuses.Add(message);
        public void OnWarning(string message) => Warnings.Add(message);
        public void OnSingBoxStarted(int pid) { }
        public List<int> ConnectedPids { get; } = new();
        public void OnConnected(int pid) => ConnectedPids.Add(pid);
        public void OnRestartAttempted(int attempt, int max) { }
        public void OnFailoverRequested(string reason) { }
        public void OnAutoFailoverTriggered(string message) =>
            AutoFailoverInvoked = true;
        public void OnProcessDetected(string name, int pid) { }
        public void SetActiveServerAddress(string address) =>
            ActiveServerAddress = address;
        public void SetActiveModes(string configMode, string routingMode, string tunFingerprint)
        {
            ActiveConfigMode = configMode;
            ActiveRoutingMode = routingMode;
            TunFingerprint = tunFingerprint;
        }
        public void SetActiveProfile(Profile profile) => ActiveProfile = profile;
        public void SetScanResult(ScanResult result) => ScanResultRecorded = result;
        public void SetSingBoxManager(SingBoxManager manager) => SetSingBox = manager;
        public VlessServerEntry? DnsTunnelTransportStarted { get; private set; }
        public void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings)
            => DnsTunnelTransportStarted = activeServer;
        public void SetFirewallManager(IFirewallManager firewall) => SetFirewall = firewall;
        public void SetProcessMonitor(IProcessMonitor etw) => SetEtw = etw;
        public void SetHealthMonitor(HealthMonitor monitor) => SetHealth = monitor;

        public void EnsureSanityCheckScaffolding(
            AppSettings settings, out ConfigSanityCheck sanityCheck)
        {
            SanityCheckEnsured = true;
            sanityCheck = new ConfigSanityCheck();
        }

        public AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck) =>
            new(new AppSettings(), sanityCheck);

        public AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck) =>
            new(new AppSettings(), sanityCheck);

        public void SchedulePostStartProbe(
            AppSettings settings,
            ConfigSanityCheck sanityCheck,
            CancellationToken ct)
        {
            PostStartProbeScheduled = true;
        }
    }
}
