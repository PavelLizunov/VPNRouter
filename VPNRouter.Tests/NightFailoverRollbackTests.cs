#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
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
/// NIGHT-06 regression tests: AutoFailover stale selector rollback prevention.
/// Verifies that obsolete failover intents do not mutate settings or tried sets,
/// cannot overwrite committed selections on the same settings instance during rollback or persistence,
/// and that VpnEngine wire callbacks correctly invalidate upon reset or user Stop.
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class NightFailoverRollbackTests
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
        public event EventHandler<ProcessEventArgs>? ProcessStarted { add { } remove { } }
        public event EventHandler<ProcessEventArgs>? ProcessStopped { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(
        NullWindowsDnsHardening? dns = null,
        FakeSplitTunnelDriver? splitDriver = null) =>
        new(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: dns ?? new NullWindowsDnsHardening(),
            splitDriver: splitDriver ?? new FakeSplitTunnelDriver());
#pragma warning restore CS0618

    private static IStartupHost CreateStartupHost(VpnEngine engine)
    {
        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic);
        Assert.True(hostType != null, "VpnEngineStartupHost nested type not found on VpnEngine.");
        var host = Activator.CreateInstance(hostType, engine) as IStartupHost;
        Assert.True(host != null, "Could not instantiate VpnEngineStartupHost as IStartupHost.");
        return host;
    }

    private static object? GetField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(field != null, $"Field '{name}' not found on {target.GetType().Name}");
        return field.GetValue(target);
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

    private static AppSettings CreateTestSettings(string activeServerName, string candidateServerName) =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
            },
            Vless = new VlessConfig
            {
                ActiveServer = activeServerName,
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = activeServerName,
                        Server = "10.0.0.1",
                        Port = 443,
                        Uuid = Guid.NewGuid().ToString(),
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                    },
                    new()
                    {
                        Name = candidateServerName,
                        Server = "10.0.0.2",
                        Port = 443,
                        Uuid = Guid.NewGuid().ToString(),
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                    }
                }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleDeadConfigAsync_WhenRestartAwaits_AndCommittedIntentBumpsGenerationAndSetsSelectionC_RetainsSelectionC_AndDoesNotSaveStore(bool restartResult)
    {
        var sameSettings = CreateTestSettings("server-a", "server-b");
        var store = new InMemorySettingsStore();

        long generation = 1;
        var restartStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var failover = new AutoFailoverEngine(
            sameSettings,
            new ConfigSanityCheck(),
            restart: async (ct) =>
            {
                restartStarted.TrySetResult(true);
                return await restartTcs.Task;
            },
            store: store)
        {
            IsCurrentIntent = () => generation == 1
        };

        var handleTask = failover.HandleDeadConfigAsync("probe failed", CancellationToken.None);

        // Wait until failover mutates sameSettings to candidate "server-b" and awaits restart
        await restartStarted.Task;
        Assert.Equal("server-b", sameSettings.Vless.ActiveServer);

        // Emulate committed intent: generation++ and selection C committed on the same settings object
        generation++;
        sameSettings.Vless.ActiveServer = "server-c";
        sameSettings.App.ActiveSubscriptionServer = "server-c";

        // Complete the restart delegate with restartResult (false or true)
        restartTcs.SetResult(restartResult);
        var outcome = await handleTask;

        // Obsolete intent returns switched: false, null server, null message
        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.Null(outcome.UserFacingMessage);

        // Newer committed selection C is retained on sameSettings (not overwritten by rollback or persist)
        Assert.Equal("server-c", sameSettings.Vless.ActiveServer);
        Assert.Equal("server-c", sameSettings.App.ActiveSubscriptionServer);

        // Store was NOT saved
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task HandleDeadConfigAsync_StaleEntry_NoMutation_NoRestart_NoStore()
    {
        var settings = CreateTestSettings("server-a", "server-b");
        var store = new InMemorySettingsStore();
        bool restartInvoked = false;

        var failover = new AutoFailoverEngine(
            settings,
            new ConfigSanityCheck(),
            restart: (ct) =>
            {
                restartInvoked = true;
                return Task.FromResult(true);
            },
            store: store)
        {
            IsCurrentIntent = () => false
        };

        var outcome = await failover.HandleDeadConfigAsync("probe failed", CancellationToken.None);

        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.Null(outcome.UserFacingMessage);

        Assert.False(restartInvoked, "Restart delegate must not be invoked on stale entry.");
        Assert.Equal("server-a", settings.Vless.ActiveServer);
        Assert.Equal(0, store.SaveCount);
        Assert.Empty(failover.TriedServers);
    }

    [Fact]
    public async Task HandleDeadConfigAsync_CurrentIntent_RestartReturnsFalse_RollbackRetained()
    {
        var settings = CreateTestSettings("server-a", "server-b");
        var store = new InMemorySettingsStore();

        var failover = new AutoFailoverEngine(
            settings,
            new ConfigSanityCheck(),
            restart: (ct) => Task.FromResult(false),
            store: store)
        {
            IsCurrentIntent = () => true
        };

        var outcome = await failover.HandleDeadConfigAsync("probe failed", CancellationToken.None);

        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.Null(outcome.UserFacingMessage);

        Assert.Equal("server-a", settings.Vless.ActiveServer);
        Assert.Equal(0, store.SaveCount);
        Assert.Contains("server-b", failover.TriedServers);
    }

    [Fact]
    public async Task HandleDeadConfigAsync_CurrentIntent_RestartReturnsTrue_PersistsNewSelection()
    {
        var settings = CreateTestSettings("server-a", "server-b");
        var store = new InMemorySettingsStore();
        store.Save(settings);
        var initialSaves = store.SaveCount;

        var failover = new AutoFailoverEngine(
            settings,
            new ConfigSanityCheck(),
            restart: (ct) => Task.FromResult(true),
            store: store)
        {
            IsCurrentIntent = () => true
        };

        var outcome = await failover.HandleDeadConfigAsync("probe failed", CancellationToken.None);

        Assert.True(outcome.Switched);
        Assert.Equal("server-b", outcome.NewActiveServer);
        Assert.NotNull(outcome.UserFacingMessage);

        Assert.Equal("server-b", settings.Vless.ActiveServer);
        Assert.Equal(initialSaves + 1, store.SaveCount);

        var loaded = store.Load();
        Assert.Equal("server-b", loaded.Vless.ActiveServer);
    }

    [Fact]
    public void VpnEngine_WireFailover_CallbackRejects_AfterResetWithSameSettingsObject()
    {
        var dns = new NullWindowsDnsHardening();
        var fakeDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, fakeDriver);

        var settings = CreateTestSettings("server-a", "server-b");
        engine.ResetFailoverContext(settings);

        var host = CreateStartupHost(engine);
        var sanity = new ConfigSanityCheck();
        var failover = host.WireFailover(sanity);

        Assert.NotNull(failover.IsCurrentIntent);
        Assert.True(failover.IsCurrentIntent!(), "Callback must accept active generation before reset.");

        // Reset with exact SAME settings instance
        engine.ResetFailoverContext(settings);

        Assert.False(failover.IsCurrentIntent!(), "Callback must reject after reset even with the exact same settings object.");
    }

    [Fact]
    public void VpnEngine_ActualStop_InvalidatesFailoverGenerationAndCallback_SafeFixtureHelpers()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-stop-test-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);

        try
        {
            var dns = new NullWindowsDnsHardening();
            var fakeDriver = new FakeSplitTunnelDriver();
            using var engine = BuildEngine(dns, fakeDriver);

            var settings = CreateTestSettings("server-a", "server-b");
            engine.ResetFailoverContext(settings);

            var host = CreateStartupHost(engine);
            var sanity = new ConfigSanityCheck();
            var failover = host.WireFailover(sanity);

            Assert.NotNull(failover.IsCurrentIntent);
            Assert.True(failover.IsCurrentIntent!(), "Callback must be valid before stop.");

            // Public Stop under safe fixtures (no real scanner/dns/driver, no live process)
            engine.Stop();

            // Generation was incremented and session cancelled, invalidating the failover instance
            Assert.False(failover.IsCurrentIntent!(), "Failover callback must evaluate to false after public Stop.");
            Assert.Null(GetField(engine, "_failover"));
        }
        finally
        {
            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HandleDeadConfigAsync_CancellationDuringRestart_GuardsRollbackWhenIntentObsolete()
    {
        var sameSettings = CreateTestSettings("server-a", "server-b");
        var store = new InMemorySettingsStore();

        long generation = 1;
        var restartStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var failover = new AutoFailoverEngine(
            sameSettings,
            new ConfigSanityCheck(),
            restart: async (ct) =>
            {
                restartStarted.TrySetResult(true);
                await Task.Yield();
                throw new OperationCanceledException();
            },
            store: store)
        {
            IsCurrentIntent = () => generation == 1
        };

        var handleTask = failover.HandleDeadConfigAsync("probe failed", CancellationToken.None);
        await restartStarted.Task;

        // Emulate Apply setting selection C and bumping generation
        generation++;
        sameSettings.Vless.ActiveServer = "server-c";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handleTask);

        // Retains server-c because intent was obsolete, rollback did not overwrite it
        Assert.Equal("server-c", sameSettings.Vless.ActiveServer);
    }

    [Fact]
    public async Task VpnEngine_ActualDispose_InvalidatesFailoverCallback_RetainedFailoverEntryNoSaveNoRestart()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-dispose-test-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);

        try
        {
            var dns = new NullWindowsDnsHardening();
            var fakeDriver = new FakeSplitTunnelDriver();
            var engine = BuildEngine(dns, fakeDriver);

            var settings = CreateTestSettings("server-a", "server-b");
            engine.ResetFailoverContext(settings);

            var host = CreateStartupHost(engine);
            var sanity = new ConfigSanityCheck();
            var failover = host.WireFailover(sanity);

            var store = new InMemorySettingsStore();
            typeof(AutoFailoverEngine).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(failover, store);

            Assert.NotNull(failover.IsCurrentIntent);
            Assert.True(failover.IsCurrentIntent!(), "Callback must be valid before dispose.");

            // Actual Dispose once (helper is idempotent; do not double-dispose)
            engine.Dispose();

            // Old callback evaluates to false after Dispose
            Assert.False(failover.IsCurrentIntent!(), "Failover callback must evaluate to false after Dispose.");

            // Retained failover handle rejects on entry without mutating settings, saving, or restarting
            var outcome = await failover.HandleDeadConfigAsync("probe failed", CancellationToken.None);

            Assert.False(outcome.Switched);
            Assert.Null(outcome.NewActiveServer);
            Assert.Null(outcome.UserFacingMessage);
            Assert.Equal("server-a", settings.Vless.ActiveServer);
            Assert.Equal(0, store.SaveCount);
            Assert.Empty(failover.TriedServers);
        }
        finally
        {
            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
