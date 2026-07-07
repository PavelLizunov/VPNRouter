#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace VPNRouter.Core.Services;

/// <summary>
/// Pure, cross-platform protocol layer for the Mullvad split-tunnel kernel driver
/// (<c>mullvad-split-tunnel</c>). This file is the ABI single-source-of-truth in C#:
/// IOCTL codes, the driver state machine, the WFP sublayer GUIDs/weights, the event
/// id + reason enums, and the hand-packed buffer builders / event parser that the
/// live-verified W1.0 spike proved reach <c>ENGAGED</c>.
///
/// <para><b>Deliberately has ZERO P/Invoke and no <c>[SupportedOSPlatform]</c>.</b> All
/// byte-layout logic is expressed with <see cref="BitConverter"/> / span writes over
/// managed <c>byte[]</c> so it compiles and unit-tests on Linux CI as well as Windows.
/// The Windows-only native calls live in <see cref="SplitTunnelDriverInterop"/>; the
/// I/O orchestration lives in the manager. This split mirrors <c>WedgeKillPolicy</c>
/// (pure decision) vs the HealthMonitor wiring: everything testable without a driver,
/// ProgramData, or the network sits here and is pinned by golden-vector unit tests.</para>
///
/// <para><b>ABI source:</b> <c>plans/w1-driver-abi-reference-2026-07-03.md</c> (pinned to
/// <c>win-split-tunnel@0a0eb97f</c> headers + <c>mullvadvpn-app@15fca6c8</c> driver.rs).
/// The buffer builders are a faithful port of the spike's hand-packed writers — the
/// offsets are relative to the string region, strings are UTF-16LE and
/// non-null-terminated, and length fields are in bytes. A shifted offset here would
/// give a runic kernel error instead of a compile error, so the golden-vector tests
/// are the safety net.</para>
/// </summary>
internal static class SplitTunnelDriverProtocol
{
    // ───────────────────────────────────────────────────────────────────────
    // Device / service identity
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>Symbolic device path — opened R/W, share_mode=0 (exclusive),
    /// FILE_FLAG_OVERLAPPED (needed only for the DEQUEUE_EVENT inverted call).</summary>
    public const string DevicePath = @"\\.\MULLVADSPLITTUNNEL";

    /// <summary>Kernel service name (<c>sc create &lt;name&gt; type= kernel</c>).</summary>
    public const string ServiceName = "mullvad-split-tunnel";

    // ───────────────────────────────────────────────────────────────────────
    // IOCTL codes.  CTL_CODE = (0x8000 << 16) | (access << 14) | (func << 2) | method,
    // access = FILE_ANY_ACCESS = 0.  Named per the ABI doc so no magic numbers leak
    // into the manager. METHOD: BUFFERED = 0, NEITHER = 3.
    // ───────────────────────────────────────────────────────────────────────

    public const uint IoctlInitialize = 0x80000004;          // func 1, BUFFERED — in: ST_SUBLAYER_GUIDS (32b)
    public const uint IoctlDequeueEvent = 0x80000008;         // func 2, BUFFERED — out: ST_EVENT_HEADER + payload (inverted call)
    public const uint IoctlRegisterProcesses = 0x8000000C;    // func 3, BUFFERED — in: process-registry buffer
    public const uint IoctlRegisterIpAddresses = 0x80000010;  // func 4, BUFFERED — in: SplitTunnelAddresses (40b)
    public const uint IoctlSetConfiguration = 0x80000018;     // func 6, BUFFERED — in: configuration buffer
    public const uint IoctlGetState = 0x80000024;             // func 9, BUFFERED — out: u64 state (8b)
    public const uint IoctlReset = 0x8000002F;                // func 11, NEITHER — none (before unload / to go inert)

    // ───────────────────────────────────────────────────────────────────────
    // WFP sublayers.  The driver installs its callout filters into these two
    // sublayers (by the GUIDs passed to INITIALIZE) but does NOT create them —
    // we ship no winfw, so the manager creates both before engaging and removes
    // them after RESET. Weights per winfw mullvadobjects.cpp.
    // ───────────────────────────────────────────────────────────────────────

    public static readonly Guid SublayerBaseline = new("21E068A2-2851-43C5-8A29-7AFE3F260384");
    public static readonly Guid SublayerDns = new("E65841B6-82F6-4D55-BDE2-61F84D4508D4");

    public const ushort SublayerWeightBaseline = 0xFFFF;
    public const ushort SublayerWeightDns = 0xFFFE;

    // ───────────────────────────────────────────────────────────────────────
    // Fixed struct strides / offsets (x64 natural alignment). Named so the
    // builders and the golden-vector tests share one definition of the ABI.
    // ───────────────────────────────────────────────────────────────────────

    // ST_CONFIGURATION_HEADER / ST_PROCESS_DISCOVERY_HEADER: SIZE_T NumEntries@0 + SIZE_T TotalLength@8.
    private const int HeaderSize = 16;

    // ST_CONFIGURATION_ENTRY: SIZE_T ImageNameOffset@0 + USHORT ImageNameLength@8 (+6 pad) = 16.
    private const int ConfigEntryStride = 16;

    // ST_PROCESS_DISCOVERY_ENTRY: HANDLE Pid@0 + HANDLE Parent@8 + SIZE_T Off@16 + USHORT Len@24 (+6 pad) = 32.
    private const int ProcessEntryStride = 32;

    // ───────────────────────────────────────────────────────────────────────
    // Driver state machine (GET_STATE returns a u64).
    // ───────────────────────────────────────────────────────────────────────

    public enum DriverState : ulong
    {
        None = 0,
        Started = 1,
        Initialized = 2,
        Ready = 3,
        Engaged = 4,
        /// <summary>Driver unloading (ZOMBIE / TERMINATING).</summary>
        Terminating = 5,
    }

    // ───────────────────────────────────────────────────────────────────────
    // Event ids (DEQUEUE_EVENT). The ERROR_FLAG (0x80000000) bit distinguishes
    // the error family; the concrete ids are ERROR_FLAG + 1..3.
    // ───────────────────────────────────────────────────────────────────────

    public const uint EventErrorFlag = 0x80000000;

    public enum EventId : uint
    {
        StartSplittingProcess = 0,
        StopSplittingProcess = 1,
        ErrorStartSplittingProcess = 0x80000001,
        ErrorStopSplittingProcess = 0x80000002,
        ErrorMessage = 0x80000003,
    }

    /// <summary>ST_SPLITTING_STATUS_CHANGE_REASON bitflags carried by splitting events.</summary>
    [Flags]
    public enum SplittingReason : uint
    {
        None = 0,
        ByInheritance = 1,
        ByConfig = 2,
        ProcessArriving = 4,
        ProcessDeparting = 8,
    }

    /// <summary>Which severity a parsed event maps to for logging.</summary>
    public enum EventSeverity
    {
        Information,
        Warning,
        Debug,
    }

    /// <summary>
    /// What the manager should do when the <c>mullvad-split-tunnel</c> kernel service
    /// already exists (collision guard, fail-path §3 #3). Decided purely from the two
    /// binPaths by <see cref="SplitTunnelPolicy.ClassifyServiceBinPath"/>.
    /// </summary>
    public enum ServiceCollisionAction
    {
        /// <summary>Existing binPath is exactly ours — just StartService it.</summary>
        StartExisting,
        /// <summary>Ours but relocated (our install moved) — <c>ChangeServiceConfig</c> to the new
        /// binPath, then start. Self-heal, mirrors Mullvad's install_driver_if_required.</summary>
        AdoptMovedInstall,
        /// <summary>Not ours (a real Mullvad daemon, or an unknown squatter on the name) —
        /// do NOT touch it; log and fall back to post-capture. Never break a coexisting VPN.</summary>
        BailForeign,
    }

    /// <summary>Discriminates the <see cref="SplitTunnelEvent"/> payload shape.</summary>
    public enum SplitTunnelEventKind
    {
        /// <summary>START/STOP splitting — Pid + Reason + Image populated.</summary>
        Splitting,
        /// <summary>ERROR_START/STOP splitting — Pid + Image populated.</summary>
        SplittingError,
        /// <summary>ERROR_MESSAGE — Status + Image (message text) populated.</summary>
        ErrorMessage,
        /// <summary>Event id was not recognised (forward-compat on a driver bump).</summary>
        Unknown,
        /// <summary>Buffer shorter than the fixed layout — never thrown, surfaced instead.</summary>
        Malformed,
    }

    // ───────────────────────────────────────────────────────────────────────
    // Sublayer GUIDs buffer (INITIALIZE input) — ST_SUBLAYER_GUIDS = 2 × GUID = 32 b.
    // Guid.ToByteArray() yields the Windows little-endian GUID layout (Data1 u32 LE,
    // Data2/3 u16 LE, Data4 as-is), which is exactly the on-wire GUID the driver reads.
    // ───────────────────────────────────────────────────────────────────────

    public static byte[] BuildSublayerGuids(Guid baseline, Guid dns)
    {
        var buf = new byte[32];
        baseline.ToByteArray().CopyTo(buf, 0);
        dns.ToByteArray().CopyTo(buf, 16);
        return buf;
    }

    // ───────────────────────────────────────────────────────────────────────
    // SplitTunnelAddresses buffer (REGISTER_IP_ADDRESSES) — 40 b, order matters:
    //   tunnel_ipv4 @0 (4) · internet_ipv4 @4 (4) · tunnel_ipv6 @8 (16) · internet_ipv6 @24 (16)
    // internet_* = physical NIC (excluded sockets bind here); tunnel_* = wintun/TUN.
    // Nulls are left zeroed. IN_ADDR / IN6_ADDR are stored in network byte order,
    // which is exactly what IPAddress.GetAddressBytes() returns.
    // ───────────────────────────────────────────────────────────────────────

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
        // Guard against a v6 address handed to a v4 slot (or vice versa): only copy
        // an address whose byte-length matches the slot, otherwise leave it zeroed.
        if (bytes.Length != expectedLen) return;
        bytes.CopyTo(buf, offset);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Configuration buffer (SET_CONFIGURATION):
    //   ST_CONFIGURATION_HEADER { SIZE_T NumEntries@0; SIZE_T TotalLength@8; }   (16 b)
    //   N × ST_CONFIGURATION_ENTRY { SIZE_T ImageNameOffset@0; USHORT ImageNameLength@8; } (16 b, 6 pad)
    //   string blob: excluded NT device paths concatenated (UTF-16LE, no NUL).
    // ImageNameOffset is relative to the string region (which starts after all entries).
    // ───────────────────────────────────────────────────────────────────────

    public static byte[] BuildConfiguration(IReadOnlyList<string> ntPaths)
    {
        var wide = new byte[ntPaths.Count][];
        for (int i = 0; i < ntPaths.Count; i++)
        {
            wide[i] = Encoding.Unicode.GetBytes(ntPaths[i] ?? string.Empty);
            // ImageNameLength is a USHORT — a path whose UTF-16 byte length overflows
            // 65535 cannot be represented and would silently truncate in the kernel.
            if (wide[i].Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"Excluded path #{i} is {wide[i].Length} UTF-16 bytes, exceeds the USHORT " +
                    $"ImageNameLength limit ({ushort.MaxValue}).", nameof(ntPaths));
        }

        int stringRegion = wide.Sum(w => w.Length);
        int total = HeaderSize + ConfigEntryStride * ntPaths.Count + stringRegion;

        var buf = new byte[total];

        // header
        WriteU64(buf, 0, (ulong)ntPaths.Count);   // NumEntries
        WriteU64(buf, 8, (ulong)total);           // TotalLength

        // entries (offset relative to the string region)
        int blobBase = HeaderSize + ConfigEntryStride * ntPaths.Count;
        int strOff = 0;
        for (int i = 0; i < ntPaths.Count; i++)
        {
            int entryOff = HeaderSize + ConfigEntryStride * i;
            WriteU64(buf, entryOff, (ulong)strOff);              // ImageNameOffset
            WriteU16(buf, entryOff + 8, (ushort)wide[i].Length); // ImageNameLength (bytes); 6 pad left zero
            wide[i].CopyTo(buf, blobBase + strOff);              // string blob
            strOff += wide[i].Length;
        }

        return buf;
    }

    // ───────────────────────────────────────────────────────────────────────
    // Process-registry buffer (REGISTER_PROCESSES):
    //   ST_PROCESS_DISCOVERY_HEADER { SIZE_T NumEntries@0; SIZE_T TotalLength@8; } (16 b)
    //   N × ST_PROCESS_DISCOVERY_ENTRY { HANDLE Pid@0; HANDLE Parent@8; SIZE_T Off@16; USHORT Len@24; } (32 b, 6 pad)
    //   string blob: each process's NT device path (UTF-16LE, no NUL). Empty → off/len 0.
    // Pid/Parent are HANDLEs widened to 8 bytes.
    // ───────────────────────────────────────────────────────────────────────

    public static byte[] BuildProcessRegistry(IReadOnlyList<ProcInfo> procs)
    {
        var wide = new byte[procs.Count][];
        for (int i = 0; i < procs.Count; i++)
        {
            wide[i] = string.IsNullOrEmpty(procs[i].DevicePath)
                ? Array.Empty<byte>()
                : Encoding.Unicode.GetBytes(procs[i].DevicePath);
            // Same USHORT ceiling as the config buffer; an NT path this long is not
            // physically possible, but guard rather than silently truncate.
            if (wide[i].Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"Process #{i} device path is {wide[i].Length} UTF-16 bytes, exceeds the USHORT limit.",
                    nameof(procs));
        }

        int stringRegion = wide.Sum(w => w.Length);
        int total = HeaderSize + ProcessEntryStride * procs.Count + stringRegion;

        var buf = new byte[total];

        // header
        WriteU64(buf, 0, (ulong)procs.Count);
        WriteU64(buf, 8, (ulong)total);

        int blobBase = HeaderSize + ProcessEntryStride * procs.Count;
        int strOff = 0;
        for (int i = 0; i < procs.Count; i++)
        {
            int entryOff = HeaderSize + ProcessEntryStride * i;
            WriteU64(buf, entryOff, procs[i].Pid);            // HANDLE ProcessId (widened to 8 b)
            WriteU64(buf, entryOff + 8, procs[i].ParentPid);  // HANDLE ParentProcessId
            if (wide[i].Length > 0)
            {
                WriteU64(buf, entryOff + 16, (ulong)strOff);              // ImageNameOffset
                WriteU16(buf, entryOff + 24, (ushort)wide[i].Length);    // ImageNameLength (bytes)
                wide[i].CopyTo(buf, blobBase + strOff);
                strOff += wide[i].Length;
            }
            // else: offset/len stay 0. 6 pad bytes after the USHORT stay zero.
        }

        return buf;
    }

    // ───────────────────────────────────────────────────────────────────────
    // pid-recycle guard (post-processing, pure): if a mapped parent's creation-time
    // is *newer* than the child, the pid was recycled and this "parent" is not the
    // real parent — set parent = 0. Mutates the map in place (parent absent → untouched).
    // ───────────────────────────────────────────────────────────────────────

    public static void ApplyPidRecycleGuard(Dictionary<uint, ProcInfo> byPid)
    {
        // Snapshot the keys so we can reassign entries while iterating.
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

    // ───────────────────────────────────────────────────────────────────────
    // Event parsing (DEQUEUE_EVENT out). Layout (§2.3 / events.h, x64 natural align):
    //   ST_EVENT_HEADER:  EventId u32 @0 (+4 pad) · EventSize SIZE_T @8 · EventData @16
    //   ST_SPLITTING_EVENT (body): Pid HANDLE @0 · Reason u32 @8 · ImageNameLength u16 @12 · ImageName @14
    //   ST_SPLITTING_ERROR_EVENT (body): Pid HANDLE @0 · ImageNameLength u16 @8 · ImageName @10
    //   ST_ERROR_MESSAGE_EVENT (body): NTSTATUS i32 @0 · MessageLength u16 @4 · Message @6
    // Body offsets are relative to EventData (absolute = 16 + body offset). Strings are
    // non-null-terminated UTF-16LE, length in bytes. Never throws: a too-short buffer
    // surfaces as Malformed, an unknown id as Unknown (forward-compat).
    // ───────────────────────────────────────────────────────────────────────

    private const int EventHeaderSize = 16;     // EventData @16

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
                // body: Pid@0 (8) · Reason@8 (4) · ImageNameLength@12 (2) · ImageName@14
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
                // body: Pid@0 (8) · ImageNameLength@8 (2) · ImageName@10
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
                // body: NTSTATUS@0 (4) · MessageLength@4 (2) · Message@6
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

    /// <summary>
    /// Reads a non-null-terminated UTF-16LE string of <paramref name="byteLen"/> bytes at
    /// <paramref name="offset"/> in <paramref name="body"/>, clamped to what the buffer
    /// actually holds (a truncated tail yields a shorter string rather than a throw).
    /// </summary>
    private static string ReadWideString(ReadOnlySpan<byte> body, int offset, int byteLen)
    {
        if (byteLen <= 0 || offset >= body.Length) return string.Empty;
        int avail = Math.Min(byteLen, body.Length - offset);
        avail &= ~1; // whole UTF-16 code units only
        if (avail <= 0) return string.Empty;
        return Encoding.Unicode.GetString(body.Slice(offset, avail));
    }

    // ───────────────────────────────────────────────────────────────────────
    // DOS → NT path (pure core). "C:\dir\app.exe" → "\Device\HarddiskVolumeN\dir\app.exe".
    // Splits the drive, calls the injected QueryDosDevice resolver on "C:", prepends the
    // returned device prefix to the remainder. Returns null (never throws) for a
    // non-drive path (UNC, relative) or when the resolver yields null/empty.
    // The Win32 QueryDosDeviceW call is injected so this stays pure + testable on Linux.
    // ───────────────────────────────────────────────────────────────────────

    public static string? DosPathToNtPath(string dosPath, Func<string, string?> queryDosDevice)
    {
        if (string.IsNullOrEmpty(dosPath) || dosPath.Length < 2 || dosPath[1] != ':')
            return null; // not a drive-letter DOS path (UNC "\\host\share", relative, …)

        char driveLetter = dosPath[0];
        if (!((driveLetter >= 'A' && driveLetter <= 'Z') || (driveLetter >= 'a' && driveLetter <= 'z')))
            return null;

        string drive = dosPath.Substring(0, 2);      // "C:"
        string remainder = dosPath.Substring(2);     // "\dir\app.exe" (may be empty / lack a leading '\')

        string? devicePrefix = queryDosDevice(drive);
        if (string.IsNullOrEmpty(devicePrefix))
            return null;

        if (remainder.Length == 0 || remainder[0] != '\\')
            remainder = "\\" + remainder;

        return devicePrefix + remainder;
    }

    // ───────────────────────────────────────────────────────────────────────
    // Internet-NIC picker (pure). Candidates are already-filtered snapshots
    // (Up + has a v4 gateway); this picks the one excluded sockets should bind to.
    // Excludes our own TUN by name and any WG/AWG/Tailscale adapter (reuses the
    // existing NetworkInterfaceDetector.IsWireGuardName). Prefers Ethernet over
    // Wireless over anything else; deterministic tie-break by name (ordinal) so the
    // choice is stable across enumeration order on a multi-NIC host.
    // ───────────────────────────────────────────────────────────────────────

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

    /// <summary>True when <paramref name="candidate"/> should win over <paramref name="current"/>.
    /// Lower type-rank wins (Ethernet &lt; Wireless &lt; other); ties break by ordinal name
    /// so the pick is deterministic regardless of enumeration order.</summary>
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

    // ───────────────────────────────────────────────────────────────────────
    // Little-endian writers (BitConverter is LE on every platform .NET runs the
    // desktop app on; x64 driver expects LE — the two agree).
    // ───────────────────────────────────────────────────────────────────────

    private static void WriteU64(byte[] buf, int offset, ulong value)
        => BitConverter.GetBytes(value).CopyTo(buf, offset);

    private static void WriteU16(byte[] buf, int offset, ushort value)
        => BitConverter.GetBytes(value).CopyTo(buf, offset);
}

// ───────────────────────────────────────────────────────────────────────────
// Supporting types (pure, cross-platform).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>A process in the registration snapshot: pid/ppid (HANDLE-widened),
/// process creation time (for the pid-recycle guard) and its NT device path.</summary>
internal readonly record struct ProcInfo(uint Pid, uint ParentPid, ulong CreationTime, string DevicePath);

/// <summary>
/// A parsed DEQUEUE_EVENT. One shape per <see cref="SplitTunnelDriverProtocol.SplitTunnelEventKind"/>;
/// irrelevant fields are left at their defaults. Constructed only by
/// <see cref="SplitTunnelDriverProtocol.ParseEventBuffer"/>; never throws on bad input
/// (Malformed / Unknown variants carry the failure instead).
/// </summary>
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

/// <summary>
/// An immutable snapshot of a NIC for the pure <see cref="SplitTunnelDriverProtocol.PickInternetInterface"/>
/// decision (so the picker is testable without a live <see cref="NetworkInterface"/>, which is
/// abstract). <paramref name="V4"/>/<paramref name="V6"/> are the interface's chosen unicast
/// addresses (null when absent).
/// </summary>
internal readonly record struct NicSnapshot(
    string Name,
    string? Description,
    NetworkInterfaceType Type,
    bool IsUp,
    bool HasV4Gateway,
    IPAddress? V4,
    IPAddress? V6);

/// <summary>
/// Pure split-tunnel decisions (mirror of <c>WedgeKillPolicy</c> / <c>DnsLockdownPolicy</c>):
/// the "should we engage", "how loud is this event", and "did the addresses change enough to
/// re-register" gates, all testable without a driver.
/// </summary>
internal static class SplitTunnelPolicy
{
    /// <summary>
    /// True only when the driver should engage: Windows, routing mode = split, apps-mode =
    /// exclude, at least one excluded app, and the <c>true_split_driver</c> setting is not "off".
    /// Any other combination (include-split, full-tunnel, empty exclude list, off, non-Windows)
    /// → false. String comparisons are ordinal/ignore-case so yaml casing doesn't matter.
    /// </summary>
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

    // AppConfig.RoutingAppsMode is the settings enum; AppSettingsSane canonicalises it to
    // lowercase "include"/"exclude", so "exclude" is the only spelling we ever see (P1 wire-in #2).
    private static bool IsExcludeMode(string routingAppsMode)
        => string.Equals(routingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps an event id to a log severity: splitting → Information, any error-flag
    /// id → Warning, an unrecognised id → Debug (skip / forward-compat).</summary>
    public static SplitTunnelDriverProtocol.EventSeverity ClassifyEvent(uint eventId)
    {
        switch (eventId)
        {
            case (uint)SplitTunnelDriverProtocol.EventId.StartSplittingProcess:
            case (uint)SplitTunnelDriverProtocol.EventId.StopSplittingProcess:
                return SplitTunnelDriverProtocol.EventSeverity.Information;
        }
        if ((eventId & SplitTunnelDriverProtocol.EventErrorFlag) != 0)
            return SplitTunnelDriverProtocol.EventSeverity.Warning;
        return SplitTunnelDriverProtocol.EventSeverity.Debug;
    }

    /// <summary>
    /// True when the internet/tunnel addresses changed enough to warrant a
    /// REGISTER_IP_ADDRESSES re-register: any of the four slots differs (a v4 change, or a
    /// v6 appearing/disappearing/changing). Identical tuples → false. Null-safe.
    /// </summary>
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

    /// <summary>
    /// Collision guard (fail-path §3 #3): decides what to do when the
    /// <c>mullvad-split-tunnel</c> service already exists, from its current binPath vs the
    /// one we would install. Deliberately conservative — we only ever mutate a service whose
    /// binPath is recognisably OURS (contains a "VPNRouter" segment); anything else (a real
    /// Mullvad daemon, or an unknown service squatting the name) is left untouched so a
    /// coexisting VPN is never broken. The excluded apps just fall back to post-capture routing.
    /// </summary>
    public static SplitTunnelDriverProtocol.ServiceCollisionAction ClassifyServiceBinPath(string existingBinPath, string ourBinPath)
    {
        string existing = NormalizeBinPath(existingBinPath);
        string ours = NormalizeBinPath(ourBinPath);

        if (existing.Length != 0 && existing == ours)
            return SplitTunnelDriverProtocol.ServiceCollisionAction.StartExisting;

        // Only self-heal a path that is unmistakably a RELOCATED VPNRouter install of THIS driver:
        // a whole "\vpnrouter\" path segment (not a raw substring — defeats "…\NotVpnRouterApp\…")
        // AND our exact layout tail "\driver\mullvad-split-tunnel.sys" (bug-hunt: keying on the segment
        // alone would ChangeServiceConfig any "…\vpnrouter\…\foo.sys" squatter). Real Mullvad
        // ("…\Mullvad VPN\resources\mullvad-split-tunnel.sys") has no "\vpnrouter\" segment → bails.
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

    /// <summary>Normalises an SCM binPath for comparison: strips surrounding quotes and a
    /// leading NT object-manager prefix (<c>\??\</c>), trims, lowercases (paths are
    /// case-insensitive on Windows). Empty/whitespace → empty string.</summary>
    private static string NormalizeBinPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string p = path.Trim().Trim('"').Trim();
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p.Substring(4);
        return p.ToLowerInvariant();
    }
}
