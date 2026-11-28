using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
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

    [Theory]
    [InlineData("unconfirmed-reload")]
    [InlineData("missing-rule-set")]
    [InlineData("missing-ad-filter")]
    [InlineData("failover-missing-ad-filter")]
    [InlineData("failover-unix-stop-refused")]
    [InlineData("failover-linux-exit-unknown")]
    [InlineData("verified-free-missing-rule-set")]
    [InlineData("verified-free-invalid-native")]
    [InlineData("verified-assistant-invalid-native")]
    [InlineData("verified-assistant-missing-route")]
    [InlineData("damaged-built-in")]
    [InlineData("missing-custom-config")]
    [InlineData("imported-unreadable-srs")]
    [InlineData("failover-imported-missing-srs")]
    [InlineData("imported-valid-srs")]
    public async Task ApplyAsync_FailedGenerationOrReload_PreservesActiveTunnel(string scenario)
    {
        bool unknownExit = scenario == "failover-linux-exit-unknown";
        if (unknownExit)
            Assert.SkipUnless(OperatingSystem.IsLinux(), "Exercises Linux capability-mode process ownership.");
        bool refusedStop = unknownExit || scenario == "failover-unix-stop-refused";
        if (refusedStop)
            Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                "Exercises the escalated Unix stop with an isolated ownership record.");

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
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Filter.ByIncludingOnly(e => e.MessageTemplate.Text.StartsWith("[VpnEngine] Apply failed", StringComparison.Ordinal)
                || e.MessageTemplate.Text == "[VpnEngine] {Status}")
            .WriteTo.Sink(sink).CreateLogger();
#pragma warning disable CS0618
        var engine = new VpnEngine(
            scanner: scanner,
            firewallFactory: () => firewall,
            monitorFactory: () => monitor,
            logger: logger,
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
        using var probeCts = new CancellationTokenSource();
        using var lease = refusedStop ? new TunOwnershipLock() : null;
        SetField(engine, "_sessionCts", sessionCts);

        bool importedSet = scenario is "imported-unreadable-srs" or "failover-imported-missing-srs" or "imported-valid-srs";
        bool failover = scenario.StartsWith("failover-", StringComparison.Ordinal);
        bool invalidCandidate = scenario is not ("unconfirmed-reload" or "imported-valid-srs");
        var fakeHttp = new FakeHttpClient().Setup("/configs", "{}", statusCode: invalidCandidate ? 503 : 200);
        var runner = new FakeProcessRunner();
        var singBox = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
            null, fakeHttp, runner);

        using var health = refusedStop
            ? new HealthMonitor(singBox, scanner, firewall, new MonitoringSettings(), api: new FakeSingBoxApi(),
                dnsHardening: dnsHardening)
            : null;
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
            const string originalJson = """{"route":{"final":"proxy"}}""";
            File.WriteAllText(VPNRouter.Core.AppPaths.CurrentConfigPath, originalJson);
            if (refusedStop)
            {
                if (unknownExit)
                {
                    initialHandle.ExitQueryUnavailable = true;
                }
                else if (OperatingSystem.IsMacOS())
                {
                    SetField(singBox, "_macLaunchPending", true);
                    SetField(singBox, "_macPrivilegeLaunchId", Guid.NewGuid().ToString("N"));
                    runner.OnRun(_ => true, new ProcessResult(66, "", "", TimeSpan.Zero, false));
                }
                else
                {
                    // An absent recorded child refuses escalation before any signal.
                    ProcessOwnership.WriteRuntimeOwnerRecord(Path.Combine(tempDir, "runtime-owner.json"),
                        new OwnedProcessIdentity(int.MaxValue, 1, Path.Combine(tempDir, "fixture-sing-box")));
                    SetField(singBox, "_linuxUsedPkexec", true);
                }
                SetField(singBox, "_tunLock", lease);
                SetField(lease!, "_owned", true);
                SetField(singBox, "_ownsTunLock", true);
                SetField(engine, "_probeCts", probeCts);
                SetField(engine, "_firewall", firewall);
                SetField(engine, "_healthMonitor", health);
                SetField(health!, "_shouldBeRunning", true);
            }
            string expectedFailure = "Apply failed: sing-box reload or restart was not confirmed";
            if (scenario is "missing-rule-set" or "verified-free-missing-rule-set")
            {
                // A file at the cache directory path makes resolution fail before it can fetch.
                Directory.CreateDirectory(VPNRouter.Core.AppPaths.CacheDir);
                File.WriteAllText(Path.Combine(VPNRouter.Core.AppPaths.CacheDir, RuleSetCacheManager.CacheSubdir), "fixture");
                settings.App.CustomRules = [new() { Action = "block", Type = "geosite", Value = "example" }];
            }
            if (scenario is "missing-ad-filter" or "failover-missing-ad-filter")
            {
                Directory.CreateDirectory(VPNRouter.Core.AppPaths.CacheDir);
                File.WriteAllText(Path.Combine(VPNRouter.Core.AppPaths.CacheDir, RuleSetCacheManager.CacheSubdir), "fixture");
                settings.App.BlockAds = true;
            }
            if (scenario == "damaged-built-in")
            {
                Directory.CreateDirectory(VPNRouter.Core.AppPaths.GeoDir);
                File.WriteAllBytes(VPNRouter.Core.AppPaths.GeoIpRuPath, RuleSetSamples.IntactEnvelope(12 * 1024));
                File.WriteAllBytes(VPNRouter.Core.AppPaths.GeoSiteRuPath, RuleSetSamples.UpstreamGeositeTldRu());
                settings.App.BypassRussianTraffic = true;
                runner.OnRun(r => r.Arguments.SequenceEqual(["check", "-c", "stdin"]),
                    new ProcessResult(1, "", "", TimeSpan.Zero, false));
            }
            if (scenario == "missing-custom-config")
            {
                settings.App.ConfigMode = "custom";
                settings.App.CustomConfig = Path.Combine(tempDir, "fixture-private-selection.json");
            }
            var srs = Path.Combine(tempDir, "imported.srs");
            if (importedSet)
            {
                settings.App.ConfigMode = "custom";
                settings.App.CustomConfig = Path.Combine(tempDir, "fixture-import.json");
                var native = new
                {
                    outbounds = new object[]
                    {
                        new { type = "socks", tag = "proxy", server = "10.0.0.2", server_port = 1080 },
                        new { type = "direct", tag = "direct" }
                    },
                    route = new { rule_set = new[] { new { type = "local", format = "binary", tag = "imported", path = Path.GetFileName(srs) } } }
                };
                File.WriteAllText(settings.App.CustomConfig, System.Text.Json.JsonSerializer.Serialize(native));
                if (!failover)
                    File.WriteAllBytes(srs, invalidCandidate ? RuleSetSamples.IntactEnvelope() : RuleSetSamples.KeywordRule());
                runner.OnRun(r => r.Arguments.SequenceEqual(["check", "-c", "stdin"]), async r =>
                {
                    var runtime = Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
                    if (runtime != null)
                        return await new ProcessRunner().RunAsync(r with { ExecutablePath = Path.GetFullPath(runtime) },
                            TestContext.Current.CancellationToken);
                    var json = System.Text.Json.Nodes.JsonNode.Parse(r.StdinInput!)!;
                    var selected = json["route"]!["rule_set"]![0]!["path"]!.GetValue<string>();
                    bool readable = File.Exists(selected) && await RuleSetSamples.CheckFixtureContentsAsync(
                        selected, TestContext.Current.CancellationToken);
                    return new ProcessResult(readable ? 0 : 1, "", "", TimeSpan.Zero, false);
                });
            }
            if (invalidCandidate)
            {
                expectedFailure = "Apply failed: check the selected configuration and required rule sets, then retry.";
            }

            bool result;
            if (failover)
            {
                var activeProfile = new Profile { Name = "Original profile" };
                SetField(engine, "_activeProfile", activeProfile);
                SetField(engine, "_warmupConfirmed", true);
                engine.EnterPostStartPhase();
                engine.ResetFailoverContext(settings);
                expectedFailure = "Automatic failover rejected before tunnel replacement.";

                result = await engine.ExecuteFailoverRestartAsync(settings, TestContext.Current.CancellationToken);

                Assert.DoesNotContain("Stopping...", statuses);
                Assert.Same(activeProfile, GetField(engine, "_activeProfile"));
                Assert.True(Assert.IsType<bool>(GetField(engine, "_warmupConfirmed")));
                Assert.Same(settings, GetField(engine, "_failoverSettingsContext"));
                Assert.Equal(0, dnsHardening.RestoreCount);
                if (refusedStop)
                {
                    Assert.True(singBox.ExactStopUnconfirmed);
                    Assert.True(lease!.HasOwnership);
                    Assert.Same(singBox, GetField(engine, "_singBox"));
                    Assert.Same(initialHandle, singBox.OwnedProcessHandle);
                    Assert.Equal(0, initialHandle.DisposeCallCount);
                    if (unknownExit)
                    {
                        Assert.Null(initialHandle.QueryHasExited());
                        Assert.False(engine.TryPrepareColdReconnect());
                        Assert.Equal(0, initialHandle.KillCallCount);
                    }
                    Assert.Same(sessionCts, GetField(engine, "_sessionCts"));
                    Assert.False(sessionCts.IsCancellationRequested);
                    Assert.Same(probeCts, GetField(engine, "_probeCts"));
                    Assert.False(probeCts.IsCancellationRequested);
                    Assert.Same(firewall, GetField(engine, "_firewall"));
                    Assert.Same(health, GetField(engine, "_healthMonitor"));
                    Assert.True(Assert.IsType<bool>(GetField(health!, "_shouldBeRunning")));
                }
            }
            else if (scenario.StartsWith("verified-free-", StringComparison.Ordinal)
                || scenario.StartsWith("verified-assistant-", StringComparison.Ordinal))
            {
                bool missingRoute = scenario == "verified-assistant-missing-route";
                runner.OnRun(r => r.Arguments.SequenceEqual(["check", "-c", "stdin"]),
                    new ProcessResult(missingRoute ? 0 : 1, "", "", TimeSpan.Zero, false));
                var previous = SettingsLoader.Copy(settings);
                previous.Vless.ActiveServer = "previous selection";
                SetField(engine, "_failoverSettingsContext", previous);
                bool saved = false;
                if (scenario.StartsWith("verified-assistant-", StringComparison.Ordinal))
                {
                    var accepted = new AcceptedTunnelInfo(previous, originalJson, "", null,
                        new Dictionary<string, VlessServerEntry>());
                    engine.CommitTunnelInfo(accepted);
                    bool staleSaveCalled = false;
                    var obsolete = new AcceptedTunnelInfo(previous, originalJson, "", null,
                        new Dictionary<string, VlessServerEntry>());
                    Action saveStaleSelection = () => staleSaveCalled = true;
                    Action unexpectedRestore = () => throw new InvalidOperationException("Unexpected settings restore.");
                    Assert.False(await engine.RestoreAssistantSettingsAsync(obsolete, saveStaleSelection,
                        unexpectedRestore, TestContext.Current.CancellationToken));
                    Assert.False(await engine.RestoreAssistantSettingsAsync(null, saveStaleSelection,
                        unexpectedRestore, TestContext.Current.CancellationToken));
                    Assert.False(staleSaveCalled);
                    await Assert.ThrowsAsync<IOException>(() => engine.RestoreAssistantSettingsAsync(accepted,
                        () => throw new IOException("Synthetic save failure"),
                        unexpectedRestore, TestContext.Current.CancellationToken));
                    Assert.False(initialHandle.HasExited);
                    Assert.Same(accepted, engine.AcceptedTunnel);
                    settings.App.RoutingMode = "split";
                    settings.App.RoutingAppsMode = "include";
                    settings.App.RoutingAppsInclude = ["chrome.exe"];
                    settings.App.RoutingAppsIncludeInitialized = true;
                    bool routeInspected = false;
                    bool CheckRegionalRoute(AcceptedTunnelInfo generated)
                    {
                        routeInspected = true;
                        return generated.RoutesRussianTrafficDirect();
                    }
                    result = await engine.ConnectVerifiedAppRoutingAsync(settings,
                        new AssistantRouteIntent(accepted, "chrome.exe", VerifyRoute: missingRoute ? CheckRegionalRoute : null),
                        () => saved = true, skipVpnConflictCheck: false, TestContext.Current.CancellationToken);
                    Assert.Equal(missingRoute, routeInspected);
                    Assert.Same(accepted, engine.AcceptedTunnel);
                    Assert.Same(initialHandle, singBox.OwnedProcessHandle);
                }
                else
                {
                    settings.Vless.Servers[0].UseAsSingleOutbound = true;
                    result = await engine.ConnectVerifiedFreeConfigAsync(settings, () => saved = true,
                        skipVpnConflictCheck: false, TestContext.Current.CancellationToken);
                }
                Assert.False(saved);
                Assert.Equal("previous selection", previous.Vless.ActiveServer);
                Assert.DoesNotContain("Stopping...", statuses);
            }
            else
            {
                result = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken,
                    forceRestart: invalidCandidate);
            }

            Assert.False(result);
            Assert.Equal(unknownExit ? (bool?)null : false, initialHandle.QueryHasExited());
            Assert.True(engine.HasActiveTunnel);
            Assert.Equal(OperatingSystem.IsWindows() || !invalidCandidate, engine.IsRunning);
            Assert.Equal(originalJson, File.ReadAllText(VPNRouter.Core.AppPaths.CurrentConfigPath));

            Assert.Contains(expectedFailure, statuses);
            Assert.Contains(sink.Events, e => e.Properties.ContainsKey("Status") && e.RenderMessage().Contains(expectedFailure));
            if (invalidCandidate && !failover)
                Assert.Contains(sink.Events, e => e.Properties.ContainsKey("ExceptionType") && e.Exception is null);
            sink.AssertExcludes("user-geosite-example", "fixture-private-selection", "TestProfile", "10.0.0.1");
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
            if (importedSet || scenario is "verified-free-invalid-native" or "verified-assistant-invalid-native" or "verified-assistant-missing-route")
            {
                var check = Assert.Single(runner.RunCalls);
                Assert.Equal(new[] { "check", "-c", "stdin" }, check.Arguments);
                Assert.Contains("outbounds", check.StdinInput);
                if (importedSet)
                {
                    var checkedJson = System.Text.Json.Nodes.JsonNode.Parse(check.StdinInput!)!;
                    Assert.Equal(srs, checkedJson["route"]!["rule_set"]![0]!["path"]!.GetValue<string>());
                    Assert.NotNull(checkedJson["experimental"]?["clash_api"]);
                    Assert.DoesNotContain("Stopping...", statuses);
                }
            }
            else if (scenario == "damaged-built-in")
            {
                var check = Assert.Single(runner.RunCalls);
                var deployed = OperatingSystem.IsWindows() ? "sing-box.exe" : VPNRouter.Core.AppPaths.SingBoxExePath;
                Assert.Equal(Path.GetFullPath(deployed), check.ExecutablePath);
                var json = System.Text.Json.Nodes.JsonNode.Parse(check.StdinInput!)!;
                Assert.Equal(VPNRouter.Core.AppPaths.GeoIpRuPath, (string?)json["route"]!["rule_set"]![0]!["path"]);
            }
            else if (refusedStop && OperatingSystem.IsMacOS())
            {
                var stop = Assert.Single(runner.RunCalls);
                Assert.Contains("\"op\":\"stop_launch\"", stop.StdinInput, StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(runner.RunCalls);
            }
            Assert.Equal(0, fakeDriver.EngageCount);
            Assert.Equal(0, fakeDriver.DisengageCount);

            if (unknownExit)
            {
                Assert.Throws<InvalidOperationException>(engine.Stop);
                Assert.Throws<InvalidOperationException>(engine.Dispose);
                Assert.DoesNotContain("Stopped", statuses);
                Assert.Same(singBox, GetField(engine, "_singBox"));
                Assert.Same(initialHandle, singBox.OwnedProcessHandle);
                Assert.Same(firewall, GetField(engine, "_firewall"));
                Assert.True(lease!.HasOwnership);
                Assert.Equal(0, initialHandle.KillCallCount);
                initialHandle.ExitQueryUnavailable = false;
                Assert.False(initialHandle.HasExited);
                initialHandle.SignalExit(0);
                engine.Stop();
                Assert.Null(GetField(engine, "_singBox"));
                Assert.Contains("Stopped", statuses);
                Assert.False(lease.HasOwnership);
            }

            Assert.DoesNotContain(statuses, s => s.StartsWith("Apply failed:") && s != expectedFailure);

            if (scenario.StartsWith("verified-assistant-", StringComparison.Ordinal))
            {
                initialHandle.SignalExit(0);
                engine.Stop();
                Assert.Null(engine.AcceptedTunnel);
                statuses.Clear();
                bool settingsRestored = false;
                Assert.True(await engine.RestoreAssistantSettingsAsync(null, () => settingsRestored = true,
                    () => throw new InvalidOperationException("Unexpected rollback after Stop."),
                    TestContext.Current.CancellationToken));
                Assert.True(settingsRestored);
                Assert.Empty(statuses);
                Assert.Empty(runner.StartCalls);
            }
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            if (lease != null)
            {
                SetField(singBox, "_ownsTunLock", false);
                SetField(lease, "_owned", false);
            }
            SetField(engine, "_sessionCts", null);
            initialHandle.ExitQueryUnavailable = false;
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
        File.WriteAllText(VPNRouter.Core.AppPaths.CurrentConfigPath, "{}");

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
