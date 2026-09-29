#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using VPNRouter.Core.Services;
using Xunit;

using P = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Tests;

public class SplitTunnelProtocolTests
{
    private static ulong U64(byte[] b, int off) => BitConverter.ToUInt64(b, off);
    private static ushort U16(byte[] b, int off) => BitConverter.ToUInt16(b, off);
    private static uint U32(byte[] b, int off) => BitConverter.ToUInt32(b, off);

    [Fact]
    public void BuildSublayerGuids_BaselineAt0_DnsAt16_ExactBytes()
    {
        var buf = P.BuildSublayerGuids(P.SublayerBaseline, P.SublayerDns);

        byte[] baselineLe =
        {
            0xA2, 0x68, 0xE0, 0x21, 0x51, 0x28, 0xC5, 0x43,
            0x8A, 0x29, 0x7A, 0xFE, 0x3F, 0x26, 0x03, 0x84,
        };
        byte[] dnsLe =
        {
            0xB6, 0x41, 0x58, 0xE6, 0xF6, 0x82, 0x55, 0x4D,
            0xBD, 0xE2, 0x61, 0xF8, 0x4D, 0x45, 0x08, 0xD4,
        };

        Assert.Equal(baselineLe, buf[0..16]);
        Assert.Equal(dnsLe, buf[16..32]);
        Assert.Equal(P.SublayerBaseline, new Guid(buf[0..16]));
        Assert.Equal(P.SublayerDns, new Guid(buf[16..32]));
    }

    [Fact]
    public void BuildAddresses_V4Only_TunnelAt0_InternetAt4_V6Zeroed()
    {
        var tun = IPAddress.Parse("10.0.0.5");
        var inet = IPAddress.Parse("83.97.108.34");

        var buf = P.BuildAddresses(tun, inet, null, null);

        Assert.Equal(new byte[] { 10, 0, 0, 5 }, buf[0..4]);
        Assert.Equal(new byte[] { 83, 97, 108, 34 }, buf[4..8]);
        for (int i = 8; i < 40; i++) Assert.Equal(0, buf[i]);
    }

    [Fact]
    public void BuildAddresses_OrderIsTunnelThenInternet_NotSwapped()
    {
        var tun = IPAddress.Parse("1.1.1.1");
        var inet = IPAddress.Parse("2.2.2.2");

        var buf = P.BuildAddresses(tun, inet, null, null);

        Assert.Equal(new byte[] { 1, 1, 1, 1 }, buf[0..4]);
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, buf[4..8]);
    }

    [Fact]
    public void BuildAddresses_V6_TunnelAt8_InternetAt24()
    {
        var tunV6 = IPAddress.Parse("fd00::1");
        var inetV6 = IPAddress.Parse("2001:db8::abcd");

        var buf = P.BuildAddresses(null, null, tunV6, inetV6);

        Assert.Equal(tunV6.GetAddressBytes(), buf[8..24]);
        Assert.Equal(inetV6.GetAddressBytes(), buf[24..40]);
        for (int i = 0; i < 8; i++) Assert.Equal(0, buf[i]);
    }

    [Fact]
    public void BuildAddresses_MismatchedFamily_LeavesSlotZeroed()
    {
        var v6 = IPAddress.Parse("fd00::1");
        var buf = P.BuildAddresses(v6, null, null, null);

        for (int i = 0; i < 4; i++) Assert.Equal(0, buf[i]);
    }

    private const int ConfigHeader = 16;
    private const int ConfigEntry = 16;

    [Fact]
    public void BuildConfiguration_EmptyList_HeaderOnly_ZeroEntries()
    {
        var buf = P.BuildConfiguration(Array.Empty<string>());

        Assert.Equal(ConfigHeader, buf.Length);
        Assert.Equal(0UL, U64(buf, 0));
        Assert.Equal((ulong)ConfigHeader, U64(buf, 8));
    }

    [Fact]
    public void BuildConfiguration_SinglePath_GoldenVector()
    {
        const string path = @"\Device\HarddiskVolume2\curl.exe";
        var wide = Encoding.Unicode.GetBytes(path);

        var buf = P.BuildConfiguration(new[] { path });

        int expectedTotal = ConfigHeader + ConfigEntry + wide.Length;
        Assert.Equal(expectedTotal, buf.Length);

        Assert.Equal(1UL, U64(buf, 0));
        Assert.Equal((ulong)expectedTotal, U64(buf, 8));

        Assert.Equal(0UL, U64(buf, ConfigHeader + 0));
        Assert.Equal((ushort)wide.Length, U16(buf, ConfigHeader + 8));
        for (int i = 10; i < 16; i++) Assert.Equal(0, buf[ConfigHeader + i]);

        int blobBase = ConfigHeader + ConfigEntry;
        Assert.Equal(wide, buf[blobBase..(blobBase + wide.Length)]);
        Assert.Equal(path, Encoding.Unicode.GetString(buf, blobBase, wide.Length));
    }

    [Fact]
    public void BuildConfiguration_ThreePaths_OffsetsAreStringRegionRelative_AndCumulative()
    {
        string[] paths =
        {
            @"\Device\HarddiskVolume2\a.exe",
            @"\Device\HarddiskVolume2\bb.exe",
            @"\Device\HarddiskVolume3\ccc.exe",
        };
        var wide = new byte[3][];
        for (int i = 0; i < 3; i++) wide[i] = Encoding.Unicode.GetBytes(paths[i]);

        var buf = P.BuildConfiguration(paths);

        Assert.Equal(3UL, U64(buf, 0));

        int blobBase = ConfigHeader + ConfigEntry * 3;
        int runningOff = 0;
        for (int i = 0; i < 3; i++)
        {
            int entryOff = ConfigHeader + ConfigEntry * i;
            Assert.Equal((ulong)runningOff, U64(buf, entryOff));
            Assert.Equal((ushort)wide[i].Length, U16(buf, entryOff + 8));
            Assert.Equal(paths[i], Encoding.Unicode.GetString(buf, blobBase + runningOff, wide[i].Length));
            runningOff += wide[i].Length;
        }

        Assert.Equal((ulong)(blobBase + runningOff), U64(buf, 8));
        Assert.Equal(blobBase + runningOff, buf.Length);
    }

    [Fact]
    public void BuildConfiguration_PathOverflowingUshort_ThrowsArgumentException()
    {
        var huge = new string('a', 40000);

        var ex = Assert.Throws<ArgumentException>(() => P.BuildConfiguration(new[] { huge }));
        Assert.Equal("ntPaths", ex.ParamName);
    }

    [Fact]
    public void BuildConfiguration_PathExactlyAtUshortCeiling_DoesNotThrow()
    {
        var maxFit = new string('a', 32767);
        var buf = P.BuildConfiguration(new[] { maxFit });
        Assert.Equal((ushort)65534, U16(buf, ConfigHeader + 8));
    }

    private const int ProcHeader = 16;
    private const int ProcEntry = 32;

    [Fact]
    public void BuildProcessRegistry_SingleEntry_GoldenVector()
    {
        var p = new ProcInfo(Pid: 0x1234, ParentPid: 0x5678, CreationTime: 999,
            DevicePath: @"\Device\HarddiskVolume2\notepad.exe");
        var wide = Encoding.Unicode.GetBytes(p.DevicePath);

        var buf = P.BuildProcessRegistry(new[] { p });

        int expectedTotal = ProcHeader + ProcEntry + wide.Length;
        Assert.Equal(expectedTotal, buf.Length);
        Assert.Equal(1UL, U64(buf, 0));
        Assert.Equal((ulong)expectedTotal, U64(buf, 8));

        Assert.Equal(0x1234UL, U64(buf, ProcHeader + 0));
        Assert.Equal(0x5678UL, U64(buf, ProcHeader + 8));
        Assert.Equal(0UL, U64(buf, ProcHeader + 16));
        Assert.Equal((ushort)wide.Length, U16(buf, ProcHeader + 24));
        for (int i = 26; i < 32; i++) Assert.Equal(0, buf[ProcHeader + i]);

        int blobBase = ProcHeader + ProcEntry;
        Assert.Equal(p.DevicePath, Encoding.Unicode.GetString(buf, blobBase, wide.Length));
    }

    private static List<(uint Pid, uint ParentPid, string DevicePath)> ReverseParseProcessRegistry(byte[] buf)
    {
        var result = new List<(uint, uint, string)>();
        ulong n = U64(buf, 0);
        int blobBase = ProcHeader + ProcEntry * (int)n;
        for (int i = 0; i < (int)n; i++)
        {
            int e = ProcHeader + ProcEntry * i;
            uint pid = (uint)U64(buf, e + 0);
            uint parent = (uint)U64(buf, e + 8);
            ulong off = U64(buf, e + 16);
            ushort len = U16(buf, e + 24);
            string path = len == 0 ? "" : Encoding.Unicode.GetString(buf, blobBase + (int)off, len);
            result.Add((pid, parent, path));
        }
        return result;
    }

    [Fact]
    public void ApplyPidRecycleGuard_ParentNewerThanChild_DropsParent()
    {
        var map = new Dictionary<uint, ProcInfo>
        {
            [10] = new(Pid: 10, ParentPid: 5, CreationTime: 100, DevicePath: "c"),
            [5] = new(Pid: 5, ParentPid: 0, CreationTime: 200, DevicePath: "p"),
        };

        P.ApplyPidRecycleGuard(map);

        Assert.Equal(0u, map[10].ParentPid);
        Assert.Equal(0u, map[5].ParentPid);
    }

    private static byte[] Event(uint id, byte[] body)
    {
        var buf = new byte[16 + body.Length];
        BitConverter.GetBytes(id).CopyTo(buf, 0);
        BitConverter.GetBytes((ulong)body.Length).CopyTo(buf, 8);
        body.CopyTo(buf, 16);
        return buf;
    }

    private static byte[] Wide(string s) => Encoding.Unicode.GetBytes(s);

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void ParseEventBuffer_Splitting_GoldenVector(uint id)
    {
        const string image = @"\Device\HarddiskVolume2\notepad.exe";
        var w = Wide(image);
        var body = new byte[14 + w.Length];
        BitConverter.GetBytes((ulong)0xABCD).CopyTo(body, 0);
        BitConverter.GetBytes((uint)(P.SplittingReason.ByConfig | P.SplittingReason.ProcessArriving)).CopyTo(body, 8);
        BitConverter.GetBytes((ushort)w.Length).CopyTo(body, 12);
        w.CopyTo(body, 14);

        var ev = P.ParseEventBuffer(Event(id, body));

        Assert.Equal(P.SplitTunnelEventKind.Splitting, ev.Kind);
        Assert.Equal((P.EventId)id, ev.Id);
        Assert.Equal(0xABCDUL, ev.Pid);
        Assert.Equal(P.SplittingReason.ByConfig | P.SplittingReason.ProcessArriving, ev.Reason);
        Assert.Equal(image, ev.Image);
    }

    [Theory]
    [InlineData(0x80000001u)]
    [InlineData(0x80000002u)]
    public void ParseEventBuffer_SplittingError_GoldenVector(uint id)
    {
        const string image = @"\Device\HarddiskVolume2\bad.exe";
        var w = Wide(image);
        var body = new byte[10 + w.Length];
        BitConverter.GetBytes((ulong)0x42).CopyTo(body, 0);
        BitConverter.GetBytes((ushort)w.Length).CopyTo(body, 8);
        w.CopyTo(body, 10);

        var ev = P.ParseEventBuffer(Event(id, body));

        Assert.Equal(P.SplitTunnelEventKind.SplittingError, ev.Kind);
        Assert.Equal((P.EventId)id, ev.Id);
        Assert.Equal(0x42UL, ev.Pid);
        Assert.Equal(image, ev.Image);
    }

    [Fact]
    public void ParseEventBuffer_ErrorMessage_GoldenVector()
    {
        const string msg = "callout registration failed";
        var w = Wide(msg);
        var body = new byte[6 + w.Length];
        BitConverter.GetBytes(unchecked((int)0xC0000001)).CopyTo(body, 0);
        BitConverter.GetBytes((ushort)w.Length).CopyTo(body, 4);
        w.CopyTo(body, 6);

        var ev = P.ParseEventBuffer(Event(0x80000003u, body));

        Assert.Equal(P.SplitTunnelEventKind.ErrorMessage, ev.Kind);
        Assert.Equal(P.EventId.ErrorMessage, ev.Id);
        Assert.Equal(unchecked((int)0xC0000001), ev.Status);
        Assert.Equal(msg, ev.Image);
    }

    [Fact]
    public void ParseEventBuffer_UnknownId_ReturnsUnknown_NotThrow()
    {
        var ev = P.ParseEventBuffer(Event(0x12345678u, new byte[16]));
        Assert.Equal(P.SplitTunnelEventKind.Unknown, ev.Kind);
        Assert.Equal(0x12345678u, ev.UnknownId);
    }

    [Fact]
    public void ParseEventBuffer_BufferShorterThanHeader_ReturnsMalformed_NotThrow()
    {
        Assert.Equal(P.SplitTunnelEventKind.Malformed, P.ParseEventBuffer(new byte[15]).Kind);
        Assert.Equal(P.SplitTunnelEventKind.Malformed, P.ParseEventBuffer(ReadOnlySpan<byte>.Empty).Kind);
    }

    [Fact]
    public void ParseEventBuffer_SplittingBodyTruncated_ReturnsMalformed_NotThrow()
    {
        var ev = P.ParseEventBuffer(Event(0u, new byte[10]));
        Assert.Equal(P.SplitTunnelEventKind.Malformed, ev.Kind);
    }

    [Fact]
    public void ParseEventBuffer_ImageLengthBeyondBuffer_ClampsInsteadOfOverrunning()
    {
        var body = new byte[14 + 8];
        BitConverter.GetBytes((ulong)1).CopyTo(body, 0);
        BitConverter.GetBytes((uint)P.SplittingReason.ByConfig).CopyTo(body, 8);
        BitConverter.GetBytes((ushort)200).CopyTo(body, 12);
        Wide("AB").CopyTo(body, 14);

        var ev = P.ParseEventBuffer(Event(0u, body));
        Assert.Equal(P.SplitTunnelEventKind.Splitting, ev.Kind);
        Assert.True(ev.Image.Length <= 4);
    }

    private static string? FakeQueryDosDevice(string drive) => drive.ToUpperInvariant() switch
    {
        "C:" => @"\Device\HarddiskVolume2",
        "D:" => @"\Device\HarddiskVolume5",
        _ => null,
    };

    [Fact]
    public void DosPathToNtPath_NormalPath_PrependsDevicePrefix()
    {
        var nt = P.DosPathToNtPath(@"C:\Program Files\curl.exe", FakeQueryDosDevice);
        Assert.Equal(@"\Device\HarddiskVolume2\Program Files\curl.exe", nt);
    }

    private static NicSnapshot Nic(
        string name, NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool up = true, bool gw = true, string? v4 = "192.168.1.10", string? desc = null)
        => new(name, desc ?? name, type, up, gw, v4 is null ? null : IPAddress.Parse(v4), null);

    [Fact]
    public void PickInternetInterface_SingleCandidate_IsChosen()
    {
        var pick = P.PickInternetInterface(new[] { Nic("Ethernet") });
        Assert.NotNull(pick);
        Assert.Equal("Ethernet", pick!.Value.Name);
    }

    [Fact]
    public void ShouldEngage_WindowsSplitExcludeWithApps_NotOff_True()
    {
        Assert.True(SplitTunnelPolicy.ShouldEngage(
            isWindows: true, routingMode: "split", routingAppsMode: "exclude",
            hasExcludedApps: true, driverSetting: "auto"));
    }

    private static (IPAddress?, IPAddress?, IPAddress?, IPAddress?) Addr(
        string? tunV4, string? inetV4, string? tunV6 = null, string? inetV6 = null)
        => (tunV4 is null ? null : IPAddress.Parse(tunV4),
            inetV4 is null ? null : IPAddress.Parse(inetV4),
            tunV6 is null ? null : IPAddress.Parse(tunV6),
            inetV6 is null ? null : IPAddress.Parse(inetV6));

    [Fact]
    public void ShouldReRegister_V4Changed_True()
    {
        Assert.True(SplitTunnelPolicy.ShouldReRegister(
            Addr("10.0.0.1", "83.97.108.34"),
            Addr("10.0.0.1", "83.97.108.99")));
    }

    private const string Sidecar =
        "10cf25bbcfe51fd663a1fec88a98e9b858f3a579589bb2ec496b66e4fdd1b201  mullvad-split-tunnel.sys\n" +
        "c599926a0327d7ae06b534f4cd039db30392e1897bb9d03e4fec3631744a4e6d  mullvad-split-tunnel.cat\n" +
        "3dd5905e5fb98d61a942a33e8c9a5ba07c3a2de1e4f319e1fec3e54df6591608  mullvad-split-tunnel.inf\n";

    [Fact]
    public void ParseSidecarHashFor_ReturnsHashForListedFile()
        => Assert.Equal(
            "10cf25bbcfe51fd663a1fec88a98e9b858f3a579589bb2ec496b66e4fdd1b201",
            SplitTunnelPolicy.ParseSidecarHashFor(Sidecar, "mullvad-split-tunnel.sys"));
}

