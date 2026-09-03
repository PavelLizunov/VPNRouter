using System;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class UnixOwnedProcessSignalTests
{
    [Fact]
    public void LinuxHelperArguments_PreserveExactIdentityAsSeparateTokens()
    {
        var target = new OwnedProcessIdentity(4321, 638923456789012345, "/tmp/owned sing-box");

        var pkexec = SingBoxManager.BuildLinuxOwnedSignalHelperArguments(
            "/opt/vpnrouter/VPNRouter.App", target, signal: 9);
        var sudo = SingBoxManager.BuildLinuxOwnedSignalHelperArguments(
            "/opt/vpnrouter/VPNRouter.App", target, signal: 9, nonInteractiveSudo: true);

        Assert.Equal(new[]
        {
            "/opt/vpnrouter/VPNRouter.App",
            UnixOwnedProcessSignal.HelperFlag,
            "4321",
            "638923456789012345",
            "/tmp/owned sing-box",
            "9"
        }, pkexec);
        Assert.Equal("-n", sudo[0]);
        Assert.Equal(pkexec, sudo.Skip(1));
        Assert.DoesNotContain("-f", pkexec);
    }

    [Fact]
    public void MacKillArguments_TargetOnlyOnePositivePid()
    {
        Assert.Equal(
            new[] { "-n", "/bin/kill", "-KILL", "--", "4321" },
            SingBoxManager.BuildMacExactKillArguments(4321));
    }

    [Fact]
    public void InternalHelper_RejectsMalformedIdentityWithoutSignaling()
    {
        var handled = UnixOwnedProcessSignal.TryHandleHelper(
            new[] { UnixOwnedProcessSignal.HelperFlag, "0", "bad", "", "99" },
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(64, exitCode);
    }

    [Fact]
    public void InternalHelper_IgnoresOrdinaryApplicationArguments()
    {
        Assert.False(UnixOwnedProcessSignal.TryHandleHelper(Array.Empty<string>(), out var emptyExitCode));
        Assert.Equal(0, emptyExitCode);
        Assert.False(UnixOwnedProcessSignal.TryHandleHelper(new[] { "--minimized" }, out var exitCode));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void LinuxUpdateHelperCapability_RejectsMissingAndLegacyHelpers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vpnrouter-helper-{Guid.NewGuid():N}");
        try
        {
            Assert.False(UpdateChecker.HelperSupportsExactOwnedSignal(path));
            File.WriteAllText(path, "#!/bin/sh\n# legacy helper\n");
            Assert.False(UpdateChecker.HelperSupportsExactOwnedSignal(path));
            File.AppendAllText(path, "# --owned-signal-v1\n");
            Assert.True(UpdateChecker.HelperSupportsExactOwnedSignal(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void LinuxPidFdSignal_RejectsWrongIdentityAndPreservesChild()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux pidfd behavior only");

        var (child, exePath) = StartControlledChild();
        try
        {
            var identity = ProcessOwnership.TryReadOwnedSingBoxIdentity(child);
            Assert.NotNull(identity);
            var wrongStart = identity.Value with
            {
                StartedAtUtcTicks = identity.Value.StartedAtUtcTicks + 1
            };
            var wrongPath = identity.Value with
            {
                ExecutablePath = identity.Value.ExecutablePath + ".not-owned"
            };

            Assert.Equal(
                UnixOwnedSignalResult.IdentityMismatch,
                UnixOwnedProcessSignal.SignalLinux(wrongStart, signal: 15));
            Assert.False(child.HasExited);
            Assert.Equal(
                UnixOwnedSignalResult.IdentityMismatch,
                UnixOwnedProcessSignal.SignalLinux(wrongPath, signal: 15));
            Assert.False(child.HasExited);
            Assert.Equal(
                UnixOwnedSignalResult.Unsupported,
                UnixOwnedProcessSignal.SignalLinux(identity.Value, signal: 1));
            Assert.False(child.HasExited);
        }
        finally
        {
            StopControlledChild(child, exePath);
        }
    }

    [Fact]
    public void LinuxPidFdSignal_MissingTargetIsNoOp()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux pidfd behavior only");

        var result = UnixOwnedProcessSignal.SignalLinux(
            new OwnedProcessIdentity(int.MaxValue, 1, "/nonexistent/sing-box"),
            signal: 15);

        Assert.Equal(UnixOwnedSignalResult.TargetGone, result);
    }

    [Fact]
    public void LinuxPidFdSignal_SignalsOnlyControlledMatchingChild()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux pidfd behavior only");

        var (child, exePath) = StartControlledChild();
        try
        {
            var identity = ProcessOwnership.TryReadOwnedSingBoxIdentity(child);
            Assert.NotNull(identity);

            var result = UnixOwnedProcessSignal.SignalLinux(identity.Value, signal: 15);

            Assert.Equal(UnixOwnedSignalResult.Signaled, result);
            Assert.True(child.WaitForExit(5000));
        }
        finally
        {
            StopControlledChild(child, exePath);
        }
    }

    private static (Process child, string exePath) StartControlledChild()
    {
        var binDir = AppPaths.BinDir;
        Directory.CreateDirectory(binDir);
        var testExe = Path.Combine(binDir, $"test-sleep-{Guid.NewGuid():N}");
        File.Copy("/bin/sleep", testExe, overwrite: true);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(testExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var child = Process.Start(new ProcessStartInfo(testExe, "30")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (child == null)
        {
            try { File.Delete(testExe); } catch { }
            throw new InvalidOperationException("Could not start controlled sleep child.");
        }
        return (child, testExe);
    }

    private static void StopControlledChild(Process child, string? exePath = null)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
        }
        catch
        {
            // Bounded cleanup of this test's own child only.
        }
        finally
        {
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try { File.Delete(exePath); } catch { }
            }
        }
    }
}
