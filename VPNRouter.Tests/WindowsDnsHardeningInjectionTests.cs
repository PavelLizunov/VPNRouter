#nullable enable

using System.Reflection;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class WindowsDnsHardeningInjectionTests
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
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseDummy()
        {
            ProcessStarted?.Invoke(this, new());
            ProcessStopped?.Invoke(this, new());
        }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(IWindowsDnsHardening dnsHardening) =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: dnsHardening);
#pragma warning restore CS0618

    [Fact]
    public void Stop_OnIdleEngine_InvokesRestoreThroughSeam()
    {
        var fake = new NullWindowsDnsHardening();
        using var engine = BuildEngine(fake);

        engine.Stop();

        Assert.Equal(1, fake.RestoreCount);
        Assert.Equal(0, fake.ApplyCount);
        Assert.Equal(0, fake.EnableLockdownCount);
    }

    [Fact]
    public async Task ApplyAsync_OnIdleEngine_DoesNotInvokeHardening()
    {
        var fake = new NullWindowsDnsHardening();
        using var engine = BuildEngine(fake);
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>()
            },
            Vless = new VlessConfig(),
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile"
        };

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Empty(fake.Calls);
    }
}
