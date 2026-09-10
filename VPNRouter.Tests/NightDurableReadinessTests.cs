#nullable enable

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Serilog;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Behavioral and source-guard tests for durable typed readiness event subscription (NIGHT-07 survivor fix):
/// - Permanent `_engine.Connected += OnEngineConnected` in MainWindowViewModel constructor.
/// - Unsubscription in MainWindowViewModel.Dispose alongside StatusChanged.
/// - Legacy Connected strings never promote IsConnected from false.
/// - Typed Connected event with matching running PID flips IsConnected to true.
/// - Queue-drain safety: Stop cancellation, manager replacement, generation change, or VM disposal before drain preserves false.
/// - Mismatched PID remains false.
/// - Active coordinator (IsConnecting or _isReconnecting) is never bypassed by permanent handler.
/// - StartupHost guards against stale host or reused PID.
/// </summary>
public sealed class NightDurableReadinessTests
{
    private static void SetField(object target, string name, object? value)
    {
        var type = target.GetType();
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? (name.StartsWith('_') && name.Length > 1
                ? type.GetProperty($"{char.ToUpperInvariant(name[1])}{name[2..]}", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                : null);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? (name.StartsWith('_') && name.Length > 1
                ? type.GetField($"<{char.ToUpperInvariant(name[1])}{name[2..]}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                : null);
        if (field is null && prop is null)
            throw new InvalidOperationException($"Field '{name}' not found on {type.FullName}.");
        field?.SetValue(target, value);
    }

    private static T? GetField<T>(object target, string name)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is null)
            throw new InvalidOperationException($"Field '{name}' not found on {type.FullName}.");
        return (T?)field.GetValue(target);
    }

    private static FakeHttpClient GetFakeHttp(SingBoxManager manager)
    {
        var field = typeof(SingBoxManager).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        return (FakeHttpClient)field!.GetValue(manager)!;
    }

    private static async Task DrainUiQueueAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { /* drain queued UI posts */ }, DispatcherPriority.Background);
    }

    private static (VpnEngine engine, SingBoxManager manager, FakeProcessHandle handle, CancellationTokenSource sessionCts) CreateFakeEngine(int pid = 12345)
    {
        var engine = (VpnEngine)RuntimeHelpers.GetUninitializedObject(typeof(VpnEngine));

        var fakeRunner = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid);
        fakeRunner.OnStart(_ => true, _ => fakeHandle);

        var fakeHttp = new FakeHttpClient();

        var singBoxSettings = new SingBoxSettings { ClashApi = "127.0.0.1:9090" };
        var manager = new SingBoxManager(singBoxSettings, logger: null, http: fakeHttp, runner: fakeRunner);
        SetField(manager, "_handle", fakeHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager, SingBoxState.Running);
        SetField(manager, "_state", SingBoxState.Running);
        SetField(manager, "State", SingBoxState.Running);

        var sessionCts = new CancellationTokenSource();

        SetField(engine, "_singBox", manager);
        SetField(engine, "_sessionCts", sessionCts);
        SetField(engine, "_failoverGeneration", 1L);
        SetField(engine, "_warmupConfirmed", true);
        SetField(engine, "_disposed", false);
        SetField(engine, "ActiveServerAddress", string.Empty);

        return (engine, manager, fakeHandle, sessionCts);
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
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildSeamEngine() =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: new NullWindowsDnsHardening(),
            splitDriver: new FakeSplitTunnelDriver());
#pragma warning restore CS0618

    private static MainWindowViewModel CreateIsolatedVm(VpnEngine engine)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = string.Empty,
                RoutingMode = "split"
            },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig()
        };

        SetField(vm, "_settings", settings);
        SetField(vm, "_engine", engine);
        SetField(vm, "_logger", Log.Logger);
        SetField(vm, "_isConnected", false);
        SetField(vm, "_isConnecting", false);
        SetField(vm, "_isReconnecting", false);
        SetField(vm, "_disposed", false);
        SetField(vm, "_connectButtonText", Strings.StartVPN);
        SetField(vm, "_statusText", Strings.NotConnected);
        SetField(vm, "_connectionStatsText", string.Empty);
        SetField(vm, "SubscriptionServers", new ObservableCollection<ServerViewModel>());
        SetField(vm, "Servers", new ObservableCollection<ServerViewModel>());
        SetField(vm, "CustomConfigs", new ObservableCollection<CustomConfigViewModel>());
        SetField(vm, "Subscriptions", new ObservableCollection<SubscriptionViewModel>());

        return vm;
    }

    private static void WireConnectedHandler(MainWindowViewModel vm, VpnEngine engine)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "OnEngineConnected",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var handler = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), vm, method!);
        engine.Connected += handler;
    }

    private static void InvokeEngineStatus(MainWindowViewModel vm, string status)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, new object[] { status });
    }

    private static void EmitConnectedEvent(VpnEngine engine, int pid)
    {
        var eventField = typeof(VpnEngine).GetField("Connected", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(eventField);
        var handler = (Action<int>?)eventField!.GetValue(engine);
        handler?.Invoke(pid);
    }

    [AvaloniaFact]
    public async Task Stopped_LegacyStrings_CannotSetIsConnected_TypedCurrentConnected_SetsIsConnectedTrue()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        Assert.False(vm.IsConnected);
        Assert.Equal(Strings.StartVPN, vm.ConnectButtonText);

        // 1. Legacy status strings must NEVER promote IsConnected from false
        InvokeEngineStatus(vm, "Connected (PID 12345)");
        await DrainUiQueueAsync();
        Assert.False(vm.IsConnected);

        InvokeEngineStatus(vm, "VPN Router is running (PID 12345)");
        await DrainUiQueueAsync();
        Assert.False(vm.IsConnected);

        // 2. Typed Connected event flips IsConnected to true and updates UI
        EmitConnectedEvent(engine, 12345);
        await DrainUiQueueAsync();

        Assert.True(vm.IsConnected);
        Assert.Equal(Strings.StopVPN, vm.ConnectButtonText);
        Assert.Empty(GetFakeHttp(manager).SentRequests);
    }

    [AvaloniaFact]
    public async Task QueuedTyped_StopCancellationBeforeDrain_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        // Emit Connected event (posts to UI dispatcher queue)
        EmitConnectedEvent(engine, 12345);

        // Cancel the session CTS before UI queue drains
        sessionCts.Cancel();

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task QueuedTyped_ManagerReplacementBeforeDrain_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        // Emit Connected event (posts to UI dispatcher queue)
        EmitConnectedEvent(engine, 12345);

        // Replace _singBox reference before UI queue drains
        var fakeRunner2 = new FakeProcessRunner();
        var fakeHandle2 = new FakeProcessHandle(54321);
        fakeRunner2.OnStart(_ => true, _ => fakeHandle2);
        var manager2 = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:9090" }, runner: fakeRunner2);
        SetField(engine, "_singBox", manager2);

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task QueuedTyped_DisposedVmBeforeDrain_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        // Emit Connected event (posts to UI dispatcher queue)
        EmitConnectedEvent(engine, 12345);

        // Dispose VM before drain (simulate dispose flag without calling native real VM Dispose)
        SetField(vm, "_disposed", true);

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task EventForWrongPid_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        // Emit Connected for a mismatched PID
        EmitConnectedEvent(engine, 99999);

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task IsConnecting_True_PermanentHandlerDoesNotBypassCoordinator()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        SetField(vm, "_isConnecting", true);
        WireConnectedHandler(vm, engine);

        EmitConnectedEvent(engine, 12345);

        await DrainUiQueueAsync();

        // Must stay false — explicit coordinator owns budget and state transition
        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task IsReconnecting_True_PermanentHandlerDoesNotBypassCoordinator()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        SetField(vm, "_isReconnecting", true);
        WireConnectedHandler(vm, engine);

        EmitConnectedEvent(engine, 12345);

        await DrainUiQueueAsync();

        // Must stay false — reconnect coordinator owns budget and state transition
        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task QueuedTyped_GenerationChangedBeforeDrain_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        EmitConnectedEvent(engine, 12345);

        // Increment failover generation before drain
        SetField(engine, "_failoverGeneration", 2L);

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task QueuedTyped_WarmupNotConfirmedBeforeDrain_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        EmitConnectedEvent(engine, 12345);

        // Invalidate warmup confirmation before drain
        SetField(engine, "_warmupConfirmed", false);

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [Fact]
    public void StartupHost_OnConnected_GuardsDelayedHostAndReusedPid()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);

        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic);
        Assert.NotNull(hostType);
        var host = Activator.CreateInstance(
            hostType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { engine },
            culture: null)!;
        var setSbMethod = hostType!.GetMethod("SetSingBoxManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        setSbMethod?.Invoke(host, new object[] { manager });
        var onStartedMethod = hostType.GetMethod("OnSingBoxStarted", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(int) }, null);
        onStartedMethod?.Invoke(host, new object[] { 12345 });
        var onConnectedMethod = hostType.GetMethod("OnConnected", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(int) }, null);
        Assert.NotNull(onConnectedMethod);

        var fired = false;
        engine.Connected += _ => fired = true;

        // Invalidate generation on engine (simulating next host / failover)
        SetField(engine, "_failoverGeneration", 99L);
        SetField(engine, "_warmupConfirmed", false);

        onConnectedMethod!.Invoke(host, new object[] { 12345 });

        Assert.False(fired, "Connected event must not fire from a stale host whose generation differs.");
        Assert.False(GetField<bool>(engine, "_warmupConfirmed"), "Warmup must not be confirmed by a stale host.");
    }

    [Fact]
    public void StartupHost_OnConnected_StaleHost_SamePidDifferentManager_Suppressed()
    {
        var (engine, manager1, handle1, sessionCts) = CreateFakeEngine(12345);

        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic);
        Assert.NotNull(hostType);
        var host = Activator.CreateInstance(
            hostType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { engine },
            culture: null)!;
        var setSbMethod = hostType!.GetMethod("SetSingBoxManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        setSbMethod?.Invoke(host, new object[] { manager1 });
        var onStartedMethod = hostType.GetMethod("OnSingBoxStarted", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(int) }, null);
        onStartedMethod?.Invoke(host, new object[] { 12345 });
        var onConnectedMethod = hostType.GetMethod("OnConnected", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(int) }, null);
        Assert.NotNull(onConnectedMethod);

        // Replace manager on engine with a different manager instance that has the same PID (12345)
        var fakeRunner2 = new FakeProcessRunner();
        var fakeHandle2 = new FakeProcessHandle(12345);
        fakeRunner2.OnStart(_ => true, _ => fakeHandle2);

        var fakeHttp2 = new FakeHttpClient();

        var manager2 = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:9090" }, logger: null, http: fakeHttp2, runner: fakeRunner2);
        SetField(manager2, "_handle", fakeHandle2);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager2, SingBoxState.Running);
        SetField(manager2, "_state", SingBoxState.Running);
        SetField(manager2, "State", SingBoxState.Running);

        SetField(engine, "_singBox", manager2);
        SetField(engine, "_warmupConfirmed", false);

        var fired = false;
        engine.Connected += _ => fired = true;

        onConnectedMethod!.Invoke(host, new object[] { 12345 });

        Assert.False(fired, "Connected event must not fire when host manager differs from current engine manager despite same PID.");
        Assert.False(GetField<bool>(engine, "_warmupConfirmed"), "Warmup must remain false when host manager is stale.");
    }

    [Fact]
    public void CaptureReadinessGuard_SameManagerSamePid_HandleReplacement_RejectsGuard()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var guard = engine.CaptureReadinessGuard(12345);
        Assert.True(guard());
        Assert.Empty(GetFakeHttp(manager).SentRequests);

        // Replace handle on the SAME manager with a new handle having the SAME PID
        var replacementHandle = new FakeProcessHandle(12345);
        SetField(manager, "_handle", replacementHandle);

        Assert.False(guard(), "CaptureReadinessGuard must reject when handle identity changes on the same manager with the same PID.");
        Assert.Empty(GetFakeHttp(manager).SentRequests);
    }

    [AvaloniaFact]
    public async Task QueuedTyped_SameManagerSamePid_HandleReplacementBeforeDrain_StaysFalse()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var vm = CreateIsolatedVm(engine);
        WireConnectedHandler(vm, engine);

        EmitConnectedEvent(engine, 12345);

        // Replace handle on the SAME manager with a new handle having the SAME PID before UI queue drains
        var replacementHandle = new FakeProcessHandle(12345);
        SetField(manager, "_handle", replacementHandle);

        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
    }

    [Fact]
    public void StartupHost_OnConnected_StaleHost_SameManagerSamePid_HandleReplaced_Suppressed()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);

        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic);
        Assert.NotNull(hostType);
        var host = Activator.CreateInstance(
            hostType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { engine },
            culture: null)!;
        var setSbMethod = hostType!.GetMethod("SetSingBoxManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        setSbMethod?.Invoke(host, new object[] { manager });
        var onStartedMethod = hostType.GetMethod("OnSingBoxStarted", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(int) }, null);
        onStartedMethod?.Invoke(host, new object[] { 12345 });
        var onConnectedMethod = hostType.GetMethod("OnConnected", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(int) }, null);
        Assert.NotNull(onConnectedMethod);

        // Replace handle on the SAME manager with a new handle having the SAME PID (12345)
        var replacementHandle = new FakeProcessHandle(12345);
        SetField(manager, "_handle", replacementHandle);

        SetField(engine, "_warmupConfirmed", false);
        var fired = false;
        engine.Connected += _ => fired = true;

        onConnectedMethod!.Invoke(host, new object[] { 12345 });

        Assert.False(fired, "Connected event must not fire when manager handle was replaced despite same manager and same PID.");
        Assert.False(GetField<bool>(engine, "_warmupConfirmed"), "Warmup must remain false when host callback is stale due to handle replacement.");
    }

    [Fact]
    public void Subscription_And_Unsubscription_SourceGuard()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? source = null;
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
            if (File.Exists(candidate))
            {
                source = File.ReadAllText(candidate);
                break;
            }
        }
        Assert.NotNull(source);

        // 1. Constructor subscription alongside StatusChanged
        var statusSubIdx = source!.IndexOf("_engine.StatusChanged += OnEngineStatus;", StringComparison.Ordinal);
        Assert.True(statusSubIdx >= 0, "Constructor must wire StatusChanged += OnEngineStatus");

        var connectedSubIdx = source.IndexOf("_engine.Connected += OnEngineConnected;", StringComparison.Ordinal);
        Assert.True(connectedSubIdx >= 0, "Constructor must wire Connected += OnEngineConnected");

        Assert.True(Math.Abs(connectedSubIdx - statusSubIdx) < 200,
            "Connected subscription must be placed directly alongside StatusChanged in the constructor");

        // 2. Dispose unsubscription alongside StatusChanged
        var statusUnsubIdx = source.IndexOf("_engine.StatusChanged -= OnEngineStatus;", StringComparison.Ordinal);
        Assert.True(statusUnsubIdx >= 0, "Dispose must unhook StatusChanged -= OnEngineStatus");

        var connectedUnsubIdx = source.IndexOf("_engine.Connected -= OnEngineConnected;", StringComparison.Ordinal);
        Assert.True(connectedUnsubIdx >= 0, "Dispose must unhook Connected -= OnEngineConnected");

        Assert.True(Math.Abs(connectedUnsubIdx - statusUnsubIdx) < 200,
            "Connected unsubscription must be placed directly alongside StatusChanged in Dispose");
    }

    [Fact]
    public void CaptureReadinessGuard_FailStop_ActualEngineStop_UsesFakeSeams_RejectsGuardWithoutNetworking()
    {
        using var engine = BuildSeamEngine();

        var fakeRunner = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(12345);
        fakeRunner.OnStart(_ => true, _ => fakeHandle);

        var fakeHttp = new FakeHttpClient();
        var manager = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:9090" }, logger: null, http: fakeHttp, runner: fakeRunner);
        SetField(manager, "_handle", fakeHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager, SingBoxState.Running);
        SetField(manager, "State", SingBoxState.Running);

        using var sessionCts = new CancellationTokenSource();
        SetField(engine, "_singBox", manager);
        SetField(engine, "_sessionCts", sessionCts);
        SetField(engine, "_failoverGeneration", 1L);
        SetField(engine, "_warmupConfirmed", true);

        var guard = engine.CaptureReadinessGuard(12345);
        Assert.True(guard());
        Assert.Empty(fakeHttp.SentRequests);

        // Fail-stop: invoke actual engine.Stop() which executes TeardownInternal using fake/seams
        engine.Stop();

        Assert.False(guard(), "CaptureReadinessGuard must reject after actual engine.Stop().");
        Assert.Empty(fakeHttp.SentRequests);

        SetField(engine, "_singBox", null);
    }

    [Fact]
    public void CaptureReadinessGuard_FailStop_HandleExitedOrStateNotRunning_RejectsWithoutNetworking()
    {
        var (engine, manager, handle, sessionCts) = CreateFakeEngine(12345);
        var guard = engine.CaptureReadinessGuard(12345);
        Assert.True(guard());
        var fakeHttp = GetFakeHttp(manager);
        Assert.Empty(fakeHttp.SentRequests);

        // 1. Handle exited (killed or process exited)
        handle.Kill();
        Assert.True(handle.HasExited);
        Assert.False(guard(), "CaptureReadinessGuard must reject when handle has exited.");
        Assert.Empty(fakeHttp.SentRequests);

        // 2. Manager state is not Running
        var (engine2, manager2, handle2, sessionCts2) = CreateFakeEngine(23456);
        var guard2 = engine2.CaptureReadinessGuard(23456);
        Assert.True(guard2());
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(manager2, SingBoxState.Stopped);
        SetField(manager2, "State", SingBoxState.Stopped);
        Assert.False(guard2(), "CaptureReadinessGuard must reject when manager state is not Running.");
        Assert.Empty(GetFakeHttp(manager2).SentRequests);
    }
}
