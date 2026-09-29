using System.Reflection;
using System.Runtime.InteropServices;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class WindowsPnpDeviceManagerTests
{
    [Fact]
    public void NativeLookup_RejectsNamesOutsideOwnedWhitelist()
    {
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            "VPNRouter-TUN' OR 1=1");

        Assert.False(result.Success);
        Assert.Empty(result.InstanceIds);
    }

    [Fact]
    public void NativeLookup_MatchingConnectionWithoutPnpIdFailsClosed()
    {
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            "VPNRouter-TUN",
            () => new[]
            {
                new NativeNetworkConnectionRecord(
                    "{55555555-5555-5555-5555-555555555555}", "VPNRouter-TUN", null),
            });

        Assert.False(result.Success);
        Assert.Empty(result.InstanceIds);
        Assert.Contains("PnpInstanceID", result.Error);
    }

    [Fact]
    public void NativeLookup_ForeignPnpMappingFailsClosed()
    {
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            "VPNRouter-TUN",
            () => new[]
            {
                new NativeNetworkConnectionRecord(
                    "{77777777-7777-7777-7777-777777777777}",
                    "VPNRouter-TUN",
                    @"ROOT\NET\TAILSCALE"),
            });

        Assert.False(result.Success);
        Assert.Empty(result.InstanceIds);
        Assert.Contains(@"SWD\Wintun\{GUID}", result.Error);
    }

    [Fact]
    public void NativeLookup_RegistryReadFailureFailsClosed()
    {
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            "VPNRouter-TUN",
            () => throw new UnauthorizedAccessException("blocked"));

        Assert.False(result.Success);
        Assert.Empty(result.InstanceIds);
        Assert.Contains("UnauthorizedAccessException", result.Error);
    }
}
