#nullable enable
using System.Collections.Generic;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConnectionHealthClassifierTests
{
    private const string ProxyEp = "198.51.100.7:443";
    private static IReadOnlySet<string> Proxy => new HashSet<string> { ProxyEp };

    private const string Eof =
        "+0300 2026-06-19 15:50:54 ERROR [810041638 108ms] connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: EOF";
    private const string DialTimeout =
        "+0300 2026-06-19 15:59:13 ERROR [4204748908 5.0s] connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: dial tcp 198.51.100.7:443: i/o timeout";
    private const string OpenResetToProxy =
        "+0300 2026-06-19 21:11:36 ERROR [3010744970 2m37s] connection: open connection to 203.0.113.11:21114 using outbound/vless[proxy]: read tcp 192.0.2.5:53770->198.51.100.7:443: wsarecv: An existing connection was forcibly closed by the remote host.";
    private const string UdpRelayEof =
        "+0300 2026-06-19 20:10:00 ERROR [99 1ms] connection: listen packet connection using  using outbound/vless[proxy]: EOF";
    private const string UploadRawRead =
        "+0300 2026-06-19 15:56:18 ERROR [2130031130 26m27s] connection: connection upload closed: raw read: An existing connection was forcibly closed by the remote host.";
    private const string UploadRawReadTuple =
        "+0300 2026-06-19 16:01:58 ERROR [469167270 197ms] connection: connection upload closed: raw-read tcp4 172.19.0.1:52865->172.19.0.2:21134: An existing connection was forcibly closed by the remote host.";
    private const string DownloadStreamBreakToProxy =
        "+0300 2026-06-19 21:09:57 ERROR [2247227500 9.86s] connection: connection download closed: read tcp 192.0.2.5:53800->198.51.100.7:443: wsarecv: An existing connection was forcibly closed by the remote host.";
    private const string Attempt =
        "+0300 2026-06-19 15:50:48 INFO [835920449 0ms] outbound/vless[proxy]: outbound connection to 203.0.113.10:21115";
    private const string StreamBreakNonProxy =
        "+0300 2026-06-19 18:00:00 ERROR [555 12ms] connection: connection download closed: read tcp 192.0.2.5:5->203.0.113.99:443: wsarecv: An existing connection was forcibly closed by the remote host.";

    private static ConnLogEvent C(string line, IReadOnlySet<string>? eps = null)
        => ConnectionHealthClassifier.Classify(line, eps)!;

    [Fact]
    public void RelayOpenEof_IsRelayOpenFail_KindEof()
    {
        var ev = C(Eof, Proxy);
        Assert.Equal(ConnHealthCategory.RelayOpenFail, ev.Category);
        Assert.Equal(RelayFailKind.Eof, ev.FailKind);
    }

    [Fact]
    public void RelayOpenEof_ParsesIdTagDestDuration()
    {
        var ev = C(Eof, Proxy);
        Assert.Equal("810041638", ev.ConnId);
        Assert.Equal("proxy", ev.OutboundTag);
        Assert.Equal("203.0.113.10:21115", ev.Destination);
        Assert.Equal("108ms", ev.DurationRaw);
    }

    [Theory]
    [InlineData(UploadRawRead)]
    [InlineData(UploadRawReadTuple)]
    public void UploadClosedRawRead_IsLocalClose(string line)
        => Assert.Equal(ConnHealthCategory.LocalClose, C(line, Proxy).Category);

    [Fact]
    public void OutboundConnectionTo_IsRelayOpenAttempt()
        => Assert.Equal(ConnHealthCategory.RelayOpenAttempt, C(Attempt, Proxy).Category);

    [Theory]
    [InlineData("[810041638 108ms] connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: EOF")]
    [InlineData("connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: EOF")]
    public void RelayOpenEof_PrefixAgnostic(string line)
        => Assert.Equal(ConnHealthCategory.RelayOpenFail, C(line, Proxy).Category);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+0300 2026-06-19 01:34:57 INFO [123 4ms] dns: exchanged A example.com. 14 IN A 203.0.113.5")]
    [InlineData("+0300 2026-06-19 01:34:57 INFO router: loaded geoip database")]
    public void NonConnectionLines_ReturnNull(string? line)
        => Assert.Null(ConnectionHealthClassifier.Classify(line!, Proxy));
}
