#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class VpnEngineHotReloadLifecycleTests
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
#pragma warning disable CS0067 // stub implements the interface; the events are never raised
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
#pragma warning restore CS0067
        public int StartCount;
        public int StopCount;
        public int DisposeCount;

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Dispose() => DisposeCount++;
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

    private static AppSettings BuildHappyPathSettings(string singBoxExePath) =>
        new()
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
            $"vpnrouter-hot-reload-stub-{Guid.NewGuid():N}.exe");
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

    private static (FakeProcessRunner runner, List<FakeProcessHandle> spawnedHandles)
        BuildFreshHandleSpawnFake(int startPid = 99301)
    {
        var spawnedHandles = new List<FakeProcessHandle>();
        var runner = new FakeProcessRunner();
        var nextPid = startPid;
        runner.OnStart(_ => true, _ =>
        {
            var handle = new FakeProcessHandle(pid: nextPid++);
            spawnedHandles.Add(handle);
            return handle;
        });
        return (runner, spawnedHandles);
    }

    private static async Task<(VpnEngine engine,
                                NullWindowsDnsHardening dnsHardening,
                                List<FakeProcessHandle> spawnedHandles,
                                StubFirewallManager firewall,
                                StubProcessMonitor monitor,
                                AppSettings settings,
                                IDisposable cleanup)>
        StartHappyPathAsync(bool isolatedProfile = false)
    {
        var previousDataDir = AppPaths.DataDir;
        var tempDataDir = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-hot-reload-data-{Guid.NewGuid():N}");
        AppPaths.OverrideDataDir(tempDataDir);
        AppPaths.EnsureDirectories();

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, spawnedHandles) = BuildFreshHandleSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe);
        if (isolatedProfile)
        {
            var profile = Path.Combine(tempDataDir, "receipt-profile.json");
            File.WriteAllText(profile, """{"profiles":[{"name":"Receipt","dns_mode":"vpn_only","processes":[]}]}""");
            settings.ProfileSources = [new ProfileSource { Type = "local", Path = profile }];
            settings.ActiveProfile = "Receipt";
            settings.SingBox.ClashApi = "127.0.0.1:0";
        }

        var cleanup = new HotReloadCleanup(
            engine,
            stubExe,
            previousDataDir,
            tempDataDir,
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

        return (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup);
    }

    private sealed class HotReloadCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly string _previousDataDir;
        private readonly string _tempDataDir;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private bool _disposed;

        public HotReloadCleanup(
            VpnEngine engine,
            string stubExe,
            string previousDataDir,
            string tempDataDir,
            IProcessRunner prevSingBoxRunner,
            IProcessRunner prevTunDiagRunner)
        {
            _engine = engine;
            _stubExe = stubExe;
            _previousDataDir = previousDataDir;
            _tempDataDir = tempDataDir;
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
            AppPaths.OverrideDataDir(_previousDataDir);
            try { Directory.Delete(_tempDataDir, recursive: true); } catch { }
            try { File.Delete(_stubExe); } catch { }
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ServiceSettingsReceipt_Characterization_WaitsForReplacementAndRecoversAcceptedPolicy(
        bool ready, bool resumeFails, bool crashAfterAcceptance)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows service Apply uses the Windows restart path.");
        var previousWarmup = StartupPipeline.WarmupHttp;
        StartupPipeline.WarmupHttp = new FakeHttpClient().Setup("generate_204", "");
        try
        {
            var (engine, _, handles, _, _, initial, cleanup) = await StartHappyPathAsync(isolatedProfile: true);
            using var fixture = cleanup;
            var previousJson = File.ReadAllText(AppPaths.CurrentConfigPath);
            var candidate = SettingsLoader.Copy(initial);
            candidate.Vless.Servers[0].Server = "192.0.2.42";
            candidate.SingBox.ClashApi = "0.0.0.0:39990";
            if (resumeFails) candidate.Monitoring.HealthCheckInterval = -2;
            bool accepted = ready && !resumeFails;
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var monitor = Assert.IsType<HealthMonitor>(typeof(VpnEngine).GetField("_healthMonitor", flags)!.GetValue(engine));
            var failoverSettings = typeof(VpnEngine).GetField("_failoverSettingsContext", flags)!;
            var originalSnapshot = Assert.IsType<AppSettings>(failoverSettings.GetValue(engine));
            Assert.NotSame(initial, originalSnapshot);
            var initialServer = initial.Vless.Servers[0].Server;
            var initialInfo = Assert.IsType<AcceptedTunnelInfo>(engine.AcceptedTunnel);
            Assert.Equal(initialServer, initialInfo.Server.Address);
            initial.App.ConfigMode = "custom";
            initial.App.CustomConfig = "unsaved-fixture.json";
            initial.Vless.Servers[0].Server = "unsaved.invalid";
            var recover = typeof(HealthMonitor).GetMethod("GenerateConfigJson", flags)!;
            var beforeApply = Assert.IsType<string>(recover.Invoke(monitor, [new List<string>(), null]));
            Assert.Contains(initialServer, beforeApply);
            Assert.DoesNotContain("unsaved.invalid", beforeApply);
            var deferredLift = typeof(HealthMonitor).GetField("_deferredBlockRuleDisable", flags)!;
            deferredLift.SetValue(monitor, 1);
            engine.StatusChanged += status =>
            {
                if (crashAfterAcceptance && status.StartsWith("Applied (restart", StringComparison.Ordinal))
                    handles[1].SignalExit(1);
            };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ConfirmServiceReplacement = async (handle, json, ct) =>
            {
                Assert.Same(handles[1], handle);
                Assert.Contains("192.0.2.42", json);
                entered.TrySetResult();
                return await result.Task.WaitAsync(ct);
            };

            var apply = engine.ApplyAsync(candidate, TestContext.Current.CancellationToken);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.False(apply.IsCompleted);
                Assert.Same(initialInfo, engine.AcceptedTunnel);
                Assert.Same(originalSnapshot, failoverSettings.GetValue(engine));
                candidate.App.ConfigMode = "custom";
                candidate.Vless.Servers[0].Server = "also-unsaved.invalid";
            }
            finally { result.TrySetResult(ready); }
            Assert.Equal(accepted, await apply);
            Assert.Equal(accepted ? "192.0.2.42" : initialServer, engine.AcceptedTunnel?.Server.Address);
            var activeSnapshot = Assert.IsType<AppSettings>(failoverSettings.GetValue(engine));
            Assert.Equal(!accepted, ReferenceEquals(originalSnapshot, activeSnapshot));
            Assert.NotSame(candidate, activeSnapshot);
            Assert.Equal("generated", activeSnapshot.App.ConfigMode);
            Assert.Equal(1, Assert.IsType<int>(deferredLift.GetValue(monitor)));
            if (crashAfterAcceptance) Assert.True(handles[1].HasExited);
            if (!accepted) Assert.Equal(previousJson, File.ReadAllText(AppPaths.CurrentConfigPath));
            var recoveryJson = Assert.IsType<string>(recover.Invoke(monitor, [new List<string>(), null]));
            using var json = System.Text.Json.JsonDocument.Parse(recoveryJson);
            var proxies = json.RootElement.GetProperty("outbounds").EnumerateArray()
                .Where(outbound => outbound.GetProperty("type").GetString() == "vless").ToArray();
            Assert.Single(proxies, outbound => outbound.GetProperty("tag").GetString() == "proxy");
            var expectedServer = accepted ? "192.0.2.42" : initialServer;
            Assert.All(proxies, outbound => Assert.Equal(expectedServer, outbound.GetProperty("server").GetString()));
        }
        finally { StartupPipeline.WarmupHttp = previousWarmup; }
    }

    [Theory]
    [InlineData("external", true)]
    [InlineData("external", false)]
    [InlineData("desktop-picker", true)]
    [InlineData("desktop-picker", false)]
    [InlineData("legacy", true)]
    [InlineData("legacy", false)]
    public async Task ServiceSettingsReceipt_Characterization_RecoversAcceptedImport(string registration, bool acceptEdit)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exercises the Windows replacement path with fake processes.");
        var previousWarmup = StartupPipeline.WarmupHttp;
        StartupPipeline.WarmupHttp = new FakeHttpClient().Setup("generate_204", "");
        try
        {
            var (engine, _, _, _, _, initial, cleanup) = await StartHappyPathAsync(isolatedProfile: true);
            using var fixture = cleanup;
            var runner = Assert.IsType<FakeProcessRunner>(SingBoxManager.Runner);
            runner.OnRun(r => r.Arguments.SequenceEqual(["check", "-c", "stdin"]),
                new ProcessResult(0, "", "", TimeSpan.Zero, false));

            var directory = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "imports")).FullName;
            var source = Path.Combine(directory, "source.json");
            var srs = Path.Combine(directory, "allowed.srs");
            File.WriteAllBytes(srs, RuleSetSamples.KeywordRule());
            const string first = """
                {"outbounds":[{"type":"socks","tag":"proxy","server":"192.0.2.41","server_port":1080},
                    {"type":"direct","tag":"direct"}],
                 "route":{"rule_set":[{"type":"local","tag":"allowed","format":"binary","path":"allowed.srs"}]}}
                """;
            File.WriteAllText(source, first);
            var normalizedFirst = CustomConfigInjector.ReadImport(source);
            var settings = SettingsLoader.Copy(initial);
            settings.App.ConfigMode = "custom";
            if (registration == "legacy")
            {
                settings.App.CustomConfig = source;
                settings.App.CustomConfigs = [];
            }
            else
            {
                if (registration == "desktop-picker")
                    source = CustomConfigInjector.CopyToProgramData(source, "desk");
                settings.App.CustomConfigs = [new CustomConfigEntry { Name = "desk", Path = source }];
                settings.App.ActiveCustomConfig = ""; // Fall back to the first named entry.
            }
            var acceptedPath = CustomConfigInjector.GetAcceptedImportPath(source, registration == "legacy" ? null : "desk");
            Assert.NotEqual(source, acceptedPath);
            SettingsLoader.Save(settings);
            Assert.True(await engine.ApplyAsync(settings, TestContext.Current.CancellationToken));
            Assert.Equal(normalizedFirst, File.ReadAllText(acceptedPath));

            File.WriteAllText(source, normalizedFirst.Replace("192.0.2.41", "192.0.2.42"));
            var normalizedEdit = CustomConfigInjector.ReadImport(source);
            string? copyBeforeConfirmation = null;
            string? candidateJson = null;
            engine.ConfirmServiceReplacement = (_, json, _) =>
            {
                copyBeforeConfirmation = File.ReadAllText(acceptedPath);
                candidateJson = json;
                File.WriteAllText(source, normalizedFirst.Replace("192.0.2.41", "192.0.2.43"));
                return Task.FromResult(acceptEdit);
            };
            Assert.Equal(acceptEdit, await engine.ApplyAsync(settings, TestContext.Current.CancellationToken));
            Assert.Equal(normalizedFirst, copyBeforeConfirmation);
            Assert.NotNull(candidateJson);
            Assert.Contains("192.0.2.42", candidateJson);
            var accepted = acceptEdit ? normalizedEdit : normalizedFirst;
            Assert.Equal(accepted, File.ReadAllText(acceptedPath));
            using var raw = System.Text.Json.JsonDocument.Parse(accepted);
            Assert.Equal(srs, raw.RootElement.GetProperty("route").GetProperty("rule_set")[0].GetProperty("path").GetString());

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var monitor = Assert.IsType<HealthMonitor>(typeof(VpnEngine).GetField("_healthMonitor", flags)!.GetValue(engine));
            void CheckProxy(string json)
            {
                using var recovered = System.Text.Json.JsonDocument.Parse(json);
                var proxy = Assert.Single(recovered.RootElement.GetProperty("outbounds").EnumerateArray(),
                    ob => ob.GetProperty("tag").GetString() == "proxy");
                Assert.Equal(acceptEdit ? "192.0.2.42" : "192.0.2.41", proxy.GetProperty("server").GetString());
            }
            void CheckRecovery()
            {
                var recover = typeof(HealthMonitor).GetMethod("GenerateConfigJson", flags)!;
                CheckProxy(Assert.IsType<string>(recover.Invoke(monitor, [new List<string>(), null])));
            }
            CheckRecovery();
            Assert.Equal(source, VpnEngine.ResolveCustomConfigPath(settings));

            if (acceptEdit)
            {
                File.Delete(source);
                CheckRecovery();
            }
            engine.Stop();
            var reopened = SettingsLoader.Load();
            using var restored = BuildEngine(new NullWindowsDnsHardening(), out _, out _);
            await restored.StartAsync(reopened, TestContext.Current.CancellationToken,
                skipVpnConflictCheck: true, restoreAcceptedImport: true);
            CheckProxy(File.ReadAllText(AppPaths.CurrentConfigPath));
            if (!acceptEdit) File.Delete(source);
            Assert.Equal(acceptedPath, VpnEngine.ResolveCustomConfigPath(reopened));
            if (registration != "legacy")
            {
                var removed = Assert.Single(reopened.App.CustomConfigs);
                reopened.App.CustomConfigs.Clear();
                CustomConfigInjector.ForgetAcceptedImport(removed, reopened.App.CustomConfigs);
                Assert.False(File.Exists(acceptedPath));
                var activeMonitor = Assert.IsType<HealthMonitor>(typeof(VpnEngine).GetField("_healthMonitor", flags)!.GetValue(restored));
                var recover = typeof(HealthMonitor).GetMethod("GenerateConfigJson", flags)!;
                CheckProxy(Assert.IsType<string>(recover.Invoke(activeMonitor, [new List<string>(), null])));
                Assert.False(File.Exists(acceptedPath));
            }
        }
        finally { StartupPipeline.WarmupHttp = previousWarmup; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceSettingsReceipt_Characterization_FailedStartupPublicationRetiresChildAndRetryRestoresRecovery(
        bool refuseFirstCleanup)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses the Windows lifecycle with fake children.");
        var previousWarmup = StartupPipeline.WarmupHttp;
        StartupPipeline.WarmupHttp = new FakeHttpClient().Setup("generate_204", "");
        try
        {
            var (engine, _, handles, _, _, settings, cleanup) = await StartHappyPathAsync(isolatedProfile: true);
            using var fixture = cleanup;
            engine.Stop();
            var source = Path.Combine(AppPaths.DataDir, "fixture-import.json");
            File.WriteAllText(source, """
                {"outbounds":[{"type":"socks","tag":"proxy","server":"192.0.2.41","server_port":1080},
                              {"type":"direct","tag":"direct"}]}
                """);
            settings.App.ConfigMode = "custom";
            settings.App.CustomConfigs = [new CustomConfigEntry { Name = "fixture", Path = source }];
            var snapshot = VpnEngine.ResolveCustomConfigFiles(settings).AcceptedPath!;
            Directory.CreateDirectory(snapshot);
            var statuses = new List<string>();
            engine.StatusChanged += statuses.Add;
            engine.ConfirmServiceReplacement = (_, _, _) => Task.FromResult(true);
            void ConfigureChild(int _) => handles[^1].RefuseKill = refuseFirstCleanup;
            engine.SingBoxStarted += ConfigureChild;
            var failure = await Record.ExceptionAsync(() => engine.StartAsync(settings, TestContext.Current.CancellationToken,
                skipVpnConflictCheck: true));
            engine.SingBoxStarted -= ConfigureChild;
            try
            {
                if (refuseFirstCleanup)
                    Assert.IsType<InvalidOperationException>(failure);
                else
                    Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
                Assert.Equal(2, handles.Count);
                Assert.Equal(!refuseFirstCleanup, handles[^1].HasExited);
                Assert.Equal(refuseFirstCleanup, engine.HasActiveTunnel);
                Assert.DoesNotContain("Warming up network...", statuses);
                Assert.DoesNotContain("VPN Router is running", statuses);
                Assert.False(await engine.ApplyAsync(settings, TestContext.Current.CancellationToken));
                if (refuseFirstCleanup)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(settings,
                        TestContext.Current.CancellationToken, skipVpnConflictCheck: true));
                    Assert.Equal(2, handles.Count);
                }
            }
            finally { handles[^1].RefuseKill = false; }
            Directory.Delete(snapshot);
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);
            Assert.Equal(3, handles.Count);
            Assert.True(engine.HasActiveTunnel);
            Assert.Equal(CustomConfigInjector.ReadImport(source), File.ReadAllText(snapshot));

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var monitor = Assert.IsType<HealthMonitor>(typeof(VpnEngine).GetField("_healthMonitor", flags)!.GetValue(engine));
            monitor.Stop();
            Assert.False(await engine.ApplyAsync(settings, TestContext.Current.CancellationToken));
            Assert.Equal(3, handles.Count);
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);
            Assert.Equal(4, handles.Count);
            Assert.True(handles[2].HasExited);
            Assert.False(handles[3].HasExited);
            var restored = Assert.IsType<HealthMonitor>(typeof(VpnEngine).GetField("_healthMonitor", flags)!.GetValue(engine));
            var manager = Assert.IsType<SingBoxManager>(typeof(VpnEngine).GetField("_singBox", flags)!.GetValue(engine));
            Assert.True(restored.IsMonitoring(manager));
        }
        finally { StartupPipeline.WarmupHttp = previousWarmup; }
    }

    [Fact]
    public async Task Characterization_ChildExitReturnsWhileMonitorIsLockedAndStaleRecoveryIsDiscarded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The fixture uses the Windows lifecycle with fake children.");
        var previousWarmup = StartupPipeline.WarmupHttp;
        StartupPipeline.WarmupHttp = new FakeHttpClient().Setup("generate_204", "");
        try
        {
            var (engine, _, handles, firewall, _, settings, cleanup) = await StartHappyPathAsync(isolatedProfile: true);
            using var fixture = cleanup;
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var monitor = Assert.IsType<HealthMonitor>(typeof(VpnEngine).GetField("_healthMonitor", flags)!.GetValue(engine));
            var gate = typeof(HealthMonitor).GetField("_settingsWork", flags)!.GetValue(monitor)!;
            using var returned = new ManualResetEventSlim();
            Task signal;
            Task stale;
            lock (gate)
            {
                signal = Task.Run(() => { handles[0].SignalExit(1); returned.Set(); });
                Assert.True(returned.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                stale = monitor.QueueCrashRecoveryAsync(handles[0]);
                monitor.PauseForApply();
                monitor.ResumeAfterApply(new Profile { Name = "Receipt" }, settings, scan: null);
            }
            await Task.WhenAll(signal, stale);
            Assert.Equal(0, firewall.EnableBlockRulesCount);
            using var differentChild = new FakeProcessHandle(handles[0].Pid);
            await monitor.QueueCrashRecoveryAsync(differentChild);
            Assert.Equal(0, firewall.EnableBlockRulesCount);
            await monitor.QueueCrashRecoveryAsync(handles[0]);
            Assert.Equal(1, firewall.EnableBlockRulesCount);
        }
        finally { StartupPipeline.WarmupHttp = previousWarmup; }
    }

    [Fact]
    public async Task Apply_OnRunningEngine_RunsHotReloadFallbackToRestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite + SingBoxManager.Restart's Windows-specific TUN cleanup are Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.True(engine.IsRunning);
        Assert.Single(spawnedHandles);
        var initialHandle = spawnedHandles[0];
        Assert.False(initialHandle.HasExited);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.True(ok);

        Assert.True(initialHandle.HasExited);

        Assert.True(spawnedHandles.Count >= 2,
            $"Expected at least 2 spawned handles (Start + Restart), got {spawnedHandles.Count}");

        var latestHandle = spawnedHandles[^1];
        Assert.False(latestHandle.HasExited);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_IsNoOp_NotSecondSpawn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.True(engine.IsRunning);
        Assert.Single(spawnedHandles);
        var initialHandle = spawnedHandles[0];
        Assert.False(initialHandle.HasExited);

        await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);

        Assert.Single(spawnedHandles);
        Assert.False(initialHandle.HasExited);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public async Task Apply_OnRunningEngine_DoesNotMutateDnsHardening()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(ok);

        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);
    }

    [Fact]
    public async Task Apply_AfterStop_ReturnsFalse_AndDoesNotRespawn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        engine.Stop();
        var handlesAfterStop = spawnedHandles.Count;

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Equal(handlesAfterStop, spawnedHandles.Count);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task Apply_ConcurrentWithStop_NeverResurrectsTunnel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        var applyTask = Task.Run(() => engine.ApplyAsync(settings, CancellationToken.None));
        var stopTask = Task.Run(() => engine.Stop(), TestContext.Current.CancellationToken);
        await stopTask;
        await applyTask;

        Assert.False(engine.IsRunning);
        Assert.True(spawnedHandles[^1].HasExited,
            "no sing-box handle may remain alive after Stop has completed");
    }

    [Fact]
    public async Task Apply_OnRunningEngine_PreservesFirewallAndMonitorReferences()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.Equal(1, monitor.StartCount);

        Assert.Equal(0, firewall.CreateBlockRulesCount);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(ok);

        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(0, monitor.StopCount);
        Assert.Equal(0, firewall.CreateBlockRulesCount);
        Assert.Equal(0, firewall.DeleteAllRulesCount);
    }
}
