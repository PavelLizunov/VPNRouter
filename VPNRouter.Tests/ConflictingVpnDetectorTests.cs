using System.Diagnostics;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ConflictingVpnDetectorTests
{
    [Fact]
    public void DetectConflictingVpnProcesses_NoOtherVpns_ReturnsEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;

        var anyVpnInstalled = ConflictingVpnDetector.KnownVpnProcessNames
            .Any(n => Process.GetProcessesByName(n).Length > 0);
        if (anyVpnInstalled) return;

        var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses();
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DetectConflictingVpnProcesses_OnNonWindows_ReturnsEmpty()
    {
        if (OperatingSystem.IsWindows()) return;

        var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses();
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DetectConflictingVpnProcesses_SpawnedFakeVpn_IsDetected()
    {
        if (!OperatingSystem.IsWindows()) return;

        var preExisting = ConflictingVpnDetector.KnownVpnProcessNames
            .Any(n => Process.GetProcessesByName(n).Length > 0);
        if (preExisting) return;

        var systemCmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (!File.Exists(systemCmd)) return;

        var temp = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-conflict-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var fakeVpn = Path.Combine(temp, "xraycore.exe");
        File.Copy(systemCmd, fakeVpn);

        Process? spawned = null;
        try
        {
            spawned = Process.Start(new ProcessStartInfo
            {
                FileName = fakeVpn,
                Arguments = "/K rem vpnrouter-test-placeholder",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            Assert.NotNull(spawned);

            for (int i = 0; i < 20; i++)
            {
                if (Process.GetProcessesByName("xraycore").Length > 0) break;
                System.Threading.Thread.Sleep(50);
            }

            var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses();

            var xrays = conflicts.Where(c => c.ProcessName == "xraycore").ToList();
            Assert.NotEmpty(xrays);
            Assert.Contains(xrays, c => c.Pid == spawned!.Id);
        }
        finally
        {
            try { spawned?.Kill(entireProcessTree: true); } catch { }
            try { spawned?.WaitForExit(2000); } catch { }
            spawned?.Dispose();
            try { File.Delete(fakeVpn); } catch { }
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DetectCoexistingVpnProcesses_SpawnedFakeWireGuard_SoftDetectedNotHardBlocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        var preExisting = ConflictingVpnDetector.KnownVpnProcessNames
            .Concat(ConflictingVpnDetector.CoexistingVpnProcessNames)
            .Any(n => Process.GetProcessesByName(n).Length > 0);
        if (preExisting) return;

        var systemCmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (!File.Exists(systemCmd)) return;

        var temp = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-coexist-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var fakeWg = Path.Combine(temp, "wireguard.exe");
        File.Copy(systemCmd, fakeWg);

        Process? spawned = null;
        try
        {
            spawned = Process.Start(new ProcessStartInfo
            {
                FileName = fakeWg,
                Arguments = "/K rem vpnrouter-test-placeholder",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            Assert.NotNull(spawned);

            for (int i = 0; i < 20; i++)
            {
                if (Process.GetProcessesByName("wireguard").Length > 0) break;
                System.Threading.Thread.Sleep(50);
            }

            var coexisting = ConflictingVpnDetector.DetectCoexistingVpnProcesses();
            Assert.Contains(coexisting, c => c.ProcessName == "wireguard" && c.Pid == spawned!.Id);

            var hard = ConflictingVpnDetector.DetectConflictingVpnProcesses();
            Assert.DoesNotContain(hard, c => c.ProcessName == "wireguard");
        }
        finally
        {
            try { spawned?.Kill(entireProcessTree: true); } catch { }
            try { spawned?.WaitForExit(2000); } catch { }
            spawned?.Dispose();
            try { File.Delete(fakeWg); } catch { }
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}
