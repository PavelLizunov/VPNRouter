using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// HealthMonitor → StrictDns runtime failover wiring (v2.42.0)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pins that the HealthMonitor reconcile drives the StrictDns "all DNS via
/// tunnel" failover off the live proxy-reachability probe
/// (<see cref="ISingBoxApi.GetProxyDelayAsync"/>): when the proxy stops being
/// reachable it suppresses StrictDns (the germany "endless loading" fix), and
/// re-arms once it's reachable again — with hysteresis so a single transient
/// probe doesn't flap. Full-tunnel mode is never failed over.
///
/// <para>The decision matrix itself is in <see cref="StrictDnsFailoverPolicyTests"/>
/// and the dns.final lever in <see cref="ConfigGeneratorStrictDnsOverrideTests"/>;
/// this is the wiring half. <see cref="FakeSingBoxApi.ProxyDelayMs"/> drives the
/// probe result; we assert the private <c>_strictDnsFailedOver</c> flag (set
/// before the regen/reload, so the assertion is independent of disk/reload).</para>
/// </summary>
public class HealthMonitorStrictDnsFailoverTests
{
    private sealed class StubScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string> { "Discord.exe" } };
    }

    private sealed class StubFirewall : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
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

    private static HealthMonitor BuildHm(FakeSingBoxApi api)
    {
        var sb = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:65535" });
        var mon = new MonitoringSettings { HealthCheckInterval = 3600, MaxRestartAttempts = 5, RestartOnFailure = false };
        return new HealthMonitor(sb, new StubScanner(), new StubFirewall(), mon, api: api);
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

    private static void Reconcile(HealthMonitor hm)
    {
        var m = hm.GetType().GetMethod("ReconcileStrictDnsFailover",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(hm, null);
    }

    private static HealthMonitor Started(FakeSingBoxApi api, AppSettings settings)
    {
        var hm = BuildHm(api);
        // Deterministic hot-reload via the fake (no real ProgramData disk write,
        // which the dev box's lock would fail) — reflects ReloadResultOverride/TunnelHealthy.
        hm.HotReloadHookForTests = _ => api.ReloadResultOverride ?? api.TunnelHealthy;
        hm.Start(new Profile { Name = "test" }, settings);
        // The reconcile reuses the last scan's process list; give it one.
        SetField(hm, "_lastScan", new ScanResult { ProcessNames = new List<string> { "Discord.exe" } });
        return hm;
    }

    [Fact]
    public void ProxyUnreachable_AfterThreshold_FailsOpen()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = null }; // proxy can't reach test URL
        var hm = Started(api, Settings(strictDns: true));
        try
        {
            // Threshold is 2 consecutive failures — first tick must NOT flip yet.
            Reconcile(hm);
            Assert.False(GetFailedOver(hm));

            // Second consecutive failure → fail open.
            Reconcile(hm);
            Assert.True(GetFailedOver(hm));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void FailOpen_ReloadFails_LeavesFlagUnset_NoEvent()
    {
        // S2 (v2.45.0): proxy unreachable (fail-open decided) but the hot-reload
        // fails — the flag must NOT commit and StrictDnsFailoverChanged must NOT
        // fire, else running sing-box keeps the old StrictDns config while the
        // UI/policy believe failover happened and Decide() stops retrying.
        var api = new FakeSingBoxApi { ProxyDelayMs = null, ReloadResultOverride = false };
        var hm = Started(api, Settings(strictDns: true));
        var fired = new List<bool>();
        hm.StrictDnsFailoverChanged += (_, v) => fired.Add(v);
        try
        {
            Reconcile(hm);
            Reconcile(hm); // past the fail threshold — decides fail-open
            Assert.False(GetFailedOver(hm)); // reload failed → flag NOT committed
            Assert.Empty(fired);             // no event raised
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void FailOpen_ReloadSucceedsAfterEarlierFail_Applies()
    {
        // The retry path: a failed reload leaves the flag unset; a later tick
        // whose reload succeeds commits the flag + fires the event.
        var api = new FakeSingBoxApi { ProxyDelayMs = null, ReloadResultOverride = false };
        var hm = Started(api, Settings(strictDns: true));
        var fired = new List<bool>();
        hm.StrictDnsFailoverChanged += (_, v) => fired.Add(v);
        try
        {
            Reconcile(hm); Reconcile(hm);    // fail-open decided, reload fails
            Assert.False(GetFailedOver(hm));
            Assert.Empty(fired);

            api.ReloadResultOverride = true; // hot-reload now works
            Reconcile(hm);                   // proxy still unreachable → retry commits
            Assert.True(GetFailedOver(hm));
            Assert.Equal(new[] { true }, fired);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void ProxyReachable_NeverFailsOver()
    {
        var api = new FakeSingBoxApi { ProxyDelayMs = 42 }; // healthy
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
            Reconcile(hm); Reconcile(hm);          // fail open
            Assert.True(GetFailedOver(hm));

            api.ProxyDelayMs = 99;                  // proxy back
            Reconcile(hm);
            Assert.True(GetFailedOver(hm));         // 1 healthy probe — not yet (threshold 2)
            Reconcile(hm);
            Assert.False(GetFailedOver(hm));        // 2 healthy probes — re-armed
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void FullTunnel_UnreachableProxy_NeverFailsOver()
    {
        // Full tunnel is not the StrictDns sole-driver case — DNS must keep
        // riding the tunnel even when the proxy probe fails.
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
        // Feature off → reconcile is a no-op (and never flips the flag),
        // regardless of proxy state.
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
