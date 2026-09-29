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
    public void BuildProcessRegistry_EmptyList_HeaderOnly()
    {
        var buf = P.BuildProcessRegistry(Array.Empty<ProcInfo>());
        Assert.Equal(ProcHeader, buf.Length);
        Assert.Equal(0UL, U64(buf, 0));
        Assert.Equal((ulong)ProcHeader, U64(buf, 8));
    }

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

    [Fact]
    public void BuildProcessRegistry_EmptyPath_OffsetAndLenAreZero()
    {
        var p = new ProcInfo(Pid: 7, ParentPid: 0, CreationTime: 0, DevicePath: "");
        var buf = P.BuildProcessRegistry(new[] { p });

        Assert.Equal(ProcHeader + ProcEntry, buf.Length);
        Assert.Equal(7UL, U64(buf, ProcHeader + 0));
        Assert.Equal(0UL, U64(buf, ProcHeader + 16));
        Assert.Equal((ushort)0, U16(buf, ProcHeader + 24));
    }

    [Fact]
    public void BuildProcessRegistry_MixedList_RoundTripsThroughReverseParser()
    {
        var procs = new List<ProcInfo>
        {
            new(Pid: 100, ParentPid: 4, CreationTime: 10, DevicePath: @"\Device\HarddiskVolume2\a.exe"),
            new(Pid: 200, ParentPid: 0, CreationTime: 20, DevicePath: ""),
            new(Pid: 300, ParentPid: 100, CreationTime: 30, DevicePath: @"\Device\HarddiskVolume2\c.exe"),
        };

        var buf = P.BuildProcessRegistry(procs);
        var parsed = ReverseParseProcessRegistry(buf);

        Assert.Equal(procs.Count, parsed.Count);
        for (int i = 0; i < procs.Count; i++)
        {
            Assert.Equal(procs[i].Pid, parsed[i].Pid);
            Assert.Equal(procs[i].ParentPid, parsed[i].ParentPid);
            Assert.Equal(procs[i].DevicePath, parsed[i].DevicePath);
        }
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

    [Fact]
    public void ApplyPidRecycleGuard_NormalPair_Untouched()
    {
        var map = new Dictionary<uint, ProcInfo>
        {
            [10] = new(Pid: 10, ParentPid: 5, CreationTime: 200, DevicePath: "c"),
            [5] = new(Pid: 5, ParentPid: 0, CreationTime: 100, DevicePath: "p"),
        };

        P.ApplyPidRecycleGuard(map);

        Assert.Equal(5u, map[10].ParentPid);
    }

    [Fact]
    public void ApplyPidRecycleGuard_ParentNotInMap_Untouched()
    {
        var map = new Dictionary<uint, ProcInfo>
        {
            [10] = new(Pid: 10, ParentPid: 999, CreationTime: 100, DevicePath: "c"),
        };

        P.ApplyPidRecycleGuard(map);

        Assert.Equal(999u, map[10].ParentPid);
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

    [Fact]
    public void DosPathToNtPath_LowercaseDrive_StillResolves()
    {
        var nt = P.DosPathToNtPath(@"c:\dir\app.exe", FakeQueryDosDevice);
        Assert.Equal(@"\Device\HarddiskVolume2\dir\app.exe", nt);
    }

    [Fact]
    public void DosPathToNtPath_NoLeadingBackslashAfterDrive_InsertsOne()
    {
        var nt = P.DosPathToNtPath(@"C:app.exe", FakeQueryDosDevice);
        Assert.Equal(@"\Device\HarddiskVolume2\app.exe", nt);
    }

    [Fact]
    public void DosPathToNtPath_SecondDrive_UsesItsPrefix()
    {
        var nt = P.DosPathToNtPath(@"D:\games\game.exe", FakeQueryDosDevice);
        Assert.Equal(@"\Device\HarddiskVolume5\games\game.exe", nt);
    }

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"\Device\HarddiskVolume2\already-nt.exe")]
    [InlineData("relative\\path.exe")]
    [InlineData("")]
    [InlineData("C")]
    [InlineData("1:\\bad.exe")]
    public void DosPathToNtPath_NonDrivePath_ReturnsNull(string dos)
    {
        Assert.Null(P.DosPathToNtPath(dos, FakeQueryDosDevice));
    }

    [Fact]
    public void DosPathToNtPath_ResolverReturnsNull_ReturnsNull()
    {
        Assert.Null(P.DosPathToNtPath(@"Z:\dir\app.exe", FakeQueryDosDevice));
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
    public void PickInternetInterface_OwnTunFilteredByWgName()
    {
        var nics = new[]
        {
            Nic("VPNRouter-TUN", desc: "WireGuard Tunnel"),
            Nic("Ethernet"),
        };
        var pick = P.PickInternetInterface(nics);
        Assert.Equal("Ethernet", pick!.Value.Name);
    }

    [Theory]
    [InlineData("WireGuard Tunnel")]
    [InlineData("AmneziaWG Adapter")]
    [InlineData("Tailscale Tunnel")]
    public void PickInternetInterface_WgAwgTailscale_Excluded(string desc)
    {
        var nics = new[]
        {
            Nic("vpn0", desc: desc),
            Nic("Ethernet"),
        };
        var pick = P.PickInternetInterface(nics);
        Assert.Equal("Ethernet", pick!.Value.Name);
    }

    [Fact]
    public void PickInternetInterface_PrefersEthernetOverWifi()
    {
        var nics = new[]
        {
            Nic("Wi-Fi", NetworkInterfaceType.Wireless80211, v4: "192.168.1.20"),
            Nic("Ethernet", NetworkInterfaceType.Ethernet, v4: "192.168.1.10"),
        };
        var pick = P.PickInternetInterface(nics);
        Assert.Equal("Ethernet", pick!.Value.Name);
    }

    [Fact]
    public void PickInternetInterface_OnlyWifi_ChoosesWifi()
    {
        var pick = P.PickInternetInterface(new[] { Nic("Wi-Fi", NetworkInterfaceType.Wireless80211) });
        Assert.Equal("Wi-Fi", pick!.Value.Name);
    }

    [Fact]
    public void PickInternetInterface_SkipsDown_NoGateway_NoV4()
    {
        var nics = new[]
        {
            Nic("Down", up: false),
            Nic("NoGw", gw: false),
            Nic("NoV4", v4: null),
            Nic("Good"),
        };
        var pick = P.PickInternetInterface(nics);
        Assert.Equal("Good", pick!.Value.Name);
    }

    [Fact]
    public void PickInternetInterface_NoCandidates_ReturnsNull()
    {
        Assert.Null(P.PickInternetInterface(Array.Empty<NicSnapshot>()));
        Assert.Null(P.PickInternetInterface(new[] { Nic("Down", up: false) }));
    }

    [Fact]
    public void PickInternetInterface_TwoEthernet_DeterministicByName()
    {
        var forward = new[] { Nic("eth1", v4: "10.0.0.2"), Nic("eth0", v4: "10.0.0.1") };
        var reverse = new[] { Nic("eth0", v4: "10.0.0.1"), Nic("eth1", v4: "10.0.0.2") };

        Assert.Equal("eth0", P.PickInternetInterface(forward)!.Value.Name);
        Assert.Equal("eth0", P.PickInternetInterface(reverse)!.Value.Name);
    }

    [Fact]
    public void ShouldEngage_WindowsSplitExcludeWithApps_NotOff_True()
    {
        Assert.True(SplitTunnelPolicy.ShouldEngage(
            isWindows: true, routingMode: "split", routingAppsMode: "exclude",
            hasExcludedApps: true, driverSetting: "auto"));
    }

    [Theory]
    [InlineData(false, "split", "exclude", true, "auto")]
    [InlineData(true, "full", "exclude", true, "auto")]
    [InlineData(true, "split", "include", true, "auto")]
    [InlineData(true, "split", "exclude", false, "auto")]
    [InlineData(true, "split", "exclude", true, "off")]
    public void ShouldEngage_NonQualifying_False(
        bool win, string mode, string appsMode, bool hasApps, string driver)
    {
        Assert.False(SplitTunnelPolicy.ShouldEngage(win, mode, appsMode, hasApps, driver));
    }

    [Fact]
    public void ShouldEngage_CaseInsensitive_ExcludeOnly_AliasesRejected()
    {
        Assert.True(SplitTunnelPolicy.ShouldEngage(true, "SPLIT", "Exclude", true, "AUTO"));
        Assert.False(SplitTunnelPolicy.ShouldEngage(true, "split", "exclude", true, "OFF"));
        Assert.False(SplitTunnelPolicy.ShouldEngage(true, "split", "exclude-apps", true, "auto"));
        Assert.False(SplitTunnelPolicy.ShouldEngage(true, "split", "excludeapps", true, "auto"));
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

    [Fact]
    public void ShouldReRegister_Identical_False()
    {
        Assert.False(SplitTunnelPolicy.ShouldReRegister(
            Addr("10.0.0.1", "83.97.108.34"),
            Addr("10.0.0.1", "83.97.108.34")));
    }

    [Fact]
    public void ShouldReRegister_V6Appeared_True()
    {
        Assert.True(SplitTunnelPolicy.ShouldReRegister(
            Addr("10.0.0.1", "83.97.108.34"),
            Addr("10.0.0.1", "83.97.108.34", inetV6: "2001:db8::1")));
    }

    [Fact]
    public void ShouldReRegister_BothNullTuples_False()
    {
        Assert.False(SplitTunnelPolicy.ShouldReRegister(
            Addr(null, null), Addr(null, null)));
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

    [Fact]
    public void ParseSidecarHashFor_MatchesRegardlessOfFilenameCase()
        => Assert.Equal(
            "c599926a0327d7ae06b534f4cd039db30392e1897bb9d03e4fec3631744a4e6d",
            SplitTunnelPolicy.ParseSidecarHashFor(Sidecar, "MULLVAD-SPLIT-TUNNEL.CAT"));

    [Fact]
    public void ParseSidecarHashFor_HandlesBinaryStarMarkerAndPathPrefix()
        => Assert.Equal(
            "aa11bb22cc33dd44ee55ff66007788990011223344556677889900aabbccddee",
            SplitTunnelPolicy.ParseSidecarHashFor(
                "aa11bb22cc33dd44ee55ff66007788990011223344556677889900aabbccddee *./driver/mullvad-split-tunnel.sys",
                "mullvad-split-tunnel.sys"));

    [Theory]
    [InlineData("not-listed.sys")]
    [InlineData("")]
    public void ParseSidecarHashFor_ReturnsNullForMissingOrEmpty(string fileName)
        => Assert.Null(SplitTunnelPolicy.ParseSidecarHashFor(Sidecar, fileName));

    [Fact]
    public void ParseSidecarHashFor_NullSidecar_ReturnsNull()
        => Assert.Null(SplitTunnelPolicy.ParseSidecarHashFor(null, "mullvad-split-tunnel.sys"));

    [Fact]
    public void ParseSidecarHashFor_RejectsNon64HexOrJunkLines()
        => Assert.Null(SplitTunnelPolicy.ParseSidecarHashFor(
            "deadbeef  mullvad-split-tunnel.sys\n# a comment\ngarbage line no hash\n",
            "mullvad-split-tunnel.sys"));
}

