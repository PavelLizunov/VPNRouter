#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VpnEngineStartAsyncSeamTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class SlowOrThrowingScanner : IProcessScanner
    {
        private readonly Func<ScanResult>? _func;
        public SlowOrThrowingScanner(Func<ScanResult>? func = null) => _func = func;
        public ScanResult ScanForProfile(Profile profile) => _func != null ? _func() : new();
    }

    private sealed class TrackingFirewallManager : IFirewallManager
    {
        private readonly bool _throwOnCreate;
        private readonly CancellationTokenSource? _cancelOnCreate;
        public bool DeleteAllRulesCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public TrackingFirewallManager(bool throwOnCreate = false, CancellationTokenSource? cancelOnCreate = null)
        {
            _throwOnCreate = throwOnCreate;
            _cancelOnCreate = cancelOnCreate;
        }

        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
        {
            if (_throwOnCreate)
                throw new InvalidOperationException("Simulated firewall rule creation failure during bring-up");

            if (_cancelOnCreate != null)
            {
                _cancelOnCreate.Cancel();
                throw new OperationCanceledException(_cancelOnCreate.Token);
            }
        }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { DeleteAllRulesCalled = true; }
        public void Dispose() { DisposeCalled = true; DeleteAllRules(); }
    }

    private static void PopulateValidServer(AppSettings settings)
    {
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "11111111-2222-3333-4444-555555555555",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                    ShortId = "abcd1234"
                }
            }
        };
        settings.Vless.ActiveServer = "main";
    }

    private static IDisposable EnsureDummySingBoxBinary(AppSettings settings)
    {
        var exePath = OperatingSystem.IsWindows()
            ? Environment.ExpandEnvironmentVariables(settings.SingBox.ExecutablePath)
            : AppPaths.SingBoxExePath;

        var dir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        bool created = false;
        if (!File.Exists(exePath))
        {
            File.WriteAllText(exePath, "#!/bin/sh\nexit 0\n");
            created = true;
        }

        return new ActionDisposable(() =>
        {
            if (created && File.Exists(exePath))
            {
                try { File.Delete(exePath); } catch { }
            }
        });
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _action;
        public ActionDisposable(Action action) => _action = action;
        public void Dispose() => _action();
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
        public void RaiseDummy() { ProcessStarted?.Invoke(this, new()); ProcessStopped?.Invoke(this, new()); }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(IFirewallManager? firewall = null, IProcessScanner? scanner = null) =>
        new VpnEngine(
            scanner: scanner ?? new StubProcessScanner(),
            firewallFactory: () => firewall ?? new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null);
#pragma warning restore CS0618

    private static AppSettings BuildSafePreStartSettings(
        string configMode = "generated",
        string routingMode = "split") =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = configMode,
                RoutingMode = routingMode,
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
            },
            Vless = new VlessConfig(),
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    [Fact]
    public async Task StartAsync_EmptyServers_SubscribeMode_ThrowsActionableMessage()
    {
        var settings = BuildSafePreStartSettings(configMode: "subscribe");

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.NotNull(ex.Message);
        Assert.NotEmpty(ex.Message);
        Assert.False(engine.IsRunning);
        Assert.Null(engine.SingBoxPid);
    }

    [Fact]
    public async Task StartAsync_RemembersSkipVpnConflictCheck_ForFailoverReentry()
    {
        var settings = BuildSafePreStartSettings(configMode: "subscribe");
        using var engine = BuildEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));
        Assert.True(engine.SkipVpnConflictCheckSnapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: false));
        Assert.False(engine.SkipVpnConflictCheckSnapshot);
    }

    [Fact]
    public async Task StartAsync_SubscribeMode_AllSubscriptionsDisabled_Throws()
    {
        var settings = BuildSafePreStartSettings(configMode: "subscribe");
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = false,
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Server = "1.2.3.4",
                        Port = 443,
                        Uuid = "11111111-2222-3333-4444-555555555555"
                    }
                }
            }
        };

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.NotNull(ex.Message);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_EmptyServers_GeneratedMode_Throws()
    {
        var settings = BuildSafePreStartSettings(configMode: "generated");

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.NotNull(ex.Message);
        Assert.False(engine.IsRunning);
        Assert.Empty(engine.ActiveProfileName);
    }

    [Fact]
    public async Task StartAsync_EmptyServers_DoesNotMutateState()
    {
        var settings = BuildSafePreStartSettings(configMode: "generated");

        using var engine = BuildEngine();

        var preIsRunning = engine.IsRunning;
        var preActiveProfile = engine.ActiveProfileName;
        var preServerAddress = engine.ActiveServerAddress;
        var preMonitored = engine.MonitoredProcesses.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.Equal(preIsRunning, engine.IsRunning);
        Assert.Equal(preActiveProfile, engine.ActiveProfileName);
        Assert.Equal(preServerAddress, engine.ActiveServerAddress);
        Assert.Equal(preMonitored, engine.MonitoredProcesses.Count);
    }

    [Fact]
    public async Task StartAsync_NoActiveProfile_SplitMode_Throws()
    {
        var settings = BuildSafePreStartSettings(
            configMode: "generated", routingMode: "split");
        settings.ActiveProfile = null!;
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "11111111-2222-3333-4444-555555555555",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "test_public_key_x25519_base64url_format",
                    ShortId = "abcd1234"
                }
            }
        };
        settings.Vless.ActiveServer = "main";

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.Contains("profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_CustomMode_MissingFile_Throws()
    {
        var settings = BuildSafePreStartSettings(configMode: "custom");
        settings.App.CustomConfig =
            @"C:\definitely\does\not\exist\custom-test.json";
        settings.App.CustomConfigs = new List<CustomConfigEntry>();
        settings.App.ActiveCustomConfig = string.Empty;

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.Contains("Custom config not found", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_CustomMode_InvalidJson_Throws()
    {
        var settings = BuildSafePreStartSettings(configMode: "custom");

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-test-custom-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "this is not json {{{");
        settings.App.CustomConfig = tempPath;

        try
        {
            using var engine = BuildEngine();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

            Assert.Contains("validation", ex.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(engine.IsRunning);
        }
        finally
        {
            try { File.Delete(tempPath); } catch {  }
        }
    }

    [Fact]
    public async Task StartAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var settings = BuildSafePreStartSettings(configMode: "generated");
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "11111111-2222-3333-4444-555555555555",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "test_public_key_x25519_base64url_format",
                    ShortId = "abcd1234"
                }
            }
        };
        settings.Vless.ActiveServer = "main";

        using var engine = BuildEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.StartAsync(settings, cts.Token, skipVpnConflictCheck: true));

        Assert.True(cts.Token.IsCancellationRequested);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task ApplyAsync_OnIdleEngine_ReturnsFalseWithoutInvokingPipeline()
    {
        var settings = BuildSafePreStartSettings(configMode: "generated");

        using var engine = BuildEngine();

        var result = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_SkipVpnConflictCheck_DefaultFalse_StillRunsEmptyServersGuard()
    {
        var settings = BuildSafePreStartSettings(configMode: "generated");

        using var engine = BuildEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteProbeFailoverRestart_NeverConnected_ReturnsFalse_NoBringUp()
    {
        var settings = BuildSafePreStartSettings(configMode: "generated");
        using var engine = BuildEngine();

        var ok = await engine.ExecuteProbeFailoverRestartAsync(
            settings, TestContext.Current.CancellationToken);

        Assert.False(ok, "failover restart must not bring up a tunnel with no live session");
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_StopCalledDuringBringUp_AbortsImmediatelyAndReleasesGate()
    {
        var bringUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdBringUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scanner = new SlowOrThrowingScanner(() =>
        {
            bringUpStarted.TrySetResult();
            holdBringUp.Task.GetAwaiter().GetResult();
            return new ScanResult();
        });

        var settings = BuildSafePreStartSettings(configMode: "generated");
        PopulateValidServer(settings);
        settings.ActiveProfile = "Discord_Privacy";
        settings.App.RoutingMode = "split";

        using var engine = BuildEngine(scanner: scanner);

        var startTask = Task.Run(async () =>
        {
            try
            {
                await engine.StartAsync(settings, CancellationToken.None, skipVpnConflictCheck: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        });

        await bringUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopTask = Task.Run(() => engine.Stop());

        holdBringUp.TrySetResult();

        await Task.WhenAll(startTask, stopTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_FailureDuringBringUp_TearsDownFirewallRulesAndState()
    {
        var firewall = new TrackingFirewallManager(throwOnCreate: true);
        var settings = BuildSafePreStartSettings(configMode: "generated");
        PopulateValidServer(settings);
        settings.ActiveProfile = "Discord_Privacy";

        using var dummyBin = EnsureDummySingBoxBinary(settings);
        using var engine = BuildEngine(firewall: firewall);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, CancellationToken.None, skipVpnConflictCheck: true));

        Assert.Equal("Simulated firewall rule creation failure during bring-up", ex.Message);
        Assert.True(firewall.DeleteAllRulesCalled || firewall.DisposeCalled,
            "TeardownInternal must clean up firewall rules when bring-up throws");
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_CancelledDuringBringUp_InvokesTeardownAndReleasesState()
    {
        using var cts = new CancellationTokenSource();
        var firewall = new TrackingFirewallManager(cancelOnCreate: cts);

        var settings = BuildSafePreStartSettings(configMode: "generated");
        PopulateValidServer(settings);
        settings.ActiveProfile = "Discord_Privacy";
        settings.App.RoutingMode = "split";

        using var dummyBin = EnsureDummySingBoxBinary(settings);
        using var engine = BuildEngine(firewall: firewall);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.StartAsync(settings, cts.Token, skipVpnConflictCheck: true));

        Assert.True(firewall.DeleteAllRulesCalled || firewall.DisposeCalled,
            "Cancellation during bring-up must invoke TeardownInternal to clear partial state");
        Assert.False(engine.IsRunning);
    }
}
