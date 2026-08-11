using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
#if PLATFORM_WINDOWS
using Microsoft.Win32;
#endif

namespace VPNRouter.Core.Services;

internal readonly record struct NativePnpRemovalResult(
    bool Success,
    bool RestartRequired,
    int ErrorCode);

internal enum NativePnpPresence
{
    Present,
    Absent,
    Error,
}

internal readonly record struct NativePnpPresenceResult(
    NativePnpPresence Presence,
    uint ConfigManagerResult);

internal readonly record struct NativePnpLookupResult(
    bool Success,
    IReadOnlyList<string> InstanceIds,
    string? Error);

internal readonly record struct NativeNetworkConnectionRecord(
    string? ConnectionId,
    string? Name,
    string? PnpInstanceId);

/// <summary>
/// Exact local-device removal for Windows builds whose pnputil predates
/// /remove-device and /enum-devices (notably Windows 10 LTSC 2019).
/// </summary>
internal static class WindowsPnpDeviceManager
{
    private const string NetworkConnectionsRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevNode = 0x0000000D;
    private const uint CmLocateDevNodePhantom = 0x00000001;

    internal static NativePnpLookupResult FindNetworkAdapterInstanceIds(string adapterName)
    {
#if PLATFORM_WINDOWS
        return FindNetworkAdapterInstanceIds(adapterName, ReadNetworkConnections);
#else
        return new(false, Array.Empty<string>(), "Native network-adapter lookup is Windows-only.");
#endif
    }

    internal static NativePnpLookupResult FindNetworkAdapterInstanceIds(
        string adapterName,
        Func<IReadOnlyList<NativeNetworkConnectionRecord>> readConnections)
    {
        if (!IsOwnedAdapterName(adapterName))
        {
            return new(false, Array.Empty<string>(), "Adapter name is outside the owned-name whitelist.");
        }

        try
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var connection in readConnections())
            {
                if (!string.Equals(connection.Name, adapterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(connection.PnpInstanceId))
                    return new(false, Array.Empty<string>(),
                        "A matching Windows network connection has no PnpInstanceID.");

                var id = connection.PnpInstanceId.Trim();
                if (!TryValidateOwnedWintunMapping(connection.ConnectionId, id))
                {
                    return new(false, Array.Empty<string>(),
                        "A matching Windows network connection is not mapped to its exact " +
                        @"SWD\Wintun\{GUID} PnpInstanceID.");
                }

                if (seen.Add(id)) ids.Add(id);
            }

            return new(true, ids, null);
        }
        catch (Exception ex)
        {
            return new(false, Array.Empty<string>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsOwnedAdapterName(string? adapterName)
    {
        if (string.Equals(adapterName, "VPNRouter-TUN", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(adapterName, "sing-box-tun", StringComparison.OrdinalIgnoreCase))
            return true;
        if (adapterName == null ||
            !adapterName.StartsWith("sing-box-tun-", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = adapterName["sing-box-tun-".Length..];
        return suffix.Length > 0 &&
               suffix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    private static bool TryValidateOwnedWintunMapping(
        string? connectionId,
        string pnpInstanceId)
    {
        const string prefix = @"SWD\Wintun\";
        if (string.IsNullOrWhiteSpace(connectionId) ||
            !Guid.TryParseExact(connectionId.Trim(), "B", out var connectionGuid) ||
            !pnpInstanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(pnpInstanceId[prefix.Length..], "B", out var pnpGuid))
        {
            return false;
        }

        return connectionGuid == pnpGuid;
    }

#if PLATFORM_WINDOWS
    private static IReadOnlyList<NativeNetworkConnectionRecord> ReadNetworkConnections()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows network connections are Windows-only.");

        using var root = Registry.LocalMachine.OpenSubKey(NetworkConnectionsRegistryPath);
        if (root == null)
            throw new InvalidOperationException(
                $"Windows network-connections key is unavailable: HKLM\\{NetworkConnectionsRegistryPath}");

        var connections = new List<NativeNetworkConnectionRecord>();
        foreach (var connectionId in root.GetSubKeyNames())
        {
            using var connection = root.OpenSubKey($@"{connectionId}\Connection");
            if (connection == null) continue;

            connections.Add(new(
                connectionId,
                connection.GetValue("Name") as string,
                connection.GetValue("PnpInstanceID") as string));
        }

        return connections;
    }
#endif

    internal static NativePnpRemovalResult RemoveDevice(string instanceId)
    {
        var deviceInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
            return new(false, false, Marshal.GetLastWin32Error());

        try
        {
            var deviceInfo = new SpDevInfoData
            {
                Size = (uint)Marshal.SizeOf<SpDevInfoData>(),
            };
            if (!SetupDiOpenDeviceInfoW(
                    deviceInfoSet, instanceId, IntPtr.Zero, 0, ref deviceInfo))
            {
                return new(false, false, Marshal.GetLastWin32Error());
            }

            if (!DiUninstallDevice(
                    IntPtr.Zero, deviceInfoSet, ref deviceInfo, 0, out var restartRequired))
            {
                return new(false, restartRequired, Marshal.GetLastWin32Error());
            }

            return new(true, restartRequired, 0);
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    internal static NativePnpPresenceResult QueryPresence(string instanceId)
    {
        // PHANTOM includes non-present device records. Wintun can still reject
        // a create while that record exists even though the live devnode is no
        // longer configured, so NORMAL would make the settle gate fail open.
        var result = CMLocateDevNodeW(out _, instanceId, CmLocateDevNodePhantom);
        return result switch
        {
            CrSuccess => new(NativePnpPresence.Present, result),
            CrNoSuchDevNode => new(NativePnpPresence.Absent, result),
            _ => new(NativePnpPresence.Error, result),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(
        IntPtr classGuid,
        IntPtr parentWindow);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(
        IntPtr deviceInfoSet,
        string deviceInstanceId,
        IntPtr parentWindow,
        uint openFlags,
        ref SpDevInfoData deviceInfoData);

    [DllImport("newdev.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiUninstallDevice(
        IntPtr parentWindow,
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool restartRequired);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "CM_Locate_DevNodeW", ExactSpelling = true)]
    private static extern uint CMLocateDevNodeW(
        out uint deviceInstance,
        string deviceInstanceId,
        uint flags);
}
