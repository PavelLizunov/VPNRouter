#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace VPNRouter.Core.Services;

internal static class SplitTunnelDriverProtocol
{
    public const string DevicePath = @"\\.\MULLVADSPLITTUNNEL";

    public const string ServiceName = "mullvad-split-tunnel";

    public const uint IoctlInitialize = 0x80000004;
    public const uint IoctlDequeueEvent = 0x80000008;
    public const uint IoctlRegisterProcesses = 0x8000000C;
    public const uint IoctlRegisterIpAddresses = 0x80000010;
    public const uint IoctlSetConfiguration = 0x80000018;
    public const uint IoctlGetState = 0x80000024;
    public const uint IoctlReset = 0x8000002F;

    public static readonly Guid SublayerBaseline = new("21E068A2-2851-43C5-8A29-7AFE3F260384");
    public static readonly Guid SublayerDns = new("E65841B6-82F6-4D55-BDE2-61F84D4508D4");

    public const ushort SublayerWeightBaseline = 0xFFFF;
    public const ushort SublayerWeightDns = 0xFFFE;

    private const int HeaderSize = 16;

    private const int ConfigEntryStride = 16;

    private const int ProcessEntryStride = 32;

    public enum DriverState : ulong
    {
        None = 0,
        Started = 1,
        Initialized = 2,
        Ready = 3,
        Engaged = 4,
        Terminating = 5,
    }

    public enum EventId : uint
    {
        StartSplittingProcess = 0,
        StopSplittingProcess = 1,
        ErrorStartSplittingProcess = 0x80000001,
        ErrorStopSplittingProcess = 0x80000002,
        ErrorMessage = 0x80000003,
    }

    [Flags]
    public enum SplittingReason : uint
    {
        None = 0,
        ByInheritance = 1,
        ByConfig = 2,
        ProcessArriving = 4,
        ProcessDeparting = 8,
    }

public enum ServiceCollisionAction
    {
        StartExisting,
        AdoptMovedInstall,
        BailForeign,
    }

    public enum SplitTunnelEventKind
    {
        Splitting,
        SplittingError,
        ErrorMessage,
        Unknown,
        Malformed,
    }

    public static byte[] BuildSublayerGuids(Guid baseline, Guid dns)
    {
        var buf = new byte[32];
        baseline.ToByteArray().CopyTo(buf, 0);
        dns.ToByteArray().CopyTo(buf, 16);
        return buf;
    }

    // Driver ABI: 40-byte buffer with fixed offsets (tunnel v4 @0, internet v4 @4, tunnel v6 @8, internet v6 @24).
    public static byte[] BuildAddresses(IPAddress? tunnelV4, IPAddress? internetV4, IPAddress? tunnelV6, IPAddress? internetV6)
    {
        var buf = new byte[40];
        CopyAddr(tunnelV4, buf, 0, 4);
        CopyAddr(internetV4, buf, 4, 4);
        CopyAddr(tunnelV6, buf, 8, 16);
        CopyAddr(internetV6, buf, 24, 16);
        return buf;
    }

    private static void CopyAddr(IPAddress? addr, byte[] buf, int offset, int expectedLen)
    {
        if (addr is null) return;
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != expectedLen) return;
        bytes.CopyTo(buf, offset);
    }

    public static byte[] BuildConfiguration(IReadOnlyList<string> ntPaths)
    {
        var wide = new byte[ntPaths.Count][];
        for (int i = 0; i < ntPaths.Count; i++)
        {
            wide[i] = Encoding.Unicode.GetBytes(ntPaths[i] ?? string.Empty);
            if (wide[i].Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"Excluded path #{i} is {wide[i].Length} UTF-16 bytes, exceeds the USHORT " +
                    $"ImageNameLength limit ({ushort.MaxValue}).", nameof(ntPaths));
        }

        int stringRegion = wide.Sum(w => w.Length);
        int total = HeaderSize + ConfigEntryStride * ntPaths.Count + stringRegion;

        var buf = new byte[total];

        WriteU64(buf, 0, (ulong)ntPaths.Count);
        WriteU64(buf, 8, (ulong)total);

        int blobBase = HeaderSize + ConfigEntryStride * ntPaths.Count;
        int strOff = 0;
        for (int i = 0; i < ntPaths.Count; i++)
        {
            int entryOff = HeaderSize + ConfigEntryStride * i;
            WriteU64(buf, entryOff, (ulong)strOff);
            WriteU16(buf, entryOff + 8, (ushort)wide[i].Length);
            wide[i].CopyTo(buf, blobBase + strOff);
            strOff += wide[i].Length;
        }

        return buf;
    }

    public static byte[] BuildProcessRegistry(IReadOnlyList<ProcInfo> procs)
    {
        var wide = new byte[procs.Count][];
        for (int i = 0; i < procs.Count; i++)
        {
            wide[i] = string.IsNullOrEmpty(procs[i].DevicePath)
                ? Array.Empty<byte>()
                : Encoding.Unicode.GetBytes(procs[i].DevicePath);
            if (wide[i].Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"Process #{i} device path is {wide[i].Length} UTF-16 bytes, exceeds the USHORT limit.",
                    nameof(procs));
        }

        int stringRegion = wide.Sum(w => w.Length);
        int total = HeaderSize + ProcessEntryStride * procs.Count + stringRegion;

        var buf = new byte[total];

        WriteU64(buf, 0, (ulong)procs.Count);
        WriteU64(buf, 8, (ulong)total);

        int blobBase = HeaderSize + ProcessEntryStride * procs.Count;
        int strOff = 0;
        for (int i = 0; i < procs.Count; i++)
        {
            int entryOff = HeaderSize + ProcessEntryStride * i;
            WriteU64(buf, entryOff, procs[i].Pid);
            WriteU64(buf, entryOff + 8, procs[i].ParentPid);
            if (wide[i].Length > 0)
            {
                WriteU64(buf, entryOff + 16, (ulong)strOff);
                WriteU16(buf, entryOff + 24, (ushort)wide[i].Length);
                wide[i].CopyTo(buf, blobBase + strOff);
                strOff += wide[i].Length;
            }
        }

        return buf;
    }

    public static void ApplyPidRecycleGuard(Dictionary<uint, ProcInfo> byPid)
    {
        foreach (var pid in byPid.Keys.ToList())
        {
            var info = byPid[pid];
            if (info.ParentPid == 0) continue;
            if (byPid.TryGetValue(info.ParentPid, out var parent) &&
                parent.CreationTime > info.CreationTime)
            {
                byPid[pid] = info with { ParentPid = 0 };
            }
        }
    }

    private const int EventHeaderSize = 16;

    public static SplitTunnelEvent ParseEventBuffer(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < EventHeaderSize)
            return SplitTunnelEvent.Malformed();

        uint rawId = BitConverter.ToUInt32(buffer.Slice(0, 4));
        var body = buffer.Slice(EventHeaderSize);

        switch (rawId)
        {
            case (uint)EventId.StartSplittingProcess:
            case (uint)EventId.StopSplittingProcess:
            {
                const int strOff = 14;
                if (body.Length < strOff) return SplitTunnelEvent.Malformed();
                ulong pid = BitConverter.ToUInt64(body.Slice(0, 8));
                uint reason = BitConverter.ToUInt32(body.Slice(8, 4));
                ushort len = BitConverter.ToUInt16(body.Slice(12, 2));
                string image = ReadWideString(body, strOff, len);
                return new SplitTunnelEvent(
                    SplitTunnelEventKind.Splitting, (EventId)rawId, pid,
                    (SplittingReason)reason, image, Status: 0);
            }

            case (uint)EventId.ErrorStartSplittingProcess:
            case (uint)EventId.ErrorStopSplittingProcess:
            {
                const int strOff = 10;
                if (body.Length < strOff) return SplitTunnelEvent.Malformed();
                ulong pid = BitConverter.ToUInt64(body.Slice(0, 8));
                ushort len = BitConverter.ToUInt16(body.Slice(8, 2));
                string image = ReadWideString(body, strOff, len);
                return new SplitTunnelEvent(
                    SplitTunnelEventKind.SplittingError, (EventId)rawId, pid,
                    SplittingReason.None, image, Status: 0);
            }

            case (uint)EventId.ErrorMessage:
            {
                const int strOff = 6;
                if (body.Length < strOff) return SplitTunnelEvent.Malformed();
                int status = BitConverter.ToInt32(body.Slice(0, 4));
                ushort len = BitConverter.ToUInt16(body.Slice(4, 2));
                string msg = ReadWideString(body, strOff, len);
                return new SplitTunnelEvent(
                    SplitTunnelEventKind.ErrorMessage, (EventId)rawId, Pid: 0,
                    SplittingReason.None, msg, status);
            }

            default:
                return new SplitTunnelEvent(
                    SplitTunnelEventKind.Unknown, Id: default, Pid: 0,
                    SplittingReason.None, Image: string.Empty, Status: 0, UnknownId: rawId);
        }
    }

    private static string ReadWideString(ReadOnlySpan<byte> body, int offset, int byteLen)
    {
        if (byteLen <= 0 || offset >= body.Length) return string.Empty;
        int avail = Math.Min(byteLen, body.Length - offset);
        avail &= ~1;
        if (avail <= 0) return string.Empty;
        return Encoding.Unicode.GetString(body.Slice(offset, avail));
    }

    public static string? DosPathToNtPath(string dosPath, Func<string, string?> queryDosDevice)
    {
        if (string.IsNullOrEmpty(dosPath) || dosPath.Length < 2 || dosPath[1] != ':')
            return null;

        char driveLetter = dosPath[0];
        if (!((driveLetter >= 'A' && driveLetter <= 'Z') || (driveLetter >= 'a' && driveLetter <= 'z')))
            return null;

        string drive = dosPath.Substring(0, 2);
        string remainder = dosPath.Substring(2);

        string? devicePrefix = queryDosDevice(drive);
        if (string.IsNullOrEmpty(devicePrefix))
            return null;

        if (remainder.Length == 0 || remainder[0] != '\\')
            remainder = "\\" + remainder;

        return devicePrefix + remainder;
    }

    public static NicSnapshot? PickInternetInterface(IReadOnlyList<NicSnapshot> nics)
    {
        NicSnapshot? best = null;
        foreach (var nic in nics)
        {
            if (!nic.IsUp || !nic.HasV4Gateway || nic.V4 is null)
                continue;
            if (NetworkInterfaceDetector.IsWireGuardName(nic.Name, nic.Description))
                continue;

            if (best is null || Prefer(nic, best.Value))
                best = nic;
        }
        return best;
    }

    private static bool Prefer(NicSnapshot candidate, NicSnapshot current)
    {
        int rc = TypeRank(candidate.Type), rr = TypeRank(current.Type);
        if (rc != rr) return rc < rr;
        return string.CompareOrdinal(candidate.Name, current.Name) < 0;
    }

    private static int TypeRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        _ => 2,
    };

    private static void WriteU64(byte[] buf, int offset, ulong value)
        => BitConverter.GetBytes(value).CopyTo(buf, offset);

    private static void WriteU16(byte[] buf, int offset, ushort value)
        => BitConverter.GetBytes(value).CopyTo(buf, offset);
}

internal readonly record struct ProcInfo(uint Pid, uint ParentPid, ulong CreationTime, string DevicePath);

internal readonly record struct SplitTunnelEvent(
    SplitTunnelDriverProtocol.SplitTunnelEventKind Kind,
    SplitTunnelDriverProtocol.EventId Id,
    ulong Pid,
    SplitTunnelDriverProtocol.SplittingReason Reason,
    string Image,
    int Status,
    uint UnknownId = 0)
{
    public static SplitTunnelEvent Malformed() => new(
        SplitTunnelDriverProtocol.SplitTunnelEventKind.Malformed,
        default, 0, SplitTunnelDriverProtocol.SplittingReason.None, string.Empty, 0);
}

internal readonly record struct NicSnapshot(
    string Name,
    string? Description,
    NetworkInterfaceType Type,
    bool IsUp,
    bool HasV4Gateway,
    IPAddress? V4,
    IPAddress? V6);

internal static class SplitTunnelPolicy
{
    private const uint FwpAlreadyExists = 0x80320009;

    public static bool ShouldEngage(
        bool isWindows, string routingMode, string routingAppsMode, bool hasExcludedApps, string driverSetting)
    {
        if (!isWindows) return false;
        if (!string.Equals(routingMode, "split", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsExcludeMode(routingAppsMode)) return false;
        if (!hasExcludedApps) return false;
        if (string.Equals(driverSetting, "off", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsExcludeMode(string routingAppsMode)
        => string.Equals(routingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldReRegister(
        (IPAddress? TunV4, IPAddress? InetV4, IPAddress? TunV6, IPAddress? InetV6) oldAddrs,
        (IPAddress? TunV4, IPAddress? InetV4, IPAddress? TunV6, IPAddress? InetV6) newAddrs)
    {
        return !AddrEq(oldAddrs.TunV4, newAddrs.TunV4)
            || !AddrEq(oldAddrs.InetV4, newAddrs.InetV4)
            || !AddrEq(oldAddrs.TunV6, newAddrs.TunV6)
            || !AddrEq(oldAddrs.InetV6, newAddrs.InetV6);
    }

    private static bool AddrEq(IPAddress? a, IPAddress? b)
    {
        if (a is null) return b is null;
        return a.Equals(b);
    }

    public static SplitTunnelDriverProtocol.ServiceCollisionAction ClassifyServiceBinPath(string existingBinPath, string ourBinPath)
    {
        string existing = NormalizeBinPath(existingBinPath);
        string ours = NormalizeBinPath(ourBinPath);

        if (existing.Length != 0 && existing == ours)
            return SplitTunnelDriverProtocol.ServiceCollisionAction.StartExisting;

        if (existing.Contains(@"\vpnrouter\", StringComparison.Ordinal)
            && existing.EndsWith(@"\driver\mullvad-split-tunnel.sys", StringComparison.Ordinal))
            return SplitTunnelDriverProtocol.ServiceCollisionAction.AdoptMovedInstall;

        return SplitTunnelDriverProtocol.ServiceCollisionAction.BailForeign;
    }

    public static bool IsForeignSplitDriverService(string? serviceName, string? pathName)
    {
        if (string.Equals(serviceName, SplitTunnelDriverProtocol.ServiceName, StringComparison.OrdinalIgnoreCase))
            return false;
        return NormalizeBinPath(pathName).EndsWith(@"\mullvad-split-tunnel.sys", StringComparison.Ordinal);
    }

    public static string FormatForeignSplitDriverOwner(string serviceName, string? displayName, string pathName)
    {
        string label = string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, serviceName, StringComparison.OrdinalIgnoreCase)
            ? serviceName
            : $"{displayName} ({serviceName})";
        return "True Split cannot start because another split-tunnel kernel driver is already running: " +
               $"{label} at {pathName}. VPNRouter will not stop this kernel driver automatically because doing so can crash Windows. " +
               "Close that VPN, disable its split tunneling/service, reboot Windows, then retry True Split.";
    }

    public static string? FormatDriverStartFailure(uint errorCode) =>
        errorCode == FwpAlreadyExists
            ? "True Split cannot start because Windows WFP/BFE already has a split-tunnel object from Amnezia/Mullvad/another VPN (0x80320009). Ordinary split is active. Disable that VPN's split tunneling, reboot Windows, then retry True Split."
            : null;

    public static string? ParseSidecarHashFor(string? sidecarContent, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sidecarContent) || string.IsNullOrWhiteSpace(fileName))
            return null;
        foreach (var raw in sidecarContent.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp <= 0) continue;
            var hash = line.Substring(0, sp).Trim().ToLowerInvariant();
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
            var name = line.Substring(sp).Trim().TrimStart('*');
            name = name.Replace('\\', '/');
            var baseName = name.Contains('/') ? name.Substring(name.LastIndexOf('/') + 1) : name;
            if (string.Equals(baseName, fileName, StringComparison.OrdinalIgnoreCase))
                return hash;
        }
        return null;
    }

    private static string NormalizeBinPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string p = path.Trim().Trim('"').Trim();
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p.Substring(4);
        return p.ToLowerInvariant();
    }
}
