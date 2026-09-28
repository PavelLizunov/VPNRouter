#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HealthMonitorTunOrphanRestartTests
{
    private sealed class StubProcessScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public void CreateBlockRules(System.Collections.Generic.IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static SingBoxManager BuildSingBox(IProcessRunner runner, string exePath)
    {
        var settings = new SingBoxSettings
        {
            ExecutablePath = exePath,
            ClashApi = "127.0.0.1:9090"
        };
        return new SingBoxManager(settings, logger: null,
            http: new FakeHttpClient(), runner: runner);
    }

    private static HealthMonitor BuildHm(SingBoxManager sb)
    {
        var scanner = new StubProcessScanner();
        var fw = new StubFirewallManager();
        var monSettings = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        return new HealthMonitor(sb, scanner, fw, monSettings);
    }

    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-hm-tun-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    private static bool InvokeRunTunOrphanRecoveryCleanup(
        HealthMonitor hm, System.Threading.CancellationToken ct)
    {
        var m = typeof(HealthMonitor).GetMethod(
            "RunTunOrphanRecoveryCleanup",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (bool)m.Invoke(hm, new object[] { ct })!;
    }

    [Fact]
    public void AttemptRestart_TunOrphanFlag_TriggersNetshDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sbFake = new FakeProcessRunner();
        var sbHandle = new FakeProcessHandle(pid: 5001);
        sbFake.OnStart(_ => true, _ => sbHandle);

        var exe = CreateStubExe();
        var previousTunRunner = TunAdapterDiagnostics.Runner;
        var tunFake = new FakeProcessRunner();
        try
        {
            using var sb = BuildSingBox(sbFake, exe);
            sb.StartWithJson("{}");

            sbHandle.EmitError(
                "FATAL configure tun interface: Cannot create a file when that file already exists.");
            sbHandle.SignalExit(exitCode: 1);
            Assert.True(sb.LastCrashWasTunOrphan,
                "Precondition: SingBoxManager flagged the previous crash as TUN orphan.");

            TunAdapterDiagnostics.Runner = tunFake;
            tunFake.OnRun(
                r => r.ExecutablePath == "netsh"
                  && r.Arguments.Count >= 2
                  && r.Arguments[0] == "interface"
                  && r.Arguments[1] == "set",
                new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));

            using var hm = BuildHm(sb);
            var result = InvokeRunTunOrphanRecoveryCleanup(
                hm, System.Threading.CancellationToken.None);

            Assert.True(result,
                "Cleanup should report true when caller cancellation didn't fire.");

            var netshCalls = tunFake.RunCalls
                .Where(r => r.ExecutablePath == "netsh"
                            && r.Arguments.Count >= 2
                            && r.Arguments[0] == "interface"
                            && r.Arguments[1] == "set")
                .ToList();
            Assert.NotEmpty(netshCalls);
            var call = netshCalls[0];
            Assert.Contains("interface", call.Arguments);
            Assert.Contains("set", call.Arguments);
            Assert.Contains("name=VPNRouter-TUN", call.Arguments);
            Assert.Contains("admin=disabled", call.Arguments);
        }
        finally
        {
            TunAdapterDiagnostics.Runner = previousTunRunner;
            try { File.Delete(exe); } catch {  }
        }
    }

    [Fact]
    public void AttemptRestart_NoTunOrphanFlag_SkipsNetshDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sbFake = new FakeProcessRunner();
        var sbHandle = new FakeProcessHandle(pid: 5002);
        sbFake.OnStart(_ => true, _ => sbHandle);

        var exe = CreateStubExe();
        var previousTunRunner = TunAdapterDiagnostics.Runner;
        var tunFake = new FakeProcessRunner();
        try
        {
            using var sb = BuildSingBox(sbFake, exe);
            sb.StartWithJson("{}");

            sbHandle.EmitError("FATAL outbound[proxy]: vless dial: connection refused");
            sbHandle.SignalExit(exitCode: 1);
            Assert.False(sb.LastCrashWasTunOrphan,
                "Precondition: unrelated crash leaves the flag false.");

            TunAdapterDiagnostics.Runner = tunFake;
            using var hm = BuildHm(sb);

            var result = InvokeRunTunOrphanRecoveryCleanup(
                hm, System.Threading.CancellationToken.None);
            Assert.True(result,
                "Cleanup returns true when there's nothing to do.");

            var netshDisableCalls = tunFake.RunCalls
                .Where(r => r.ExecutablePath == "netsh"
                            && r.Arguments.Contains("admin=disabled"))
                .ToList();
            Assert.Empty(netshDisableCalls);
        }
        finally
        {
            TunAdapterDiagnostics.Runner = previousTunRunner;
            try { File.Delete(exe); } catch {  }
        }
    }
}
