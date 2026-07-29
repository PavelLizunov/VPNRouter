using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
public class EmergencyChannelEngineTests
{
    private static EmergencyChannelConfig ValidConfig() =>
        new() { WgturnUrl = "wgturn://eyJ2IjoxfQ", VkLink = "https://vk.com/call/join/x" };

    [Fact]
    public void Initial_State_IsDisconnected()
    {
        using var engine = new EmergencyChannelEngine();
        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
        Assert.Null(engine.Pid);
        Assert.Null(engine.ActiveLabel);
    }

    [Fact]
    public void StopBeforeStart_NoOp()
    {
        using var engine = new EmergencyChannelEngine();
        engine.Stop();
        engine.Stop();
        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
    }

    [Fact]
    public async Task RestartWithoutPriorStart_Throws()
    {
        using var engine = new EmergencyChannelEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.RestartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_NullConfig_Throws()
    {
        using var engine = new EmergencyChannelEngine();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await engine.StartAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_MissingBinary_TransitionsToFailedAndRaisesError()
    {
        // Inject a manager pointed at a non-existent binary.
        var bogusPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.exe");
        var logPath = Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log");

        using var engine = new EmergencyChannelEngine(
            managerFactory: () => new EmergencyChannelManager(bogusPath, logPath));

        var transitions = new List<EmergencyChannelState>();
        var error = (string?)null;
        engine.StateChanged += s => transitions.Add(s);
        engine.ErrorOccurred += msg => error = msg;

        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await engine.StartAsync(ValidConfig(), CancellationToken.None));

        Assert.Equal(EmergencyChannelState.Failed, engine.State);
        Assert.Contains(EmergencyChannelState.Connecting, transitions);
        Assert.Contains(EmergencyChannelState.Failed, transitions);
        Assert.NotNull(error);
        Assert.Contains("wgturn-cli", error!);
    }

    [Fact]
    public async Task StartAsync_Cancelled_BeforeManagerCreated()
    {
        using var engine = new EmergencyChannelEngine();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await engine.StartAsync(ValidConfig(), cts.Token));

        // Engine must remain in Disconnected — cancellation before any
        // work happened means no state transitions.
        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
    }

    [Fact]
    public async Task CrashReconnectCycles_DisposeCrashedManagers_NoLeak()
    {
        if (CrashStub() is not { } stub) return;
        var (exe, args) = stub;

        var logPath = Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log");
        var created = new List<EmergencyChannelManager>();
        EmergencyChannelManager Factory()
        {
            var m = new EmergencyChannelManager(exe, logPath);
            m.ArgsBuilderOverride = _ => args;
            created.Add(m);
            return m;
        }

        try
        {
            using var engine = new EmergencyChannelEngine(Factory);
            using var failed = new ManualResetEventSlim(false);
            engine.StateChanged += s =>
            {
                if (s == EmergencyChannelState.Failed) failed.Set();
            };

            // Cycle 1: connect, stub exits on its own → crash.
            await engine.StartAsync(ValidConfig());
            Assert.True(failed.Wait(TimeSpan.FromSeconds(10)), "cycle 1: no crash in time");
            Assert.Null(ReadManager(engine));
            AssertDisposed(created[0], expected: true);

            // Cycle 2: reconnect → fresh manager, crash again.
            failed.Reset();
            await engine.StartAsync(ValidConfig());
            Assert.True(failed.Wait(TimeSpan.FromSeconds(10)), "cycle 2: no crash in time");
            Assert.Equal(2, created.Count);
            Assert.Null(ReadManager(engine));
            AssertDisposed(created[0], expected: true);
            AssertDisposed(created[1], expected: true);
        }
        finally
        {
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        }
    }

    [Fact]
    public void StaleCrashCallback_FromReplacedManager_IsIgnored()
    {
        var bogusPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.exe");
        var logPath = Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log");

        using var engine = new EmergencyChannelEngine();
        var errors = new List<string>();
        engine.ErrorOccurred += msg => errors.Add(msg);

        var stale = new EmergencyChannelManager(bogusPath, logPath);
        var current = new EmergencyChannelManager(bogusPath, logPath);
        // Reconnect/Stop already replaced the owner: `current` holds _manager.
        SetManager(engine, current);

        // A late Crashed callback from the replaced manager must lose the
        // exact-owner claim and be a complete no-op.
        RaiseCrashed(engine, stale, exitCode: 3);

        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
        Assert.Empty(errors);
        Assert.Same(current, ReadManager(engine));
        AssertDisposed(stale, expected: false);

        // A genuine crash of the current owner still reports + disposes.
        RaiseCrashed(engine, current, exitCode: 3);

        Assert.Equal(EmergencyChannelState.Failed, engine.State);
        Assert.Single(errors);
        Assert.Contains("exit code: 3", errors[0]);
        Assert.Null(ReadManager(engine));
        AssertDisposed(current, expected: true);
    }

    private static EmergencyChannelManager? ReadManager(EmergencyChannelEngine engine) =>
        (EmergencyChannelManager?)typeof(EmergencyChannelEngine)
            .GetField("_manager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(engine);

    private static void SetManager(EmergencyChannelEngine engine, EmergencyChannelManager? manager) =>
        typeof(EmergencyChannelEngine)
            .GetField("_manager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, manager);

    private static void RaiseCrashed(EmergencyChannelEngine engine, EmergencyChannelManager sender, int? exitCode) =>
        typeof(EmergencyChannelEngine)
            .GetMethod("OnManagerCrashed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, new object?[] { sender, exitCode });

    private static void AssertDisposed(EmergencyChannelManager manager, bool expected)
    {
        var disposed = (bool)typeof(EmergencyChannelManager)
            .GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        Assert.Equal(expected, disposed);
    }

    private static (string exe, string args)? CrashStub()
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return File.Exists(cmd) ? (cmd, "/c exit 3") : null;
        }
        return File.Exists("/bin/sh") ? ("/bin/sh", "-c \"exit 3\"") : null;
    }
}
