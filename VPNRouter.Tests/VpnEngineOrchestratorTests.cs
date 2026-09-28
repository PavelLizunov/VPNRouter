#nullable enable

using System.IO;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VpnEngineOrchestratorTests
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
        public void RaiseDummy() { ProcessStarted?.Invoke(this, new()); ProcessStopped?.Invoke(this, new()); }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildIdleEngine() =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null);
#pragma warning restore CS0618

    [Fact]
    public void Construction_InitialState_IsIdle()
    {
        using var engine = BuildIdleEngine();

        Assert.False(engine.IsRunning);
        Assert.Equal(string.Empty, engine.ActiveProfileName);
        Assert.Null(engine.SingBoxPid);
        Assert.Empty(engine.MonitoredProcesses);
    }

    [Fact]
    public void Construction_DefaultModes_AreGeneratedSplit()
    {
        using var engine = BuildIdleEngine();

        Assert.Equal("generated", engine.ActiveConfigMode);
        Assert.Equal("split", engine.ActiveRoutingMode);
        Assert.Equal(string.Empty, engine.ActiveServerAddress);
    }

    [Fact]
    public void Construction_EventsDefaultToNull_NoListenersFireDuringConstruction()
    {
        var fired = false;
        using var engine = BuildIdleEngine();
        engine.StatusChanged += _ => fired = true;
        engine.Warning += _ => fired = true;
        engine.RestartAttempted += (_, _) => fired = true;
        engine.SingBoxStarted += _ => fired = true;
        engine.ProcessDetected += (_, _) => fired = true;
        engine.AutoFailoverTriggered += _ => fired = true;

        Assert.False(fired);
    }

    [Fact]
    public void Stop_OnIdleEngine_IsNoOp_EmitsStatusEvents()
    {
        using var engine = BuildIdleEngine();
        var statuses = new List<string>();
        engine.StatusChanged += s => statuses.Add(s);

        engine.Stop();

        Assert.Contains("Stopping...", statuses);
        Assert.Contains("Stopped", statuses);
        var stoppingIdx = statuses.IndexOf("Stopping...");
        var stoppedIdx = statuses.IndexOf("Stopped");
        Assert.True(stoppingIdx < stoppedIdx,
            $"Stopping... must precede Stopped (got Stopping@{stoppingIdx}, Stopped@{stoppedIdx})");
    }

    [Fact]
    public void Stop_IsIdempotent_SecondCallDoesNotThrow()
    {
        using var engine = BuildIdleEngine();
        engine.Stop();
        engine.Stop();
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task ApplyAsync_IdleEngine_ReturnsFalse()
    {
        using var engine = BuildIdleEngine();

        var result = await engine.ApplyAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ApplyAsync_IdleEngine_DoesNotEmitApplyingStatus()
    {
        using var engine = BuildIdleEngine();
        var statuses = new List<string>();
        engine.StatusChanged += s => statuses.Add(s);

        await engine.ApplyAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(statuses, s => s.Contains("Applying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dispose_OnIdleEngine_DoesNotThrow()
    {
        var engine = BuildIdleEngine();
        engine.Dispose();
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void Dispose_IsIdempotent_SecondCallIsNoOp()
    {
        var engine = BuildIdleEngine();
        engine.Dispose();
        engine.Dispose();
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void ParseClashApiPort_NullOrInvalid_ReturnsDefault9090()
    {
        Assert.Equal(9090, VpnEngine.ParseClashApiPort(null));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort(string.Empty));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("   "));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("host-no-port"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:abc"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:0"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:65536"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:-5"));
    }

    [Fact]
    public void ParseClashApiPort_ValidHostPort_ParsesPort()
    {
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:9090"));
        Assert.Equal(8080, VpnEngine.ParseClashApiPort("10.0.0.1:8080"));
        Assert.Equal(65535, VpnEngine.ParseClashApiPort("host:65535"));
        Assert.Equal(1, VpnEngine.ParseClashApiPort("host:1"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("[::1]:9090"));
    }

    [Fact]
    public void ResolveCustomConfigPath_LegacyFallback_WhenCustomConfigsListEmpty()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                CustomConfigs = new List<CustomConfigEntry>(),
                CustomConfig = @"C:\nonexistent\legacy.json"
            }
        };

        var path = VpnEngine.ResolveCustomConfigPath(settings);

        Assert.Equal(@"C:\nonexistent\legacy.json", path);
    }

    [Fact]
    public void ResolveCustomConfigPath_MultiConfig_NonExistentFiles_FallsBackToLegacyEmptyConfig()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ActiveCustomConfig = "backup",
                CustomConfigs = new List<CustomConfigEntry>
                {
                    new() { Name = "main",   Path = @"C:\nonexistent\main.json" },
                    new() { Name = "backup", Path = @"C:\nonexistent\backup.json" },
                }
            }
        };

        var path = VpnEngine.ResolveCustomConfigPath(settings);

        Assert.NotNull(path);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void BuildBundledOnlyProfileSources_AlwaysIncludesBuiltInFallback()
    {
        var sources = VpnEngine.BuildBundledOnlyProfileSources();

        Assert.NotEmpty(sources);
        var last = sources[^1];
        Assert.Contains("BuiltIn", last.GetType().Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stop_OrderingPin_HealthMonitorStopsBeforeSingBox()
    {
        var src = LoadVpnEngineSource();
        Assert.SkipUnless(src != null, "VpnEngine.cs source not reachable from test cwd — source-pin skipped");

        var stopIdx = src.IndexOf("public void Stop()");
        Assert.True(stopIdx >= 0, "Source must contain 'public void Stop()'");

        var hmIdx = src.IndexOf("_healthMonitor?.Dispose()", stopIdx);
        var sbIdx = src.IndexOf("_singBox?.Dispose()", stopIdx);

        Assert.True(hmIdx > 0, "Stop must call _healthMonitor?.Dispose()");
        Assert.True(sbIdx > 0, "Stop must call _singBox?.Dispose()");
        Assert.True(hmIdx < sbIdx,
            "BR-6a invariant violated: HealthMonitor must be torn down BEFORE sing-box. " +
            $"Got _healthMonitor at {hmIdx}, _singBox at {sbIdx}.");

        Assert.Contains("BR-6a", src);
    }

    [Fact]
    public void Dispose_CallsStopWhenRunning_LifecycleInvariant()
    {
        var src = LoadVpnEngineSource();
        Assert.SkipUnless(src != null, "VpnEngine.cs source not reachable from test cwd — source-pin skipped");

        var disposeIdx = src!.IndexOf("public void Dispose()");
        Assert.True(disposeIdx >= 0, "Source must contain 'public void Dispose()'");

        var nextMethodIdx = src.IndexOf("    }", disposeIdx);
        Assert.True(nextMethodIdx > disposeIdx, "Dispose method body not delimited");

        var disposeBody = src.Substring(disposeIdx, nextMethodIdx - disposeIdx);
        Assert.Contains("if (IsRunning) Stop()", disposeBody);
    }

    private static string? LoadVpnEngineSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
