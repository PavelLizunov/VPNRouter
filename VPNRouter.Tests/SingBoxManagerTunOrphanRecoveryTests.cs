#nullable enable

using System;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerTunOrphanRecoveryTests
{
    private static SingBoxSettings DefaultSettings(string exePath) => new()
    {
        ExecutablePath = exePath,
        ClashApi = "127.0.0.1:9090"
    };

    private static SingBoxManager BuildManager(IProcessRunner runner, string exePath)
    {
        return new SingBoxManager(
            DefaultSettings(exePath),
            logger: null,
            http: new FakeHttpClient(),
            runner: runner);
    }

    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-tun-orphan-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    [Fact]
    public void LastCrashWasTunOrphan_FreshManager_IsFalse()
    {
        var fake = new FakeProcessRunner();
        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            Assert.False(manager.LastCrashWasTunOrphan);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void LastCrashWasTunOrphan_AfterCleanExit_IsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4001);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            fakeHandle.SignalExit(exitCode: 0);

            Assert.False(manager.LastCrashWasTunOrphan);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void LastCrashWasTunOrphan_AfterTunConflictStderr_IsTrue()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4002);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            bool crashedFlagAtEventFire = false;
            manager.Crashed += (_, _) => crashedFlagAtEventFire = manager.LastCrashWasTunOrphan;

            manager.StartWithJson("{}");

            fakeHandle.EmitError(
                "FATAL[0015] start service: start inbound/tun[tun-in]: " +
                "configure tun interface: Cannot create a file when that file already exists.");

            fakeHandle.SignalExit(exitCode: 1);

            Assert.True(manager.LastCrashWasTunOrphan,
                "Scanner should flip the flag after observing the FATAL stderr signature.");
            Assert.True(crashedFlagAtEventFire,
                "LastCrashWasTunOrphan must already be true when Crashed event fires — " +
                "HealthMonitor's AttemptRestart needs to read it in time.");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void LastCrashWasTunOrphan_AfterUnrelatedCrash_IsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4003);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            fakeHandle.EmitError(
                "FATAL[0001] start service: outbound[proxy]: " +
                "vless: dial: connection refused");

            fakeHandle.SignalExit(exitCode: 1);

            Assert.False(manager.LastCrashWasTunOrphan,
                "Scanner must NOT false-positive on unrelated sing-box errors.");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void LastCrashWasTunOrphan_ResetOnSuccessfulStart()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var handles = new System.Collections.Generic.List<FakeProcessHandle>();
        fake.OnStart(_ => true, _ =>
        {
            var h = new FakeProcessHandle(pid: 4100 + handles.Count);
            handles.Add(h);
            return h;
        });

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);

            manager.StartWithJson("{}");
            handles[0].EmitError(
                "FATAL configure tun interface: Cannot create a file when that file already exists.");
            handles[0].SignalExit(exitCode: 1);
            Assert.True(manager.LastCrashWasTunOrphan,
                "Precondition: first crash flipped the flag.");

            manager.StartWithJson("{}");
            Assert.False(manager.LastCrashWasTunOrphan,
                "StartWithJson must clear LastCrashWasTunOrphan so " +
                "the new session doesn't inherit the previous crash's state.");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void LastCrashWasTunOrphan_ResetOnStop()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4200);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");
            fakeHandle.EmitError(
                "FATAL configure tun interface: Cannot create a file when that file already exists.");
            fakeHandle.SignalExit(exitCode: 1);
            Assert.True(manager.LastCrashWasTunOrphan, "Precondition: crash flipped the flag.");

            manager.Stop();

            Assert.False(manager.LastCrashWasTunOrphan,
                "Stop() must clear LastCrashWasTunOrphan — user opt-out.");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void LastCrashWasTunOrphan_BroaderPrefixSubstring_AlsoMatches()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4300);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            fakeHandle.EmitError(
                "FATAL[0042] start service: start inbound/tun[tun-in]: " +
                "configure tun interface: some other failure mode here");

            fakeHandle.SignalExit(exitCode: 1);

            Assert.True(manager.LastCrashWasTunOrphan,
                "configure tun interface: prefix should match — catches " +
                "localised + future variants of the same root cause.");
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }
}
