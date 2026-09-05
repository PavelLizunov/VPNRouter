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

/// <summary>
/// v2.49 regression coverage for the connected-Apply structural baseline.
/// </summary>
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
    public void ApplyGatedAsync_CapturesLiveBaselineBeforeHotReloadPipeline()
    {
        var source = LoadVpnEngineSource();
        if (source == null) return;

        var captureIndex = source.IndexOf(
            "var oldRoutingMode = ActiveRoutingMode;",
            StringComparison.Ordinal);
        var pipelineIndex = source.IndexOf(
            "new StartupContext(settings, StartupMode.HotReload)",
            StringComparison.Ordinal);

        Assert.True(captureIndex >= 0, "Apply must capture the live routing baseline.");
        Assert.True(pipelineIndex > captureIndex,
            "The live baseline must be captured before StartupPipeline mutates candidate state.");
    }

    [Fact]
    public void ApplyGatedAsync_FailurePathsRestoreLiveBaseline()
    {
        var source = LoadVpnEngineSource();
        if (source == null) return;

        var restoreCount = source.Split("RestoreActiveBaseline();", StringSplitOptions.None).Length - 1;

        Assert.True(restoreCount >= 2,
            "Pipeline failure and exception paths must both restore the live Apply baseline.");
        Assert.Contains(
            "ActiveAppRoutingFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!configCommitted)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyGatedAsync_SingBoxReloadFailed_RestoresBaselineAndReturnsFalse()
    {
        var source = LoadVpnEngineSource();
        Assert.True(source != null, "VpnEngine.cs source could not be loaded.");

        Assert.Contains("!_singBox.ReloadConfigJsonWithResult(configJson, forceRestart)",
            source, StringComparison.Ordinal);
        Assert.Contains("RestoreActiveBaseline();",
            source, StringComparison.Ordinal);
        Assert.Contains("sing-box reload or restart was not confirmed",
            source, StringComparison.Ordinal);
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
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: fakeDriver);

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
                RoutingMode = "full", // Structural change: split -> full triggers forceRestart
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
            var result = await engine.ApplyAsync(settings);

            Assert.False(result, "ApplyAsync must return false when sing-box reload/restart returns false.");

            // 1. Exact message assert (not generic Apply failed)
            Assert.Contains("Apply failed: sing-box reload or restart was not confirmed", statuses);
            Assert.DoesNotContain(statuses, s => s.StartsWith("Applied"));

            // 2. Baseline fingerprint all4 unchanged
            Assert.Equal(baselineConfigMode, engine.ActiveConfigMode);
            Assert.Equal(baselineRoutingMode, engine.ActiveRoutingMode);
            Assert.Equal(baselineTunFingerprint, engine.TunFingerprint);
            Assert.Equal(baselineAppRoutingFingerprint, engine.ActiveAppRoutingFingerprint);

            // 3. Zero HTTP mutations, zero process spawn, zero driver engagement
            if (OperatingSystem.IsWindows())
            {
                Assert.Empty(fakeHttp.SentRequests);
            }
            else
            {
                // Unix IsRunning probes GET /configs; reload/restart must issue zero HTTP mutations
                Assert.DoesNotContain(fakeHttp.SentRequests, r => r.Method != HttpMethod.Get);
            }
            Assert.Empty(runner.StartCalls);
            Assert.Empty(runner.RunCalls);
            Assert.Equal(0, fakeDriver.EngageCount);
            Assert.Equal(0, fakeDriver.DisengageCount);

            // 4. No hidden earlier failure in statuses
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
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: fakeDriver);

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
                RoutingMode = "full", // Structural change: split -> full triggers forceRestart
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
            var result = await engine.ApplyAsync(settings);
            Assert.False(result);
            // ZERO capability calls on failed Apply exact branch
            Assert.Empty(firewall.UpdateCalls);
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
    public async Task ApplyAsync_HotReloadSucceeds_CallsFirewallCapabilityOnceWithExactGeneratedAndIntent()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-apply-hotsuccess-{Guid.NewGuid():N}");
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
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: null,
            dnsHardening: dnsHardening,
            splitDriver: fakeDriver);

        SetField(engine, "_firewall", firewall);

        const string baselineConfigMode = "generated";
        const string baselineRoutingMode = "full";
        // Baseline matches candidate so NO structural change occurs -> hot-reload branch taken
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

        var baselineTunFingerprint = VpnEngine.ComputeTunFingerprint(settings.Tun);
        var baselineAppRoutingFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint([], settings);

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
        SetField(singBox, "_ownsTunLock", true);

        SetField(engine, "_singBox", singBox);

        try
        {
            var result = await engine.ApplyAsync(settings);
            Assert.True(result, "ApplyAsync should succeed via hot reload.");

            // Exactly ONE call with exact generated JSON and intent
            var call = Assert.Single(firewall.UpdateCalls);
            Assert.True(call.EnabledForFullTunnel, "Full tunnel + BlockOnVpnFail must enable killswitch");
            Assert.Contains("10.0.0.1", call.ConfigJson);
            Assert.Equal(File.ReadAllText(VPNRouter.Core.AppPaths.CurrentConfigPath), call.ConfigJson);

            // Exactly one HTTP PUT and no runner calls
            Assert.Single(fakeHttp.SentRequests, r => r.Method == HttpMethod.Put);
            if (OperatingSystem.IsWindows())
            {
                Assert.Single(fakeHttp.SentRequests);
            }
            else
            {
                Assert.DoesNotContain(fakeHttp.SentRequests, r => r.Method != HttpMethod.Put && r.Method != HttpMethod.Get);
            }
            Assert.Empty(runner.StartCalls);
            Assert.Empty(runner.RunCalls);
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            SetField(singBox, "_ownsTunLock", false);
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
    public void StartupPipeline_ColdOrderingSourceGuard_Phase6SkipsLegacyCapability_AndCommitOccursAfterStartBeforeMonitors()
    {
        // Note: Cold runtime is not exercised directly in unit tests because real sing-box process
        // and OS monitors cannot run without OS network stack / privilege; exact cold ordering is pinned
        // via stripped-comment source guard.
        var source = LoadStartupPipelineSource();
        Assert.True(source != null, "StartupPipeline.cs source could not be loaded.");

        var clean = StripComments(source);

        // 1. Phase 6 skips legacy CreateBlockRules for capability managers
        Assert.Contains("firewall is not ICommittedFirewallConfig", clean, StringComparison.Ordinal);

        // 2. Exact execution order in ExecuteAsync:
        // StartSingBoxPhaseAsync -> UpdateCommittedConfig -> StartMonitorsPhase
        var startIdx = clean.IndexOf("await StartSingBoxPhaseAsync(", StringComparison.Ordinal);
        var commitIdx = clean.IndexOf("committedFirewall.UpdateCommittedConfig(", StringComparison.Ordinal);
        var monitorIdx = clean.IndexOf("StartMonitorsPhase(", StringComparison.Ordinal);

        Assert.True(startIdx >= 0, "ExecuteAsync must await StartSingBoxPhaseAsync");
        Assert.True(commitIdx > startIdx, "Firewall capability commit must occur AFTER sing-box starts");
        Assert.True(monitorIdx > commitIdx, "Firewall capability commit must occur BEFORE monitors start");
    }
}
