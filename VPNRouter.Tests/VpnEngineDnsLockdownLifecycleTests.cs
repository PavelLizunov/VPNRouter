#nullable enable

using System.Net.Http;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VpnEngineDnsLockdownLifecycleTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public int CreateBlockRulesCount;
        public int EnableBlockRulesCount;
        public int DisableBlockRulesCount;
        public int DeleteAllRulesCount;
        public int DisposeCount;

        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) => CreateBlockRulesCount++;
        public void EnableBlockRules() => EnableBlockRulesCount++;
        public void DisableBlockRules() => DisableBlockRulesCount++;
        public void DeleteAllRules() => DeleteAllRulesCount++;
        public void Dispose() => DisposeCount++;
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public int StartCount;
        public int StopCount;
        public int DisposeCount;

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public async Task SplitDriver_StartHook_EngagesInWindowsExcludeMode()
    {
        var fake = new Fakes.FakeSplitTunnelDriver();
        var engine = BuildEngine(new Fakes.NullWindowsDnsHardening(), out _, out _, splitDriver: fake);
        var s = BuildHappyPathSettings("x");
        s.App.RoutingMode = "split";
        s.App.RoutingAppsMode = "exclude";
        s.App.RoutingAppsExclude = new List<string> { "curl.exe" };

        await engine.TryEngageSplitDriverAsync(s, default);

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, fake.EngageCount);
    }

    [Fact]
    public async Task SplitDriver_StartHook_SkipsIncludeMode()
    {
        var fake = new Fakes.FakeSplitTunnelDriver();
        var engine = BuildEngine(new Fakes.NullWindowsDnsHardening(), out _, out _, splitDriver: fake);
        var s = BuildHappyPathSettings("x");
        s.App.RoutingMode = "split";
        s.App.RoutingAppsMode = "include";
        s.App.RoutingAppsExclude = new List<string> { "curl.exe" };

        await engine.TryEngageSplitDriverAsync(s, default);

        Assert.Equal(0, fake.EngageCount);
    }

    [Fact]
    public void SplitDriver_Dispose_ReleasesDriver()
    {
        var fake = new Fakes.FakeSplitTunnelDriver();
        var engine = BuildEngine(new Fakes.NullWindowsDnsHardening(), out _, out _, splitDriver: fake);

        engine.Dispose();

        Assert.Equal(1, fake.DisposeCount);
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(
        IWindowsDnsHardening dnsHardening,
        out StubFirewallManager firewall,
        out StubProcessMonitor monitor,
        ISplitTunnelDriver? splitDriver = null)
    {
        var fw = new StubFirewallManager();
        var mon = new StubProcessMonitor();
        firewall = fw;
        monitor = mon;
        return new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => fw,
            monitorFactory: () => mon,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: splitDriver);
    }
#pragma warning restore CS0618

    private static AppSettings BuildHappyPathSettings(
        string singBoxExePath, bool dnsLeakLockdown = false) =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "full",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
                DnsLeakLockdown = dnsLeakLockdown,
            },
            Vless = new VlessConfig
            {
                ActiveServer = "main",
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "main",
                        Server = "10.0.0.1",
                        Port = 443,
                        Uuid = "11111111-2222-3333-4444-555555555555",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true,
                            ServerName = "www.cloudflare.com",
                            Fingerprint = "chrome",
                            PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                            ShortId = "d86e92a0c6dd2271",
                        },
                    },
                },
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings
            {
                ExecutablePath = singBoxExePath,
                ClashApi = "127.0.0.1:65535",
            },
            Monitoring = new MonitoringSettings
            {
                HealthCheckInterval = 3600,
                MaxRestartAttempts = 5,
                RestartOnFailure = true,
            },
            ActiveProfile = "",
        };

    private static string CreateStubExe()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-dnslockdown-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "stub");
        return path;
    }

    private static FakeProcessRunner BuildTunCleanupFake() =>
        new FakeProcessRunner()
            .OnRun(
                r => r.ExecutablePath == "netsh"
                  && r.Arguments.Count >= 3
                  && r.Arguments[0] == "interface"
                  && r.Arguments[1] == "show",
                new ProcessResult(
                    ExitCode: 0,
                    Stdout: "Admin State    State          Type             Interface Name\r\n"
                          + "----------------------------------------------------------\r\n"
                          + "Enabled        Connected      Dedicated        Ethernet\r\n",
                    Stderr: "",
                    Duration: TimeSpan.FromMilliseconds(1),
                    TimedOut: false))
            .OnRun(
                r => r.ExecutablePath == "netsh"
                  && r.Arguments.Contains("admin=disabled"),
                new ProcessResult(
                    ExitCode: 5,
                    Stdout: "",
                    Stderr: "Adapter not found (synthetic)",
                    Duration: TimeSpan.FromMilliseconds(1),
                    TimedOut: false));

    private static (FakeProcessRunner runner, FakeProcessHandle handle)
        BuildSingBoxSpawnFake(int pid = 99401)
    {
        var handle = new FakeProcessHandle(pid);
        var runner = new FakeProcessRunner();
        runner.OnStart(_ => true, _ => handle);
        return (runner, handle);
    }

    private static FakeHttpClient BuildWarmupSuccessHttpClient() =>
        new FakeHttpClient().Setup(
            "gstatic.com/generate_204",
            new HttpResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>(),
                Body: Array.Empty<byte>(),
                Duration: TimeSpan.FromMilliseconds(1)));

    private static FakeHttpClient BuildWarmupFailureHttpClient() =>
        new FakeHttpClient().ThrowOn(
            "gstatic.com/generate_204",
            new HttpRequestException("simulated TUN-not-routing"));

    private static async Task<bool> WaitForAsync(
        Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return true;
            await Task.Delay(50);
        }
        return predicate();
    }

    private sealed class DnsLockdownCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private readonly IHttpClient? _prevWarmupHttp;
        private bool _disposed;

        public DnsLockdownCleanup(
            VpnEngine engine,
            string stubExe,
            IProcessRunner prevSingBoxRunner,
            IProcessRunner prevTunDiagRunner,
            IHttpClient? prevWarmupHttp)
        {
            _engine = engine;
            _stubExe = stubExe;
            _prevSingBoxRunner = prevSingBoxRunner;
            _prevTunDiagRunner = prevTunDiagRunner;
            _prevWarmupHttp = prevWarmupHttp;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _engine.Stop(); } catch { }
            try { _engine.Dispose(); } catch { }
            SingBoxManager.Runner = _prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = _prevTunDiagRunner;
            StartupPipeline.WarmupHttp = _prevWarmupHttp;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            try { File.Delete(_stubExe); } catch { }
        }
    }

    [Fact]
    public async Task Start_DnsLeakLockdownOn_WarmupSuccess_InvokesEnableLockdown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart drives SingBoxManager's Windows spawn path; Linux uses pkexec + getcap shell-outs not behind IProcessRunner.");

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        var prevWarmupHttp = StartupPipeline.WarmupHttp;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, handle) = BuildSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var fakeWarmupHttp = BuildWarmupSuccessHttpClient();
        StartupPipeline.WarmupHttp = fakeWarmupHttp;

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe, dnsLeakLockdown: true);

        using var cleanup = new DnsLockdownCleanup(
            engine, stubExe, prevSingBoxRunner, prevTunDiagRunner, prevWarmupHttp);

        await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);

        Assert.Equal(1, dnsHardening.ApplyCount);
        var applyCall = dnsHardening.Calls.First(c => c.Op == "Apply");
        Assert.NotNull(applyCall.Settings);
        Assert.True(applyCall.Settings!.App.DnsLeakLockdown);

        var fired = await WaitForAsync(
            () => dnsHardening.EnableLockdownCount >= 1,
            TimeSpan.FromSeconds(5));

        Assert.True(fired,
            "Expected the BR-7 success branch to fire EnableLockdownIfConfigured within 5s. " +
            $"Actual EnableLockdownCount={dnsHardening.EnableLockdownCount}, " +
            $"FakeHttpClient.SentRequests={fakeWarmupHttp.SentRequests.Count}");

        Assert.Equal(1, dnsHardening.EnableLockdownCount);
        var lockdownCall = dnsHardening.Calls.First(c => c.Op == "EnableLockdownIfConfigured");
        Assert.NotNull(lockdownCall.Settings);
        Assert.True(lockdownCall.Settings!.App.DnsLeakLockdown);

        Assert.True(fakeWarmupHttp.SentRequests.Count >= 1);
        Assert.Contains(
            fakeWarmupHttp.SentRequests,
            r => r.Uri.ToString().Contains("gstatic.com/generate_204"));
    }

    [Fact]
    public async Task Start_DnsLeakLockdownOn_WarmupFailure_DoesNotInvokeEnableLockdown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        var prevWarmupHttp = StartupPipeline.WarmupHttp;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, handle) = BuildSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var fakeWarmupHttp = BuildWarmupFailureHttpClient();
        StartupPipeline.WarmupHttp = fakeWarmupHttp;

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe, dnsLeakLockdown: true);

        using var cleanup = new DnsLockdownCleanup(
            engine, stubExe, prevSingBoxRunner, prevTunDiagRunner, prevWarmupHttp);

        await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);

        Assert.Equal(1, dnsHardening.ApplyCount);

        await Task.Delay(2000, TestContext.Current.CancellationToken);

        engine.Stop();

        Assert.Equal(0, dnsHardening.EnableLockdownCount);
        Assert.Equal(1, dnsHardening.RestoreCount);

        Assert.True(fakeWarmupHttp.SentRequests.Count >= 1,
            $"Expected at least one warmup probe request via the seam, got {fakeWarmupHttp.SentRequests.Count}");
    }
}
