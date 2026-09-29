using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class VpnEngineApplyStructuralChangeTests
{
    [Fact]
    public void DetectStructuralChanges_IdenticalState_NoChanges()
    {
        var changes = VpnEngine.DetectStructuralChanges(
            "generated", "GENERATED", "split", "SPLIT", "tun", "tun",
            "include:chrome.exe", "include:chrome.exe");

        Assert.False(changes.ConfigModeChanged);
        Assert.False(changes.RoutingModeChanged);
        Assert.False(changes.TunChanged);
        Assert.False(changes.AppRoutingChanged);
    }

    [Theory]
    [InlineData("generated", "custom", "split", "split", "tun", "tun", "apps", "apps", true, false, false, false)]
    [InlineData("generated", "generated", "split", "full", "tun", "tun", "apps", "apps", false, true, false, false)]
    [InlineData("generated", "generated", "split", "split", "tun-a", "tun-b", "apps", "apps", false, false, true, false)]
    [InlineData("generated", "generated", "split", "split", "tun", "tun", "include:a", "include:b", false, false, false, true)]
    [InlineData("generated", "generated", "split", "split", "tun", "tun", "include:Chrome.exe", "include:chrome.exe", false, false, false, true)]
    public void DetectStructuralChanges_OneAxisChanges_ReportsThatAxis(
        string activeConfigMode,
        string candidateConfigMode,
        string activeRoutingMode,
        string candidateRoutingMode,
        string activeTunFingerprint,
        string candidateTunFingerprint,
        string activeAppFingerprint,
        string candidateAppFingerprint,
        bool expectedConfigModeChanged,
        bool expectedRoutingModeChanged,
        bool expectedTunChanged,
        bool expectedAppRoutingChanged)
    {
        var changes = VpnEngine.DetectStructuralChanges(
            activeConfigMode,
            candidateConfigMode,
            activeRoutingMode,
            candidateRoutingMode,
            activeTunFingerprint,
            candidateTunFingerprint,
            activeAppFingerprint,
            candidateAppFingerprint);

        Assert.Equal(expectedConfigModeChanged, changes.ConfigModeChanged);
        Assert.Equal(expectedRoutingModeChanged, changes.RoutingModeChanged);
        Assert.Equal(expectedTunChanged, changes.TunChanged);
        Assert.Equal(expectedAppRoutingChanged, changes.AppRoutingChanged);
    }

    [Fact]
    public async Task ApplyAsync_SingBoxReloadFails_RestoresBaselineAndReturnsFalseWithoutAppliedStatus()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-apply-reload-fail-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);
        Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);

        var profilesDir = VPNRouter.Core.AppPaths.ProfilesDir;
        Directory.CreateDirectory(profilesDir);
        var profileFile = Path.Combine(profilesDir, "test-profiles.json");
        File.WriteAllText(profileFile, """
        {
          "profiles": [
            {
              "name": "TestProfile",
              "description": "Deterministic test profile",
              "dns_mode": "vpn_only",
              "block_on_vpn_fail": false,
              "processes": []
            }
          ]
        }
        """);

        var scanner = new StubProcessScanner();
        var firewall = new StubFirewallManager();
        var monitor = new StubProcessMonitor();
        var fakeDriver = new FakeSplitTunnelDriver();
        var dnsHardening = new NullWindowsDnsHardening();
#pragma warning disable CS0618
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: fakeDriver);
#pragma warning restore CS0618

        var statuses = new List<string>();
        engine.StatusChanged += statuses.Add;

        const string baselineConfigMode = "generated";
        const string baselineRoutingMode = "split";
        const string baselineTunFingerprint = "tun-baseline-1234";
        const string baselineAppRoutingFingerprint = "app-routing-baseline-5678";

        SetProperty(engine, "ActiveConfigMode", baselineConfigMode);
        SetProperty(engine, "ActiveRoutingMode", baselineRoutingMode);
        SetProperty(engine, "TunFingerprint", baselineTunFingerprint);
        SetProperty(engine, "ActiveAppRoutingFingerprint", baselineAppRoutingFingerprint);

        using var sessionCts = new CancellationTokenSource();
        SetField(engine, "_sessionCts", sessionCts);

        var fakeHttp = new FakeHttpClient().Setup("/configs", "{}");
        var runner = new FakeProcessRunner();
        var singBox = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
            null, fakeHttp, runner);

        var initialHandle = new FakeProcessHandle(pid: 12345);
        SetField(singBox, "_handle", initialHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(singBox, SingBoxState.Running);
        SetField(singBox, "_ownsTunLock", false);

        SetField(engine, "_singBox", singBox);

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "full",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
                DnsLeakLockdown = false,
            },
            ProfileSources = new List<ProfileSource>
            {
                new() { Type = "local", Path = profileFile }
            },
            ActiveProfile = "TestProfile",
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
            SingBox = new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
        };

        try
        {
            var result = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

            Assert.False(result, "ApplyAsync must return false when sing-box reload/restart returns false.");

            Assert.Contains("Apply failed: sing-box reload or restart was not confirmed", statuses);
            Assert.DoesNotContain(statuses, s => s.StartsWith("Applied"));

            Assert.Equal(baselineConfigMode, engine.ActiveConfigMode);
            Assert.Equal(baselineRoutingMode, engine.ActiveRoutingMode);
            Assert.Equal(baselineTunFingerprint, engine.TunFingerprint);
            Assert.Equal(baselineAppRoutingFingerprint, engine.ActiveAppRoutingFingerprint);

            if (OperatingSystem.IsWindows())
            {
                Assert.Empty(fakeHttp.SentRequests);
            }
            else
            {
                Assert.DoesNotContain(fakeHttp.SentRequests, r => r.Method != HttpMethod.Get);
            }
            Assert.Empty(runner.StartCalls);
            Assert.Empty(runner.RunCalls);
            Assert.Equal(0, fakeDriver.EngageCount);
            Assert.Equal(0, fakeDriver.DisengageCount);

            Assert.DoesNotContain(statuses, s => s.StartsWith("Apply failed:") && !s.Contains("sing-box reload or restart was not confirmed"));
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            SetField(engine, "_sessionCts", null);
            initialHandle.Dispose();

            Assert.False(engine.IsRunning);

            singBox.Dispose();
            engine.Dispose();

            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var f = obj.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType()}");
        f.SetValue(obj, value);
    }

    private static object? GetField(object obj, string fieldName)
    {
        var f = obj.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType()}");
        return f.GetValue(obj);
    }

    private static void SetProperty(object obj, string propertyName, object? value)
    {
        var p = obj.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on {obj.GetType()}");
        p.SetValue(obj, value);
    }

    private static string? GetAppPathsDataDir()
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)
             ?? typeof(VPNRouter.Core.AppPaths).GetField("_dataDirOverride", BindingFlags.Static | BindingFlags.NonPublic);
        return (string?)f?.GetValue(null);
    }

    private static void RestoreAppPathsDataDir(string? priorDataDir)
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)
             ?? typeof(VPNRouter.Core.AppPaths).GetField("_dataDirOverride", BindingFlags.Static | BindingFlags.NonPublic);
        f?.SetValue(null, priorDataDir);
    }

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
        public event EventHandler<ProcessEventArgs>? ProcessStarted { add { } remove { } }
        public event EventHandler<ProcessEventArgs>? ProcessStopped { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static string? LoadVpnEngineSource()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "VPNRouter.Core",
                "Services",
                "VpnEngine.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        return null;
    }

    private static string? LoadStartupPipelineSource()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "VPNRouter.Core",
                "Services",
                "StartupPipeline.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        return null;
    }

    private static string StripComments(string source)
    {
        var noBlock = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var noLine = System.Text.RegularExpressions.Regex.Replace(noBlock, @"//.*", "");
        return noLine;
    }

    private sealed class CapturingCommittedFirewallManager : IFirewallManager, ICommittedFirewallConfig
    {
        public List<(string ConfigJson, bool EnabledForFullTunnel)> UpdateCalls { get; } = new();

        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }

        void ICommittedFirewallConfig.UpdateCommittedConfig(string configJson, bool enabledForFullTunnel)
        {
            UpdateCalls.Add((configJson, enabledForFullTunnel));
        }
    }

    [Fact]
    public async Task ApplyAsync_ReloadFailsOnExactBranch_ZeroFirewallCapabilityCalls()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-apply-exactfail-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);
        Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);

        var profilesDir = VPNRouter.Core.AppPaths.ProfilesDir;
        Directory.CreateDirectory(profilesDir);
        var profileFile = Path.Combine(profilesDir, "test-profiles.json");
        File.WriteAllText(profileFile, """
        {
          "profiles": [
            {
              "name": "TestProfile",
              "description": "Deterministic test profile",
              "dns_mode": "vpn_only",
              "block_on_vpn_fail": true,
              "processes": []
            }
          ]
        }
        """);

        var scanner = new StubProcessScanner();
        var firewall = new CapturingCommittedFirewallManager();
        var monitor = new StubProcessMonitor();
        var fakeDriver = new FakeSplitTunnelDriver();
        var dnsHardening = new NullWindowsDnsHardening();
#pragma warning disable CS0618
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: fakeDriver);
#pragma warning restore CS0618

        SetField(engine, "_firewall", firewall);

        const string baselineConfigMode = "generated";
        const string baselineRoutingMode = "split";
        const string baselineTunFingerprint = "tun-baseline-1234";
        const string baselineAppRoutingFingerprint = "app-routing-baseline-5678";

        SetProperty(engine, "ActiveConfigMode", baselineConfigMode);
        SetProperty(engine, "ActiveRoutingMode", baselineRoutingMode);
        SetProperty(engine, "TunFingerprint", baselineTunFingerprint);
        SetProperty(engine, "ActiveAppRoutingFingerprint", baselineAppRoutingFingerprint);

        using var sessionCts = new CancellationTokenSource();
        SetField(engine, "_sessionCts", sessionCts);

        var fakeHttp = new FakeHttpClient().Setup("/configs", "{}");
        var runner = new FakeProcessRunner();
        var singBox = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
            null, fakeHttp, runner);

        var initialHandle = new FakeProcessHandle(pid: 12345);
        SetField(singBox, "_handle", initialHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(singBox, SingBoxState.Running);
        SetField(singBox, "_ownsTunLock", false);

        SetField(engine, "_singBox", singBox);

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "full",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
                DnsLeakLockdown = false,
            },
            ProfileSources = new List<ProfileSource>
            {
                new() { Type = "local", Path = profileFile }
            },
            ActiveProfile = "TestProfile",
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
            SingBox = new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
        };

        var baselineSettings = new AppSettings
        {
            App = new AppConfig { ConfigMode = baselineConfigMode, RoutingMode = baselineRoutingMode }
        };
        var baselineFailover = new AutoFailoverEngine(baselineSettings, new ConfigSanityCheck());
        SetField(engine, "_failoverSettingsContext", baselineSettings);
        SetField(engine, "_failover", baselineFailover);

        try
        {
            var result = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
            Assert.False(result);
            Assert.Empty(firewall.UpdateCalls);

            Assert.Same(baselineSettings, GetField(engine, "_failoverSettingsContext"));
            Assert.Same(baselineFailover, GetField(engine, "_failover"));
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            SetField(engine, "_sessionCts", null);
            initialHandle.Dispose();

            Assert.False(engine.IsRunning);

            singBox.Dispose();
            engine.Dispose();

            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_SingBoxAlreadyRunning_NoOpStartDoesNotResetFailoverContext()
    {
        var scanner = new StubProcessScanner();
        var firewall = new StubFirewallManager();
        var monitor = new StubProcessMonitor();
        var fakeDriver = new FakeSplitTunnelDriver();
        var dnsHardening = new NullWindowsDnsHardening();
#pragma warning disable CS0618
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: fakeDriver);
#pragma warning restore CS0618

        var runner = new FakeProcessRunner();
        var fakeHttp = new FakeHttpClient().Setup("/configs", "{}");
        var singBox = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
            null, fakeHttp, runner);

        var initialHandle = new FakeProcessHandle(pid: 34567);
        SetField(singBox, "_handle", initialHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(singBox, SingBoxState.Running);

        SetField(engine, "_singBox", singBox);

        var settingsA = new AppSettings { App = new AppConfig { ConfigMode = "generated" } };
        var failoverA = new AutoFailoverEngine(settingsA, new ConfigSanityCheck());
        SetField(engine, "_failoverSettingsContext", settingsA);
        SetField(engine, "_failover", failoverA);

        var settingsB = new AppSettings { App = new AppConfig { ConfigMode = "subscribe" } };

        try
        {
            await engine.StartAsync(settingsB, TestContext.Current.CancellationToken);

            Assert.Same(settingsA, GetField(engine, "_failoverSettingsContext"));
            Assert.Same(failoverA, GetField(engine, "_failover"));
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            initialHandle.Dispose();
            singBox.Dispose();
            engine.Dispose();
        }
    }
}
