#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VpnEngineSplitTunnelLifecycleTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new()
            {
                ProcessNames = new List<string> { "chrome.exe" },
                ScannedAt = DateTime.Now,
            };
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
        public void RaiseDummy()
        {
            ProcessStarted?.Invoke(this, new());
            ProcessStopped?.Invoke(this, new());
        }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(
        IWindowsDnsHardening dnsHardening,
        out StubFirewallManager firewall,
        out StubProcessMonitor monitor)
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
            dnsHardening: dnsHardening);
    }
#pragma warning restore CS0618

    private static AppSettings BuildSplitTunnelSettings(
        string singBoxExePath, string activeProfile = "Browsers") =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
                DnsLeakLockdown = false,
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
            ActiveProfile = activeProfile,
        };

    private static string CreateStubExe()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-split-stub-{Guid.NewGuid():N}.exe");
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
        BuildSingBoxSpawnFake(int pid = 99201)
    {
        var handle = new FakeProcessHandle(pid);
        var runner = new FakeProcessRunner();
        runner.OnStart(_ => true, _ => handle);
        return (runner, handle);
    }

    private static async Task<(VpnEngine engine,
                                NullWindowsDnsHardening dnsHardening,
                                FakeProcessHandle handle,
                                StubFirewallManager firewall,
                                StubProcessMonitor monitor,
                                IDisposable cleanup)>
        StartSplitTunnelHappyPathAsync(string activeProfile = "Browsers")
    {
        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, handle) = BuildSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildSplitTunnelSettings(stubExe, activeProfile);

        var cleanup = new SplitTunnelCleanup(
            engine,
            stubExe,
            prevSingBoxRunner,
            prevTunDiagRunner);

        try
        {
            await engine.StartAsync(settings, default, skipVpnConflictCheck: true);
        }
        catch
        {
            cleanup.Dispose();
            throw;
        }

        return (engine, dnsHardening, handle, firewall, monitor, cleanup);
    }

    private sealed class SplitTunnelCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private bool _disposed;

        public SplitTunnelCleanup(
            VpnEngine engine,
            string stubExe,
            IProcessRunner prevSingBoxRunner,
            IProcessRunner prevTunDiagRunner)
        {
            _engine = engine;
            _stubExe = stubExe;
            _prevSingBoxRunner = prevSingBoxRunner;
            _prevTunDiagRunner = prevTunDiagRunner;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _engine.Stop(); } catch { }
            try { _engine.Dispose(); } catch { }
            SingBoxManager.Runner = _prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = _prevTunDiagRunner;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            try { File.Delete(_stubExe); } catch { }
        }
    }

    [Fact]
    public async Task Start_SplitTunnel_Browsers_FiresLifecycleEvents()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart drives SingBoxManager's Windows spawn path; Linux uses pkexec + getcap shell-outs not behind IProcessRunner.");

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartSplitTunnelHappyPathAsync(activeProfile: "Browsers");
        using var _ = cleanup;

        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);
        Assert.Equal("Apply", dnsHardening.Calls[0].Op);
        Assert.NotNull(dnsHardening.Calls[0].Settings);

        Assert.True(engine.IsRunning);
        Assert.Equal("10.0.0.1", engine.ActiveServerAddress);

        Assert.Equal("Browsers", engine.ActiveProfileName);
        Assert.Equal("split", engine.ActiveRoutingMode);

        Assert.NotEmpty(engine.MonitoredProcesses);
        Assert.Contains("chrome.exe", engine.MonitoredProcesses,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(0, firewall.CreateBlockRulesCount);

        Assert.Equal(1, monitor.StartCount);

        Assert.False(handle.HasExited);
    }

    [Fact]
    public async Task Stop_SplitTunnel_FiresRestoreThroughDnsHardening()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartSplitTunnelHappyPathAsync(activeProfile: "Browsers");
        try
        {
            Assert.Equal(1, dnsHardening.ApplyCount);
            Assert.Equal(0, dnsHardening.RestoreCount);
            Assert.True(engine.IsRunning);
            Assert.Equal("Browsers", engine.ActiveProfileName);

            engine.Stop();

            Assert.Equal(1, dnsHardening.RestoreCount);
            Assert.Equal("Restore", dnsHardening.Calls.Last().Op);
            Assert.Null(dnsHardening.Calls.Last().Settings);

            Assert.False(engine.IsRunning);
            Assert.True(handle.HasExited);

            Assert.Equal(1, monitor.DisposeCount);

            Assert.Equal(0, firewall.DisableBlockRulesCount);
            Assert.Equal(0, firewall.DeleteAllRulesCount);
        }
        finally
        {
            cleanup.Dispose();
        }
    }
}
