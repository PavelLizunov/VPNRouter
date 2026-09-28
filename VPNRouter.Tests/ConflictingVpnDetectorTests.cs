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
    public void DetectConflictingVpnProcesses_KnownVpnProcessNames_Curated()
    {
        var names = ConflictingVpnDetector.KnownVpnProcessNames.ToList();

        Assert.Contains("xraycore", names);
        Assert.Contains("openvpn", names);
        Assert.Contains("hiddify", names);
        Assert.Contains("qv2ray", names);
        Assert.Contains("nekoray", names);

        Assert.DoesNotContain("wireguard", names);
        Assert.DoesNotContain("amneziavpn", names);

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void CoexistingVpnProcessNames_AreSeparateAdapterClients_Curated()
    {
        var coexisting = ConflictingVpnDetector.CoexistingVpnProcessNames.ToList();

        Assert.Contains("wireguard", coexisting);
        Assert.Contains("amneziavpn", coexisting);

        Assert.DoesNotContain("xraycore", coexisting);
        Assert.DoesNotContain("hiddify", coexisting);

        Assert.Equal(coexisting.Count, coexisting.Distinct().Count());
    }

    [Fact]
    public void HardConflictAndCoexistingLists_AreDisjoint()
    {
        var hard = ConflictingVpnDetector.KnownVpnProcessNames;
        var soft = ConflictingVpnDetector.CoexistingVpnProcessNames;

        Assert.Empty(hard.Intersect(soft, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectConflictingVpnProcesses_OnNonWindows_ReturnsEmpty()
    {
        if (OperatingSystem.IsWindows()) return;

        var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses();
        Assert.Empty(conflicts);
    }

    [Fact]
    public void ConflictingProcessInfo_CarriesProcessNameAndPid()
    {
        var info = new ConflictingVpnDetector.ConflictingProcessInfo(
            ProcessName: "xraycore",
            Pid: 1234,
            FullPath: @"C:\v2RayTun\xraycore.exe");

        Assert.Equal("xraycore", info.ProcessName);
        Assert.Equal(1234, info.Pid);
        Assert.Equal(@"C:\v2RayTun\xraycore.exe", info.FullPath);
    }

    [Fact]
    public void ConflictingVpnException_PreservesConflictsList()
    {
        var first = new ConflictingVpnDetector.ConflictingProcessInfo(
            "xraycore", 1234, @"C:\xraycore.exe");
        var second = new ConflictingVpnDetector.ConflictingProcessInfo(
            "wireguard", 5678, @"C:\Program Files\WireGuard\wireguard.exe");
        var conflicts = new[] { first, second };

        var ex = new ConflictingVpnException(conflicts, "another VPN is running");

        Assert.Equal(2, ex.Conflicts.Count);
        Assert.Equal("xraycore", ex.Conflicts[0].ProcessName);
        Assert.Equal("wireguard", ex.Conflicts[1].ProcessName);
        Assert.Equal("another VPN is running", ex.Message);
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
