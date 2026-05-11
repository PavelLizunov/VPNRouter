using System.Diagnostics;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Bug-r9-E (2026-05-11) — regression coverage for the third-party VPN
/// conflict detector. Stas's logs surfaced a wintun-locked failure when
/// v2RayTun's <c>xraycore.exe</c> was running; sing-box failed adapter
/// creation with the cryptic "Cannot create a file when that file
/// already exists" and the user had no way to know which app to close.
///
/// <para>Tests use the running test process itself ("testhost") as a
/// known-running-but-not-VPN process to exercise the "no conflict"
/// branch deterministically. To exercise the "conflict found" branch
/// we spawn a sacrificial cmd.exe and temporarily rename one of the
/// allow-list names INTO the detector's input — i.e. we test the
/// pure detection logic (Process.GetProcessesByName mechanism)
/// without requiring an actual VPN client to be installed in CI.
/// We can't rename a process at runtime, so the conflict branch uses
/// a self-contained probe that injects a known process name into the
/// allow-list and runs the same detection pipeline against a real
/// running process from that name.</para>
///
/// <para>All tests are Windows-only — the detector is a no-op on
/// macOS/Linux per
/// <see cref="ConflictingVpnDetector.DetectConflictingVpnProcesses"/>
/// header. Skip via early return on non-Windows hosts so the suite
/// passes on the Mac CI workflow too.</para>
/// </summary>
public sealed class ConflictingVpnDetectorTests
{
    [Fact]
    public void DetectConflictingVpnProcesses_NoOtherVpns_ReturnsEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;

        // In a clean CI environment none of the allow-list VPN tools
        // should be installed, let alone running. If one is, the test
        // is in the wrong env — skip rather than fail.
        var anyVpnInstalled = ConflictingVpnDetector.KnownVpnProcessNames
            .Any(n => Process.GetProcessesByName(n).Length > 0);
        if (anyVpnInstalled) return;

        var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses();
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DetectConflictingVpnProcesses_KnownVpnProcessNames_Curated()
    {
        // Lock the curated list — adding/removing entries should be a
        // deliberate, reviewable change (each is correlated with a
        // wild-field report). Avoid silent drift.
        var names = ConflictingVpnDetector.KnownVpnProcessNames.ToList();

        Assert.Contains("xraycore", names);
        Assert.Contains("wireguard", names);
        Assert.Contains("openvpn", names);
        Assert.Contains("hiddify", names);
        Assert.Contains("amneziavpn", names);
        Assert.Contains("qv2ray", names);
        Assert.Contains("nekoray", names);

        // Sanity: no duplicates from a future copy-paste accident.
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void DetectConflictingVpnProcesses_OnNonWindows_ReturnsEmpty()
    {
        // The detector's first line is a Windows guard. We can't easily
        // *force* the IsWindows() check to return false, but we can pin
        // the platform-aware exit-shape: when NOT on Windows, the call
        // is contract-bound to return an empty list (no exceptions,
        // never any false-positive conflicts from cross-platform
        // process names like "openvpn" on Linux being a system service).
        if (OperatingSystem.IsWindows()) return;

        var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses();
        Assert.Empty(conflicts);
    }

    [Fact]
    public void ConflictingProcessInfo_CarriesProcessNameAndPid()
    {
        // Record shape pin — App-layer banner depends on these two
        // fields being non-null and addressable. If someone refactors
        // the record into a class that drops the PID, this catches it.
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
        // The App layer relies on the typed exception carrying the
        // detected processes so it can name them in the banner. If the
        // Conflicts property ever drops to an empty/null list, the
        // catch fallback in MainWindowViewModel would show a generic
        // "Failed to start VPN" message — defeating Bug-r9-E.
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
        // End-to-end behaviour test of the detection logic. We spawn a
        // copy of cmd.exe renamed to one of the allow-list names so
        // Process.GetProcessesByName matches it, then assert the
        // detector reports it. This is the closest we can get to the
        // wild repro (xraycore.exe running) without bundling a real
        // VPN binary into the CI environment.
        if (!OperatingSystem.IsWindows()) return;

        // Skip if any real VPN already running — would mask our spawn
        // and turn the assertion into a wrong-reason pass.
        var preExisting = ConflictingVpnDetector.KnownVpnProcessNames
            .Any(n => Process.GetProcessesByName(n).Length > 0);
        if (preExisting) return;

        // Copy cmd.exe to %TEMP%\xraycore.exe so Process.MainModule.Name
        // reports the renamed file. We use cmd /K (interactive) so the
        // process stays alive until we kill it explicitly.
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
            // /K so the process stays alive; redirect stdin so it
            // doesn't sit on a real console handle.
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

            // Give Windows a moment to register the process so
            // GetProcessesByName sees it.
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
}
