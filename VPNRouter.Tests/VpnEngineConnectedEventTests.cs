#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class VpnEngineConnectedEventTests
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

    private static void SetField(object target, string name, object? value)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? (name.StartsWith('_') && name.Length > 1
                ? type.GetField($"<{char.ToUpperInvariant(name[1])}{name[2..]}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                : null);
        if (field is null)
            throw new InvalidOperationException($"Field '{name}' not found on {type.FullName}.");
        field.SetValue(target, value);
    }

#pragma warning disable CS0618
    private static VpnEngine BuildIdleEngine() =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: new NullWindowsDnsHardening(),
            splitDriver: new FakeSplitTunnelDriver());
#pragma warning restore CS0618

    private static (SingBoxManager manager, FakeProcessHandle handle) CreateFakeManager(int pid)
    {
        var fakeRunner = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid);
        fakeRunner.OnStart(_ => true, _ => fakeHandle);

        var fakeHttp = new FakeHttpClient();

        var singBoxSettings = new SingBoxSettings { ClashApi = "127.0.0.1:9090" };
        var manager = new SingBoxManager(singBoxSettings, logger: null, http: fakeHttp, runner: fakeRunner);
        SetField(manager, "_handle", fakeHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager, SingBoxState.Running);
        SetField(manager, "State", SingBoxState.Running);
        return (manager, fakeHandle);
    }

    private static FakeHttpClient GetFakeHttp(SingBoxManager manager)
    {
        var field = typeof(SingBoxManager).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        return (FakeHttpClient)field!.GetValue(manager)!;
    }

    private static void InvokeSetSingBoxManager(object host, SingBoxManager manager)
    {
        var method = host.GetType().GetMethod("SetSingBoxManager",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(SingBoxManager) },
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VpnEngineStartupHost.SetSingBoxManager(SingBoxManager) not found.");
        method.Invoke(host, new object?[] { manager });
    }

    private static void InvokeOnSingBoxStarted(object host, int pid)
    {
        var method = host.GetType().GetMethod("OnSingBoxStarted",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VpnEngineStartupHost.OnSingBoxStarted(int) not found.");
        method.Invoke(host, new object?[] { pid });
    }

    private static void SafeDetach(VpnEngine engine)
    {
        SetField(engine, "_singBox", null);
    }

    private static object BuildHostAdapter(VpnEngine engine)
    {
        var hostType = typeof(VpnEngine).GetNestedType(
            "VpnEngineStartupHost",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "VpnEngine.VpnEngineStartupHost nested type not found. " +
                "Has it been renamed in a refactor?");
        return Activator.CreateInstance(
            hostType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { engine },
            culture: null)
            ?? throw new InvalidOperationException(
                "Could not construct VpnEngineStartupHost via reflection.");
    }

    private static void InvokeOnConnected(object host, int pid)
    {
        var method = host.GetType().GetMethod("OnConnected",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VpnEngineStartupHost.OnConnected(int) not found.");
        method.Invoke(host, new object?[] { pid });
    }

    [Fact]
    public void Connected_SuccessBranchOnly_FiresViaHostAdapter()
    {
        using var sessionCts = new CancellationTokenSource();
        using var engine = BuildIdleEngine();
        SetField(engine, "_sessionCts", sessionCts);

        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host = BuildHostAdapter(engine);
        var (manager, handle) = CreateFakeManager(31415);
        InvokeSetSingBoxManager(host, manager);
        InvokeOnSingBoxStarted(host, 31415);

        InvokeOnConnected(host, pid: 31415);

        Assert.Single(captured);
        Assert.Equal(31415, captured[0]);
        Assert.Empty(GetFakeHttp(manager).SentRequests);

        SafeDetach(engine);
    }

    [Fact]
    public void Connected_FailureBranchSilent_SourcePin()
    {
        var sourcePath = LocateStartupPipelineSource();
        var source = File.ReadAllText(sourcePath);

        var totalSites = CountSubstring(source, "_host.OnConnected(");
        Assert.True(totalSites == 1,
            $"Expected exactly 1 _host.OnConnected call site in " +
            $"StartupPipeline.cs, found {totalSites}. If the new site is " +
            $"intentional (e.g. Stage 3+ migration), update this test to " +
            $"reflect the new contract.");

        var methodStart = source.IndexOf(
            "private void ScheduleWarmupProbe(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0,
            "Could not locate ScheduleWarmupProbe in StartupPipeline.cs " +
            "source. Has the method been renamed?");

        var failureBranchStart = source.IndexOf(
            "TUN warm-up failed after",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(failureBranchStart > methodStart,
            "Could not locate failure-branch anchor 'TUN warm-up failed " +
            "after' inside ScheduleWarmupProbe.");

        var failureBranchEnd = source.IndexOf("}, ct);",
            failureBranchStart, StringComparison.Ordinal);
        Assert.True(failureBranchEnd > failureBranchStart,
            "Could not locate failure-branch terminator '}, ct);'.");

        var failureBranch = source.Substring(
            failureBranchStart, failureBranchEnd - failureBranchStart);
        Assert.DoesNotContain("_host.OnConnected(", failureBranch);

        Assert.Contains("OnStatus($\"Connected (PID {pidSnapshot})", failureBranch);
    }

    [Fact]
    public void Connected_FiresOncePerLifecycle_TwoCallsTwoEvents()
    {
        using var sessionCts = new CancellationTokenSource();
        using var engine = BuildIdleEngine();
        SetField(engine, "_sessionCts", sessionCts);

        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host1 = BuildHostAdapter(engine);
        var (manager1, handle1) = CreateFakeManager(11111);
        InvokeSetSingBoxManager(host1, manager1);
        InvokeOnSingBoxStarted(host1, 11111);
        InvokeOnConnected(host1, pid: 11111);

        var host2 = BuildHostAdapter(engine);
        var (manager2, handle2) = CreateFakeManager(22222);
        InvokeSetSingBoxManager(host2, manager2);
        InvokeOnSingBoxStarted(host2, 22222);
        InvokeOnConnected(host2, pid: 22222);

        Assert.Equal(2, captured.Count);
        Assert.Equal(11111, captured[0]);
        Assert.Equal(22222, captured[1]);
        Assert.Empty(GetFakeHttp(manager1).SentRequests);
        Assert.Empty(GetFakeHttp(manager2).SentRequests);

        SafeDetach(engine);
    }

    [Fact]
    public void Connected_NullSubscription_DoesNotThrow()
    {
        using var sessionCts = new CancellationTokenSource();
        using var engine = BuildIdleEngine();
        SetField(engine, "_sessionCts", sessionCts);

        var host = BuildHostAdapter(engine);
        var (manager, handle) = CreateFakeManager(99999);
        InvokeSetSingBoxManager(host, manager);
        InvokeOnSingBoxStarted(host, 99999);

        var ex = Record.Exception(() => InvokeOnConnected(host, pid: 99999));
        Assert.Null(ex);

        SafeDetach(engine);
    }

    [Fact]
    public void Connected_SameManagerSamePid_HandleReplacement_Suppressed()
    {
        using var sessionCts = new CancellationTokenSource();
        using var engine = BuildIdleEngine();
        SetField(engine, "_sessionCts", sessionCts);

        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host = BuildHostAdapter(engine);
        var (manager, handle) = CreateFakeManager(44444);
        InvokeSetSingBoxManager(host, manager);
        InvokeOnSingBoxStarted(host, 44444);

        var replacementHandle = new FakeProcessHandle(44444);
        SetField(manager, "_handle", replacementHandle);

        InvokeOnConnected(host, pid: 44444);

        Assert.Empty(captured);

        SafeDetach(engine);
    }

    [Fact]
    public void Connected_FailStop_ActualEngineStop_UsesFakeSeams_SuppressedWithoutNetworking()
    {
        using var sessionCts = new CancellationTokenSource();
        using var engine = BuildIdleEngine();
        SetField(engine, "_sessionCts", sessionCts);

        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host = BuildHostAdapter(engine);
        var (manager, handle) = CreateFakeManager(55555);
        InvokeSetSingBoxManager(host, manager);
        InvokeOnSingBoxStarted(host, 55555);

        engine.Stop();

        InvokeOnConnected(host, pid: 55555);

        Assert.Empty(captured);
        Assert.Empty(GetFakeHttp(manager).SentRequests);

        SafeDetach(engine);
    }

    [Fact]
    public void Connected_FailStop_HandleExitedOrStateNotRunning_SuppressedWithoutNetworking()
    {
        using var sessionCts = new CancellationTokenSource();
        using var engine = BuildIdleEngine();
        SetField(engine, "_sessionCts", sessionCts);

        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host = BuildHostAdapter(engine);
        var (manager, handle) = CreateFakeManager(66666);
        InvokeSetSingBoxManager(host, manager);
        InvokeOnSingBoxStarted(host, 66666);

        handle.Kill();
        Assert.True(handle.HasExited);

        InvokeOnConnected(host, pid: 66666);
        Assert.Empty(captured);
        Assert.Empty(GetFakeHttp(manager).SentRequests);

        var (manager2, handle2) = CreateFakeManager(77777);
        InvokeSetSingBoxManager(host, manager2);
        InvokeOnSingBoxStarted(host, 77777);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager2, SingBoxState.Stopped);
        SetField(manager2, "State", SingBoxState.Stopped);

        InvokeOnConnected(host, pid: 77777);
        Assert.Empty(captured);
        Assert.Empty(GetFakeHttp(manager2).SentRequests);

        SafeDetach(engine);
    }

    private static string LocateStartupPipelineSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var slnCandidate = Path.Combine(dir.FullName, "VPNRouter.sln");
            if (File.Exists(slnCandidate))
            {
                var srcPath = Path.Combine(
                    dir.FullName,
                    "VPNRouter.Core", "Services", "StartupPipeline.cs");
                if (File.Exists(srcPath)) return srcPath;
                throw new FileNotFoundException(
                    $"Found repo root at {dir.FullName} but " +
                    $"StartupPipeline.cs missing: {srcPath}");
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate repo root (no VPNRouter.sln found in " +
            $"any parent of {AppContext.BaseDirectory})");
    }

    private static int CountSubstring(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
