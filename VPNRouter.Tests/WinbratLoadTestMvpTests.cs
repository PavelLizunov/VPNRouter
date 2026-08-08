#nullable enable

using System.Buffers.Binary;
using System.Net;
using VPNRouter.Tools.LoadTest.Protocol;
using VPNRouter.Tools.WinbratLoadGen;

namespace VPNRouter.Tests;

public sealed class WinbratLoadTestMvpTests
{
    private static readonly byte[] KnownSecret = Enumerable.Repeat((byte)7, 16).ToArray();
    private static readonly IPAddress KnownSource = IPAddress.Parse("203.0.113.7");
    private static readonly DateTimeOffset KnownNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Cookie_DifferentSourceOrExpired_IsRejected()
    {
        var auth = new UdpCookieAuthenticator(KnownSecret);
        var cookie = auth.CreateCookie(KnownSource, new byte[8], KnownNow);

        Assert.False(auth.IsValid(IPAddress.Parse("203.0.113.8"), cookie, KnownNow, out _));
        Assert.False(auth.IsValid(KnownSource, cookie, KnownNow.AddSeconds(LoadTestContract.CookieLifetimeSeconds + 1), out var failure));
        Assert.Equal(UdpEchoDisposition.Expired, failure);
    }

    [Fact]
    public void Echo_AuthenticatedRequest_EchoesNoMoreThanRequestAndRejectsReplay()
    {
        var processor = new UdpEchoProcessor(KnownSecret);
        var cookieRequest = new byte[LoadTestContract.CookieRequestBytes];
        cookieRequest[0] = 1;
        cookieRequest[1] = (byte)UdpPacketKind.CookieRequest;
        var issued = processor.Process(KnownSource, cookieRequest, KnownNow, out var cookie);
        var request = CreateEchoRequest(cookie!, 42, new byte[256]);

        var accepted = processor.Process(KnownSource, request, KnownNow, out var response);
        var replay = processor.Process(KnownSource, request, KnownNow, out _);

        Assert.Equal(UdpEchoDisposition.Echo, issued);
        Assert.Equal(UdpEchoDisposition.Echo, accepted);
        Assert.NotNull(response);
        Assert.True(response!.Length <= request.Length);
        Assert.Equal(UdpEchoDisposition.Replay, replay);
    }

    [Fact]
    public void RateLimiter_PerSourceCap_RejectsOneHundredAndFirstPacket()
    {
        var limiter = new FixedRateLimiter();

        for (var i = 0; i < LoadTestContract.PerSourcePacketsPerSecond; i++)
            Assert.True(limiter.TryTake(KnownSource, KnownNow));

        Assert.False(limiter.TryTake(KnownSource, KnownNow));
    }

    [Fact]
    public void RateLimiter_GlobalCap_RejectsPacketsAcrossSources()
    {
        var limiter = new FixedRateLimiter();

        for (var i = 0; i < LoadTestContract.GlobalPacketsPerSecond; i++)
        {
            var source = new IPAddress(new byte[] { 10, 0, (byte)(i / 256), (byte)i });
            Assert.True(limiter.TryTake(source, KnownNow));
        }

        Assert.False(limiter.TryTake(IPAddress.Parse("10.1.1.1"), KnownNow));
    }

    [Fact]
    public void Metrics_SentButUnansweredGap_UsesOutstandingPacketsNotSenderIdle()
    {
        var metrics = new GameUdpMetrics();
        metrics.Sent(1, KnownNow);
        metrics.Sent(2, KnownNow.AddSeconds(1));
        metrics.Received(1, true, KnownNow.AddSeconds(1.1));

        Assert.False(metrics.HasFailureGap(KnownNow.AddSeconds(3.9)));
        Assert.True(metrics.HasFailureGap(KnownNow.AddSeconds(4)));
    }

    [Fact]
    public void Metrics_DuplicateReorderCorruptionAndPercentiles_AreAggregated()
    {
        var metrics = new GameUdpMetrics();
        metrics.Sent(1, KnownNow);
        metrics.Sent(2, KnownNow);
        metrics.Sent(3, KnownNow);
        metrics.Received(2, true, KnownNow.AddMilliseconds(20));
        metrics.Received(1, true, KnownNow.AddMilliseconds(10));
        metrics.Received(2, true, KnownNow.AddMilliseconds(30));
        metrics.Received(3, false, KnownNow.AddMilliseconds(40));

        var summary = metrics.Snapshot();

        Assert.Equal(3, summary.Sent);
        Assert.Equal(2, summary.Received);
        Assert.Equal(1, summary.Loss);
        Assert.Equal(1, summary.Duplicate);
        Assert.Equal(1, summary.Reorder);
        Assert.Equal(1, summary.Corruption);
        Assert.Equal(20, summary.RttP95Ms);
    }

    [Fact]
    public void BrowserPage_FixedBurstAndWebSocketCaps_ArePresent()
    {
        var target = ReadRepoFile("VPNRouter.Tools", "LoadTarget", "Program.cs");

        Assert.Contains("Array(32)", target, StringComparison.Ordinal);
        Assert.Contains("byteLength===65536", target, StringComparison.Ordinal);
        Assert.Contains("setInterval(burst,5000)", target, StringComparison.Ordinal);
        Assert.Contains("for(let i=0;i<4;i++)", target, StringComparison.Ordinal);
        Assert.Contains("new Uint8Array(64)", target, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooling_FixedProfilesPreserveConfigAndEvidenceAllowlist()
    {
        var verifier = ReadRepoFile("tools", "brat-verify.ps1");
        var coordinator = ReadRepoFile("tools", "brat-loadtest.ps1");
        var target = ReadRepoFile("VPNRouter.Tools", "LoadTarget", "Program.cs");
        var loadAction = Slice(verifier, "    'loadtest' {", "    'lifecycle' {");

        Assert.Contains("'loadtest'", verifier, StringComparison.Ordinal);
        Assert.Contains("loadtest.vpn.ninitux.com", loadAction, StringComparison.Ordinal);
        Assert.Contains("RouteScope", loadAction, StringComparison.Ordinal);
        Assert.Contains("Status = 'BLOCKED'", loadAction, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", loadAction, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Vpn", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SaveSettings", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-PSSession", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Command", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Profile =", coordinator, StringComparison.Ordinal);
        Assert.Contains("Metrics =", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Token =", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Target =", coordinator, StringComparison.Ordinal);
        Assert.Contains("response.Length <= packet.Buffer.Length", target, StringComparison.Ordinal);
    }

    private static byte[] CreateEchoRequest(byte[] cookie, long sequence, byte[] payload)
    {
        var request = new byte[LoadTestContract.EchoHeaderBytes + payload.Length];
        request[0] = 1;
        request[1] = (byte)UdpPacketKind.EchoRequest;
        cookie.CopyTo(request, 2);
        BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(36, 8), sequence);
        payload.CopyTo(request, LoadTestContract.EchoHeaderBytes);
        return request;
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(new[] { directory!.FullName }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Repository file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }
}
