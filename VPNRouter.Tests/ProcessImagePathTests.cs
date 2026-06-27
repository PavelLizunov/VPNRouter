using System;
using System.Diagnostics;
using System.IO;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="ProcessImagePath"/> — the QueryFullProcessImageName-based
/// resolver that replaced <c>Process.MainModule.FileName</c> in
/// <see cref="FirewallManager"/> after the latter was found to return null for
/// every routed process when VPNRouter runs in session 0 (Windows Service /
/// SYSTEM autostart), silently turning the <c>block_on_vpn_fail</c> kill-switch
/// into a fail-OPEN. See the type remarks on <see cref="ProcessImagePath"/>.
///
/// <para>Windows-only: the API and the defect are Windows-specific, so these
/// skip on Linux/macOS CI (matching the FirewallManager netsh tests). The
/// cross-session property itself (session-0 reader → user-session target) is
/// proven by the live windows-brat gate, not unit tests — here we pin the
/// in-process correctness + null-safety contract.</para>
/// </summary>
public class ProcessImagePathTests
{
    [Fact]
    public void TryGetByPid_CurrentProcess_MatchesMainModulePath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "QueryFullProcessImageName is Windows-only");

        using var self = Process.GetCurrentProcess();
        // In-process, MainModule always succeeds (no cross-session boundary),
        // so it is the ground-truth the native resolver must agree with.
        var expected = self.MainModule!.FileName;

        var actual = ProcessImagePath.TryGetByPid(self.Id);

        Assert.False(string.IsNullOrEmpty(actual));
        Assert.True(File.Exists(actual));
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual!), ignoreCase: true);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0x7FFFFFF0)] // a PID that will not exist
    public void TryGetByPid_InvalidPid_ReturnsNull(int pid)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");
        Assert.Null(ProcessImagePath.TryGetByPid(pid));
    }

    [Fact]
    public void ResolveRunningPath_CurrentProcessByName_ResolvesToExistingFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var self = Process.GetCurrentProcess();
        var name = self.ProcessName + ".exe"; // e.g. "testhost.exe"

        var path = ProcessImagePath.ResolveRunningPath(name);

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
        Assert.Equal(self.ProcessName, Path.GetFileNameWithoutExtension(path), ignoreCase: true);
    }

    [Fact]
    public void ResolveRunningPath_WithoutExeSuffix_AlsoResolves()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var self = Process.GetCurrentProcess();
        // The kill-switch passes names with .exe, but the resolver must also
        // accept a bare base name (only a trailing ".exe" is stripped — NOT via
        // Path.GetFileNameWithoutExtension, which would mangle dotted names).
        var path = ProcessImagePath.ResolveRunningPath(self.ProcessName);

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ResolveRunningPath_DottedProcessName_StripsOnlyExeSuffix()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var self = Process.GetCurrentProcess();
        // Only exercises the hazard if the test host name actually contains an
        // internal dot — it does ("VPNRouter.Tests"). Guard so the test stays
        // honest if the host name ever changes.
        Assert.SkipUnless(self.ProcessName.Contains('.'),
            $"test host '{self.ProcessName}' has no internal dot to exercise");

        // "<Name>.exe" must strip ONLY the trailing .exe — Path.GetFileNameWithout-
        // Extension would truncate "VPNRouter.Tests" to "VPNRouter" and the
        // process would never be found (the bug this resolver deliberately avoids).
        var path = ProcessImagePath.ResolveRunningPath(self.ProcessName + ".exe");

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
        Assert.Equal(self.ProcessName, Path.GetFileNameWithoutExtension(path), ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRunningPath_NullOrWhitespace_ReturnsNull(string? name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");
        Assert.Null(ProcessImagePath.ResolveRunningPath(name));
    }

    [Fact]
    public void ResolveRunningPath_NonexistentProcess_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");
        Assert.Null(ProcessImagePath.ResolveRunningPath(
            "VPNRouter_definitely_not_a_real_process_zzq.exe"));
    }
}
