using System.Diagnostics;
using System.Runtime.Versioning;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class CliStopHandleCharacterizationTests
{
    [Fact]
    public void PinnedWindowsHandle_RemainsLiveThroughControlledKillAndWait()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pingPath = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var startInfo = new ProcessStartInfo(pingPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("31");
        startInfo.ArgumentList.Add("127.0.0.1");

        using var fixture = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start controlled ping fixture.");
        try
        {
            Assert.False(fixture.WaitForExit(500));
            var expected = Snapshot(fixture);

            using var fresh = Process.GetProcessById(fixture.Id);
            var pinnedHandle = fresh.SafeHandle;
            Assert.False(pinnedHandle.IsInvalid);
            Assert.False(pinnedHandle.IsClosed);
            Assert.True(ProcessOwnership.IsSameProcessIdentity(expected, Snapshot(fresh)));

            fresh.Kill(entireProcessTree: true);
            Assert.True(fresh.WaitForExit(5000));
            Assert.False(pinnedHandle.IsClosed);
            GC.KeepAlive(pinnedHandle);
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.Kill(entireProcessTree: true);
                fixture.WaitForExit(5000);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static OwnedProcessIdentity Snapshot(Process process) => new(
        process.Id,
        process.StartTime.ToUniversalTime().Ticks,
        ProcessImagePath.TryGetByPid(process.Id)
            ?? throw new InvalidOperationException("Could not read fixture image path."),
        ParentPid: null);
}
