using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// EmergencyChannelManager + Engine — lifecycle state transitions
// ═══════════════════════════════════════════════════════════════════════════════

public class EmergencyChannelManagerTests
{
    private static EmergencyChannelConfig ValidConfig() =>
        new() { WgturnUrl = "wgturn://eyJ2IjoxfQ", VkLink = "https://vk.com/call/join/x" };

    [Fact]
    public void Stop_BeforeStart_IsIdempotent()
    {
        using var manager = new EmergencyChannelManager(
            exePath: Path.Combine(Path.GetTempPath(), "nonexistent-wgturn.exe"),
            logPath: Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log"));

        // Calling Stop without Start must not throw and state stays Disconnected.
        manager.Stop();
        manager.Stop();
        Assert.Equal(EmergencyChannelState.Disconnected, manager.State);
        Assert.Null(manager.Pid);
    }

    [Fact]
    public void Start_MissingBinary_ThrowsAndStateIsFailed()
    {
        var bogusPath = Path.Combine(Path.GetTempPath(), $"nonexistent-wgturn-{Guid.NewGuid():N}.exe");
        using var manager = new EmergencyChannelManager(
            exePath: bogusPath,
            logPath: Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log"));

        Assert.Throws<FileNotFoundException>(() => manager.Start(ValidConfig()));
        Assert.Equal(EmergencyChannelState.Failed, manager.State);
    }

    [Fact]
    public void Start_NullConfig_Throws()
    {
        using var manager = new EmergencyChannelManager(
            exePath: Path.Combine(Path.GetTempPath(), "stub.exe"),
            logPath: Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log"));

        Assert.Throws<ArgumentNullException>(() => manager.Start(null!));
    }

    [Fact]
    public void Start_EmptyVkLink_Throws()
    {
        using var manager = new EmergencyChannelManager(
            exePath: Path.Combine(Path.GetTempPath(), "stub.exe"),
            logPath: Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log"));

        // VK link is required at start time even though TryParse accepts
        // empty (config persisted without one, runtime paste required).
        Assert.Throws<ArgumentException>(() =>
            manager.Start(new EmergencyChannelConfig { WgturnUrl = "wgturn://x", VkLink = "" }));
    }

    [Fact]
    public void StartStop_LifecycleStateTransitions_WindowsOnly()
    {
        // Phase-2 desktop scope is Windows; the spawn integration test
        // uses cmd.exe as a long-running stub. Other platforms skip
        // (still get the tests above for state-machine coverage).
        if (!OperatingSystem.IsWindows()) return;

        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmdPath)) return; // CI without System32 cmd — skip rather than fail

        var logPath = Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log");
        try
        {
            using var manager = new EmergencyChannelManager(cmdPath, logPath);
            // Override args so cmd.exe has something it can run for ~5s
            // without exiting (ping localhost N times).
            manager.ArgsBuilderOverride = _ => "/c ping -n 6 127.0.0.1";

            int? observedPid = null;
            int? crashExitCode = null;
            var crashed = false;
            manager.Started += pid => observedPid = pid;
            manager.Crashed += (_, code) => { crashed = true; crashExitCode = code; };

            Assert.Equal(EmergencyChannelState.Disconnected, manager.State);

            manager.Start(ValidConfig());

            // Started fires synchronously inside LaunchProcess →
            // PID is observable immediately. State should be Connected.
            Assert.Equal(EmergencyChannelState.Connected, manager.State);
            Assert.NotNull(manager.Pid);
            Assert.Equal(manager.Pid, observedPid);

            // Intentional Stop must NOT raise Crashed (the EnableRaisingEvents=false
            // pattern ensures Process.Exited callback never fires for a Stop).
            manager.Stop();
            Assert.Equal(EmergencyChannelState.Disconnected, manager.State);
            Assert.Null(manager.Pid);

            // Give threadpool a beat in case a stray Exited callback was queued.
            Thread.Sleep(300);
            Assert.False(crashed,
                $"Crashed event must not fire on intentional Stop (got exitCode={crashExitCode})");

            // wgturn-cli.log should exist and have at least the session-start marker.
            Assert.True(File.Exists(logPath), $"Expected log at {logPath}");
        }
        finally
        {
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        }
    }
}
