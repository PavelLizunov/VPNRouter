using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
#if PLATFORM_WINDOWS
using System.Management;
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

/// <summary>
/// Exact local-device removal for Windows builds whose pnputil predates
/// /remove-device and /enum-devices (notably Windows 10 LTSC 2019).
/// </summary>
internal static class WindowsPnpDeviceManager
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevNode = 0x0000000D;
    private const uint CmLocateDevNodePhantom = 0x00000001;

    internal static NativePnpLookupResult FindNetworkAdapterInstanceIds(string adapterName)
    {
#if PLATFORM_WINDOWS
        if (string.IsNullOrWhiteSpace(adapterName) ||
            adapterName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            return new(false, Array.Empty<string>(), "Adapter name is outside the owned-name whitelist.");
        }

        try
        {
            var connection = new ConnectionOptions { Timeout = LookupTimeout };
            var scope = new ManagementScope(@"\\.\root\cimv2", connection);
            scope.Connect();

            var query = new ObjectQuery(
                $"SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE NetConnectionID = '{adapterName}'");
            var options = new System.Management.EnumerationOptions
            {
                Timeout = LookupTimeout,
                // Semisynchronous enumeration makes the WMI timeout apply to
                // result retrieval instead of blocking Get() until all rows exist.
                ReturnImmediately = true,
                Rewindable = false,
            };

            using var searcher = new ManagementObjectSearcher(scope, query, options);
            using var results = searcher.Get();
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject adapter in results)
            {
                using (adapter)
                {
                    var id = adapter["PNPDeviceID"] as string;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return new(false, Array.Empty<string>(),
                            "A matching Win32_NetworkAdapter row has no PNPDeviceID.");
                    }

                    id = id.Trim();
                    if (seen.Add(id)) ids.Add(id);
                }
            }

            return new(true, ids, null);
        }
        catch (Exception ex)
        {
            return new(false, Array.Empty<string>(), $"{ex.GetType().Name}: {ex.Message}");
        }
#else
        return new(false, Array.Empty<string>(), "Native network-adapter lookup is Windows-only.");
#endif
    }

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
