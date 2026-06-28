using System;
using System.IO;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// S1 (v2.45.0): the pure ownership decision (image path under the VPNRouter bin
/// dir) that gates both runtime "connected" detection and the takeover sing-box
/// kill, so a third-party / dev sing-box is never treated as ours.
/// </summary>
public sealed class ProcessOwnershipTests
{
    [Fact]
    public void FileDirectlyInDir_IsOwned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnr-bin");
        Assert.True(ProcessOwnership.IsUnderDirectory(Path.Combine(dir, "sing-box.exe"), dir));
    }

    [Fact]
    public void FileInSubdir_IsOwned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnr-bin");
        Assert.True(ProcessOwnership.IsUnderDirectory(Path.Combine(dir, "sub", "sing-box"), dir));
    }

    [Fact]
    public void FileOutsideDir_IsNotOwned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnr-bin");
        var thirdParty = Path.Combine(Path.GetTempPath(), "some-other-vpn", "sing-box.exe");
        Assert.False(ProcessOwnership.IsUnderDirectory(thirdParty, dir));
    }

    [Fact]
    public void SiblingDirWithSharedPrefix_IsNotOwned()
    {
        // "vpnr-bin2" must NOT count as under "vpnr-bin" — the trailing-separator
        // guard prevents a prefix-only false match.
        var dir = Path.Combine(Path.GetTempPath(), "vpnr-bin");
        var sibling = Path.Combine(Path.GetTempPath(), "vpnr-bin2", "sing-box.exe");
        Assert.False(ProcessOwnership.IsUnderDirectory(sibling, dir));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void NullOrEmpty_IsNotOwned(string? path, string? dir)
        => Assert.False(ProcessOwnership.IsUnderDirectory(path, dir));

    [Fact]
    public void CaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // Linux/macOS paths are case-sensitive
        var dir = Path.Combine(Path.GetTempPath(), "VpnR-Bin");
        var file = Path.Combine(Path.GetTempPath(), "vpnr-bin", "SING-BOX.exe");
        Assert.True(ProcessOwnership.IsUnderDirectory(file, dir));
    }
}
