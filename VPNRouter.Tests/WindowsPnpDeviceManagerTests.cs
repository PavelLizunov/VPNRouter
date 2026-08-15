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

    [Theory]
    [InlineData("Tailscale")]
    [InlineData("VPNRouter-TUN 46")]
    [InlineData("sing-box-tun-")]
    public void NativeLookup_RejectsOtherOrNumberedAdapterNames(string adapterName)
    {
        var readCalled = false;
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            adapterName,
            () =>
            {
                readCalled = true;
                return Array.Empty<NativeNetworkConnectionRecord>();
            });

        Assert.False(result.Success);
        Assert.False(readCalled);
    }

    [Fact]
    public void NativeLookup_ExactNameReturnsPhantomIdAndIgnoresNumberedNames()
    {
        const string exactId = @"SWD\Wintun\{11111111-1111-1111-1111-111111111111}";
        var records = new[]
        {
            new NativeNetworkConnectionRecord("{22222222-2222-2222-2222-222222222222}",
                "VPNRouter-TUN 46",
                @"SWD\Wintun\{22222222-2222-2222-2222-222222222222}"),
            new NativeNetworkConnectionRecord("{11111111-1111-1111-1111-111111111111}",
                "VPNRouter-TUN", exactId),
            new NativeNetworkConnectionRecord("{33333333-3333-3333-3333-333333333333}",
                "Tailscale",
                @"SWD\Wintun\{33333333-3333-3333-3333-333333333333}"),
        };

        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            "VPNRouter-TUN", () => records);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { exactId }, result.InstanceIds);
    }

    [Theory]
    [InlineData("sing-box-tun")]
    [InlineData("sing-box-tun-legacy_1")]
    public void NativeLookup_AcceptsOwnedFallbackNames(string adapterName)
    {
        const string id = @"SWD\Wintun\{44444444-4444-4444-4444-444444444444}";
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            adapterName,
            () => new[]
            {
                new NativeNetworkConnectionRecord(
                    "{44444444-4444-4444-4444-444444444444}", adapterName, id),
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { id }, result.InstanceIds);
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
    public void NativeLookup_AcceptsDistinctConnectionAndWintunDeviceGuids()
    {
        const string pnpId = @"SWD\Wintun\{66666666-6666-6666-6666-666666666666}";
        var result = WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds(
            "VPNRouter-TUN",
            () => new[]
            {
                new NativeNetworkConnectionRecord(
                    "{77777777-7777-7777-7777-777777777777}",
                    "VPNRouter-TUN",
                    pnpId),
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { pnpId }, result.InstanceIds);
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

    [Theory]
    [InlineData(17763, true)]
    [InlineData(19041, false)]
    [InlineData(22621, false)]
    public void WindowsBuild_SelectsCompatibleRemovalApi(int build, bool expectedNative)
    {
        Assert.Equal(expectedNative,
            TunAdapterDiagnostics.RequiresNativePnpForWindowsBuild(build));
    }

    [Fact]
    public void NativeEntryPointsAndDeviceInfoLayout_ArePinned()
    {
        var type = typeof(WindowsPnpDeviceManager);

        var open = type.GetMethod(
            "SetupDiOpenDeviceInfoW", BindingFlags.Static | BindingFlags.NonPublic)!;
        var openImport = open.GetCustomAttribute<DllImportAttribute>()!;
        Assert.Equal("setupapi.dll", openImport.Value, ignoreCase: true);
        Assert.True(openImport.ExactSpelling);
        Assert.Equal(CharSet.Unicode, openImport.CharSet);

        var uninstall = type.GetMethod(
            "DiUninstallDevice", BindingFlags.Static | BindingFlags.NonPublic)!;
        var uninstallImport = uninstall.GetCustomAttribute<DllImportAttribute>()!;
        Assert.Equal("newdev.dll", uninstallImport.Value, ignoreCase: true);
        Assert.True(uninstallImport.SetLastError);
        Assert.True(uninstallImport.ExactSpelling);
        Assert.Equal(UnmanagedType.Bool,
            uninstall.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()!.Value);

        var uninstallParameters = uninstall.GetParameters();
        Assert.Equal(5, uninstallParameters.Length);
        Assert.Equal(typeof(IntPtr), uninstallParameters[0].ParameterType);
        Assert.Equal(typeof(IntPtr), uninstallParameters[1].ParameterType);
        Assert.True(uninstallParameters[2].ParameterType.IsByRef);
        Assert.Equal(typeof(uint), uninstallParameters[3].ParameterType);
        Assert.Equal(typeof(bool).MakeByRefType(), uninstallParameters[4].ParameterType);
        Assert.True(uninstallParameters[4].IsOut);
        Assert.Equal(UnmanagedType.Bool,
            uninstallParameters[4].GetCustomAttribute<MarshalAsAttribute>()!.Value);

        var locate = type.GetMethod(
            "CMLocateDevNodeW", BindingFlags.Static | BindingFlags.NonPublic)!;
        var locateImport = locate.GetCustomAttribute<DllImportAttribute>()!;
        Assert.Equal("cfgmgr32.dll", locateImport.Value, ignoreCase: true);
        Assert.Equal("CM_Locate_DevNodeW", locateImport.EntryPoint);
        Assert.True(locateImport.ExactSpelling);

        var deviceInfo = type.GetNestedType(
            "SpDevInfoData", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 32 : 28, Marshal.SizeOf(deviceInfo));
    }

    [Fact]
    public void QueryPresence_NonexistentExactId_ResolvesNativeAbiWithoutMutation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = WindowsPnpDeviceManager.QueryPresence(
            @"ROOT\NET\VPNROUTER_ABI_TEST_FFFFFFFF");

        Assert.Equal(NativePnpPresence.Absent, result.Presence);
        Assert.Equal(0x0000000Du, result.ConfigManagerResult);
    }
}
