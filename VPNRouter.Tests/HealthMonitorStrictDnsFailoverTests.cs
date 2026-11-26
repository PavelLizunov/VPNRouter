using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public class HealthMonitorStrictDnsFailoverTests
{
    private sealed class StubScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string> { "Discord.exe" } };
    }

    private sealed class StubFirewall : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() => EnableCount++;
        public void DisableBlockRules() => DisableCount++;
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static AppSettings Settings(bool strictDns, string routingMode = "split", string appsMode = "include")
    {
        var s = new AppSettings
        {
            App = new AppConfig
            {
                StrictDns = strictDns,
                BypassRussianTraffic = false,
                ConfigMode = "generated",
                RoutingMode = routingMode,
                RoutingAppsMode = appsMode,
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig(),
        };
        s.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Name = "main", Server = "1.2.3.4", Port = 443, Uuid = "u",
                    Security = "reality", Reality = new VlessRealityConfig { PublicKey = "k", ShortId = "ab" } }
        };
        return s;
    }

    private static HealthMonitor BuildHm(FakeSingBoxApi api, StubFirewall? firewall = null)
    {
        var sb = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:65535" },
            http: new FakeHttpClient());
        var mon = new MonitoringSettings { HealthCheckInterval = 3600, MaxRestartAttempts = 5, RestartOnFailure = false };
        return new HealthMonitor(sb, new StubScanner(), firewall ?? new StubFirewall(), mon, api: api);
    }

    private static void SetField(object obj, string name, object value)
    {
        var f = obj.GetType().GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        f.SetValue(obj, value);
    }

    private static bool GetFailedOver(HealthMonitor hm)
    {
        var f = hm.GetType().GetField("_strictDnsFailedOver",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (bool)f.GetValue(hm)!;
    }

    private static T? GetNullableField<T>(HealthMonitor hm, string name) where T : struct
    {
        var f = hm.GetType().GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (T?)f.GetValue(hm);
    }

    private static string? GetNullableFieldReference(HealthMonitor hm, string name)
    {
        var f = hm.GetType().GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (string?)f.GetValue(hm);
    }

    private static void Reconcile(HealthMonitor hm)
    {
        var m = hm.GetType().GetMethod("ReconcileStrictDnsFailover",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(hm, null);
    }

    private static HealthMonitor Started(FakeSingBoxApi api, AppSettings settings)
    {
        var hm = BuildHm(api);
        hm.ReloadHookForTests = _ => api.ReloadResultOverride ?? api.TunnelHealthy;
        hm.Start(new Profile { Name = "test" }, settings);
        SetField(hm, "_lastScan", new ScanResult { ProcessNames = new List<string> { "Discord.exe" } });
        return hm;
    }

    [Fact]
    public void ProxyUnreachable_AfterThreshold_FailsOpen()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null };
        var hm = Started(api, Settings(strictDns: true));
        try
        {
            Reconcile(hm);
            Assert.False(GetFailedOver(hm));

            Reconcile(hm);
            Assert.True(GetFailedOver(hm));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DnsFailoverWithUnavailableAdFilterReloadsOnlyWhenFilteringIsDisabled(bool blockAds)
    {
        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            Directory.CreateDirectory(VPNRouter.Core.AppPaths.CacheDir);
            // A file at the cache directory path prevents any fetch.
            File.WriteAllText(Path.Combine(VPNRouter.Core.AppPaths.CacheDir, RuleSetCacheManager.CacheSubdir), "fixture");
            var settings = Settings(strictDns: true);
            settings.App.BlockAds = blockAds;
            using var hm = Started(new FakeSingBoxApi { ProxyDelayMs = null }, settings);
            var reloads = 0;
            hm.ReloadHookForTests = _ => { reloads++; return true; };

            Reconcile(hm);
            Reconcile(hm);

            Assert.Equal(blockAds ? 0 : 1, reloads);
            Assert.Equal(!blockAds, GetFailedOver(hm));
        });
    }

    [Fact]
    public void FailOpen_ReloadFails_LeavesFlagUnset_NoEvent()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null, ReloadResultOverride = false };
        var hm = Started(api, Settings(strictDns: true));
        var fired = new List<bool>();
        hm.StrictDnsFailoverChanged += (_, v) => fired.Add(v);
        try
        {
            Reconcile(hm);
            Reconcile(hm);
            Assert.False(GetFailedOver(hm));
            Assert.Empty(fired);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void FailOpen_StartedUnconfirmed_KeepsKillSwitchArmedAndRecordsPendingTarget()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null };
        var firewall = new StubFirewall();
        var hm = BuildHm(api, firewall);
        hm.ReloadOutcomeHookForTests = _ => ConfigApplyState.StartedUnconfirmed;
        hm.Start(new Profile { Name = "test" }, Settings(strictDns: true));
        SetField(hm, "_lastScan", new ScanResult { ProcessNames = new List<string> { "Discord.exe" } });
        try
        {
            Reconcile(hm);
            Reconcile(hm);

            Assert.False(GetFailedOver(hm));
            Assert.True(GetNullableField<bool>(hm, "_pendingStrictDnsFailOpen") == true);
            Assert.NotNull(GetNullableFieldReference(hm, "_pendingConfigConfirmation"));
            Assert.Equal(1, firewall.EnableCount);
            Assert.Equal(0, firewall.DisableCount);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void PendingStrictDnsTarget_CommitsOnlyWhenConfigConfirmationArrives()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null };
        var hm = BuildHm(api);
        hm.Start(new Profile { Name = "test" }, Settings(strictDns: true));
        var events = new List<bool>();
        hm.StrictDnsFailoverChanged += (_, value) => events.Add(value);
        try
        {
            SetField(hm, "_pendingConfigConfirmation", "{}");
            SetField(hm, "_pendingStrictDnsFailOpen", (bool?)true);

            typeof(HealthMonitor)
                .GetMethod("CommitPendingConfigConfirmation",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(hm, null);

            Assert.True(GetFailedOver(hm));
            Assert.Null(GetNullableFieldReference(hm, "_pendingConfigConfirmation"));
            Assert.Null(GetNullableField<bool>(hm, "_pendingStrictDnsFailOpen"));
            Assert.Equal(new[] { true }, events);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void FailOpen_ReloadSucceedsAfterEarlierFail_Applies()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null, ReloadResultOverride = false };
        var hm = Started(api, Settings(strictDns: true));
        var fired = new List<bool>();
        hm.StrictDnsFailoverChanged += (_, v) => fired.Add(v);
        try
        {
            Reconcile(hm); Reconcile(hm);
            Assert.False(GetFailedOver(hm));
            Assert.Empty(fired);

            api.ReloadResultOverride = true;
            Reconcile(hm);
            Assert.True(GetFailedOver(hm));
            Assert.Equal(new[] { true }, fired);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void ProxyReachable_NeverFailsOver()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = 42 };
        var hm = Started(api, Settings(strictDns: true));
        try
        {
            Reconcile(hm);
            Reconcile(hm);
            Reconcile(hm);
            Assert.False(GetFailedOver(hm));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void Recovers_ReArmsAfterThreshold()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null };
        var hm = Started(api, Settings(strictDns: true));
        try
        {
            Reconcile(hm); Reconcile(hm);
            Assert.True(GetFailedOver(hm));

            api.ProxyDelayMs = 99;
            Reconcile(hm);
            Assert.True(GetFailedOver(hm));
            Reconcile(hm);
            Assert.False(GetFailedOver(hm));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void FullTunnel_UnreachableProxy_NeverFailsOver()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null };
        var hm = Started(api, Settings(strictDns: true, routingMode: "full"));
        try
        {
            Reconcile(hm); Reconcile(hm); Reconcile(hm);
            Assert.False(GetFailedOver(hm));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void StrictDnsOff_NeverProbes()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null };
        var hm = Started(api, Settings(strictDns: false));
        try
        {
            Reconcile(hm); Reconcile(hm);
            Assert.False(GetFailedOver(hm));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }
}
