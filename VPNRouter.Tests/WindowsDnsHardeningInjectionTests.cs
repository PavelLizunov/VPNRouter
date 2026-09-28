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

    [Fact]
    public void IWindowsDnsHardening_InterfaceShape_ThreeMethodsPresent()
    {
        var type = typeof(IWindowsDnsHardening);
        Assert.True(type.IsInterface);

        var apply = type.GetMethod("Apply");
        Assert.NotNull(apply);
        var applyParams = apply!.GetParameters();
        Assert.Equal(2, applyParams.Length);
        Assert.Equal(typeof(AppSettings), applyParams[0].ParameterType);
        Assert.Equal(typeof(Serilog.ILogger), applyParams[1].ParameterType);

        var restore = type.GetMethod("Restore");
        Assert.NotNull(restore);
        var restoreParams = restore!.GetParameters();
        Assert.Single(restoreParams);
        Assert.Equal(typeof(Serilog.ILogger), restoreParams[0].ParameterType);

        var enableLockdown = type.GetMethod("EnableLockdownIfConfigured");
        Assert.NotNull(enableLockdown);
        var enableParams = enableLockdown!.GetParameters();
        Assert.Equal(2, enableParams.Length);
        Assert.Equal(typeof(AppSettings), enableParams[0].ParameterType);
        Assert.Equal(typeof(Serilog.ILogger), enableParams[1].ParameterType);

        Assert.NotNull(WindowsDnsHardeningImpl.Default);
        Assert.IsAssignableFrom<IWindowsDnsHardening>(WindowsDnsHardeningImpl.Default);
    }

    [Fact]
    public void VpnEngine_NullCtorArg_UsesDefaultImpl()
    {
#pragma warning disable CS0618
        using var engine = new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null);
#pragma warning restore CS0618

        engine.Stop();

        var field = typeof(VpnEngine).GetField(
            "_dnsHardening",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(engine);
        Assert.NotNull(value);
        Assert.IsAssignableFrom<IWindowsDnsHardening>(value);
    }
}
