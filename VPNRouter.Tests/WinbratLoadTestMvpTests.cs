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
    public void Contract_UsesDedicatedUdpPort() => Assert.Equal(19000, LoadTestContract.UdpPort);

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
    public void Echo_ExpiredCookieReplayState_AllowsSameSequenceForRenewedCookie()
    {
        var processor = new UdpEchoProcessor(KnownSecret);
        var cookieRequest = new byte[LoadTestContract.CookieRequestBytes];
        cookieRequest[0] = 1;
        cookieRequest[1] = (byte)UdpPacketKind.CookieRequest;
        var issued = processor.Process(KnownSource, cookieRequest, KnownNow, out var firstCookie);
        var first = processor.Process(KnownSource, CreateEchoRequest(firstCookie!, 1, new byte[256]), KnownNow, out _);
        var renewedAt = KnownNow.AddSeconds(LoadTestContract.CookieLifetimeSeconds + 1);
        var renewed = processor.Process(KnownSource, cookieRequest, renewedAt, out var secondCookie);
        var accepted = processor.Process(KnownSource, CreateEchoRequest(secondCookie!, 1, new byte[256]), renewedAt, out _);

        Assert.Equal(UdpEchoDisposition.Echo, issued);
        Assert.Equal(UdpEchoDisposition.Echo, first);
        Assert.Equal(UdpEchoDisposition.Echo, renewed);
        Assert.Equal(UdpEchoDisposition.Echo, accepted);
        Assert.Equal(1, processor.ReplayEntryCount);
        Assert.True(processor.ReplayEntryCount <= LoadTestContract.MaxReplayEntries);
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
    public void RateLimiter_ConcurrentRequests_NeverExceedPerSourceCap()
    {
        var limiter = new FixedRateLimiter();
        var accepted = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (limiter.TryTake(KnownSource, KnownNow)) Interlocked.Increment(ref accepted);
        });

        Assert.True(accepted <= LoadTestContract.PerSourcePacketsPerSecond);
    }

    [Fact]
    public void CookieRotation_AfterOriginalExpiry_UsesRenewedCookie()
    {
        var auth = new UdpCookieAuthenticator(KnownSecret);
        var state = new GameUdpCookieState(auth.CreateCookie(KnownSource, new byte[8], KnownNow));

        Assert.True(state.TryBeginRefresh(KnownNow.AddSeconds(25)));
        Assert.True(state.Accept(auth.CreateCookie(KnownSource, new byte[8], KnownNow.AddSeconds(25))));

        Assert.False(state.TryBeginRefresh(KnownNow.AddSeconds(LoadTestContract.CookieLifetimeSeconds + 1)));
    }

    [Fact]
    public void CookieRefresh_RetriesAfterOneSecondAndRejectsStaleCookie()
    {
        var auth = new UdpCookieAuthenticator(KnownSecret);
        var initial = auth.CreateCookie(KnownSource, new byte[8], KnownNow);
        var state = new GameUdpCookieState(initial);

        Assert.True(state.TryBeginRefresh(KnownNow.AddSeconds(25)));
        Assert.False(state.TryBeginRefresh(KnownNow.AddSeconds(25.5)));
        Assert.True(state.TryBeginRefresh(KnownNow.AddSeconds(26)));
        Assert.True(state.Accept(auth.CreateCookie(KnownSource, new byte[8], KnownNow.AddSeconds(25))));
        Assert.False(state.Accept(initial));
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
        Assert.True(GameUdpMvp.PayloadFor(2).SequenceEqual(GameUdpMvp.PayloadFor(2)));
        Assert.False(GameUdpMvp.PayloadFor(1).SequenceEqual(GameUdpMvp.PayloadFor(2)));
    }

    [Fact]
    public void Metrics_ContinuousAcknowledgements_DoNotTurnOneOldLossIntoBlackout()
    {
        var metrics = new GameUdpMetrics();
        metrics.Sent(1, KnownNow);
        metrics.Sent(2, KnownNow.AddSeconds(1));
        metrics.Received(2, true, KnownNow.AddSeconds(1.1));
        metrics.Sent(3, KnownNow.AddSeconds(3.9));
        metrics.Received(3, true, KnownNow.AddSeconds(4));

        Assert.False(metrics.HasFailureGap(KnownNow.AddSeconds(4.1)));
        metrics.Sent(4, KnownNow.AddSeconds(7.1));
        Assert.True(metrics.HasFailureGap(KnownNow.AddSeconds(7.1)));
    }

    [Fact]
    public void Metrics_UnknownResponse_DoesNotAcknowledgeUnsentSequence()
    {
        var metrics = new GameUdpMetrics();
        metrics.Sent(1, KnownNow);
        metrics.Received(99, true, KnownNow.AddMilliseconds(10));

        var summary = metrics.Snapshot();

        Assert.Equal(0, summary.Received);
        Assert.Equal(1, summary.Loss);
        Assert.Equal(1, summary.Unknown);
    }

    [Fact]
    public void Metrics_ConcurrentSendAndReceive_DoNotCorruptAggregateState()
    {
        var metrics = new GameUdpMetrics();

        Parallel.Invoke(
            () => Parallel.For(0, 1_000, sequence => metrics.Sent(sequence, KnownNow)),
            () => Parallel.For(0, 1_000, sequence => metrics.Received(sequence, true, KnownNow.AddMilliseconds(1))));

        var summary = metrics.Snapshot();
        Assert.Equal(1_000, summary.Sent);
        Assert.InRange(summary.Received, 0, summary.Sent);
    }

    [Fact]
    public void Profile_Scheduling_UsesFixedNormalAndBurstIntervals()
    {
        Assert.Equal(GameUdpProfile.NormalInterval, GameUdpProfile.IntervalAt(TimeSpan.Zero));
        Assert.Equal(GameUdpProfile.BurstInterval, GameUdpProfile.IntervalAt(GameUdpProfile.BurstStart));
        Assert.Equal(GameUdpProfile.NormalInterval, GameUdpProfile.IntervalAt(GameUdpProfile.BurstStart + GameUdpProfile.BurstDuration));
    }

    [Fact]
    public void GameUdp_FinalDrainDoesNotRunADeadFailureGapCheck()
    {
        var loadGenerator = ReadRepoFile("VPNRouter.Tools", "WinbratLoadGen", "Program.cs");

        Assert.DoesNotContain("metrics.HasFailureGap(DateTimeOffset.UtcNow)", loadGenerator, StringComparison.Ordinal);
    }

    [Fact]
    public void GameUdp_ControlledFailure_PreservesAggregateWithoutRawExceptionText()
    {
        var metrics = new GameUdpMetrics();
        metrics.Sent(1, KnownNow);

        var summary = metrics.Snapshot("ReplyGap");
        Assert.Equal("ReplyGap", summary.Status);
        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.Loss);

        var loadGenerator = ReadRepoFile("VPNRouter.Tools", "WinbratLoadGen", "Program.cs");
        Assert.Contains("status = \"ReplyGap\"", loadGenerator, StringComparison.Ordinal);
        Assert.Contains("metrics.Snapshot(\"InternalFailure\")", loadGenerator, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Error", loadGenerator, StringComparison.Ordinal);
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
        Assert.Contains("if(busy||stopped)return", target, StringComparison.Ordinal);
        Assert.Contains("setTimeout(stop,600000)", target, StringComparison.Ordinal);
        Assert.Contains("sockets.forEach(ws=>ws.close())", target, StringComparison.Ordinal);
        Assert.Contains("aborter.abort()", target, StringComparison.Ordinal);
        Assert.Contains("signal:aborter.signal", target, StringComparison.Ordinal);
        Assert.Contains("if(!stopped)state.fetchFail++", target, StringComparison.Ordinal);
        Assert.Contains("state.done=true", target, StringComparison.Ordinal);
        Assert.Contains("ws.onclose=()=>{if(!stopped){state.wsFail++;show()}}", target, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_HttpAndWebSocketShareFixedRateGate()
    {
        var target = ReadRepoFile("VPNRouter.Tools", "LoadTarget", "Program.cs");

        Assert.Contains("AddSingleton<FixedRateLimiter>", target, StringComparison.Ordinal);
        Assert.Contains("Results.StatusCode(429)", target, StringComparison.Ordinal);
        Assert.Contains("WebSocketCloseStatus.PolicyViolation", target, StringComparison.Ordinal);
        Assert.Contains("RateLimited(context, rate)", target, StringComparison.Ordinal);
        Assert.Contains("session.CancelAfter(TimeSpan.FromMinutes(10))", target, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooling_FixedProfilesPreserveConfigAndEvidenceAllowlist()
    {
        var verifier = ReadRepoFile("tools", "brat-verify.ps1");
        var coordinator = ReadRepoFile("tools", "brat-loadtest.ps1");
        var target = ReadRepoFile("VPNRouter.Tools", "LoadTarget", "Program.cs");
        var payloadBuilder = ReadRepoFile("tools", "build-winbrat-loadtest-payload.ps1");
        var loadAction = Slice(verifier, "    'loadtest' {", "    'lifecycle' {");

        Assert.Contains("'loadtest'", verifier, StringComparison.Ordinal);
        Assert.Contains("loadtest.vpn.ninitux.com", loadAction, StringComparison.Ordinal);
        Assert.Contains("RouteScope", loadAction, StringComparison.Ordinal);
        Assert.Contains("Status = if ($passed) { 'PASS' } else { 'FAIL' }", loadAction, StringComparison.Ordinal);
        Assert.Contains("Test-ApprovedWinbratLoadPayload", verifier, StringComparison.Ordinal);
        Assert.Contains("$ApprovedWinbratLoadPayloadSha256 = @(", verifier, StringComparison.Ordinal);
        Assert.Contains("5855167c4c89efa5c5adbd0933ee4269382785bb35d6b04f7a5fd27d80f72934", verifier, StringComparison.Ordinal);
        Assert.Contains("PayloadNotApproved", loadAction, StringComparison.Ordinal);
        Assert.Contains("EndpointUnavailable", loadAction, StringComparison.Ordinal);
        Assert.Contains("MeasurementGated", loadAction, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $archive", loadAction, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256 -LiteralPath $zip", loadAction, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(330000)", loadAction, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit()", loadAction, StringComparison.Ordinal);
        Assert.Contains("PayloadExitNonZero", loadAction, StringComparison.Ordinal);
        Assert.Contains("PayloadOutputMissing", loadAction, StringComparison.Ordinal);
        Assert.Contains("PayloadOutputEmpty", loadAction, StringComparison.Ordinal);
        Assert.Contains("Get-NetAdapterStatistics -Name 'VPNRouter-TUN'", loadAction, StringComparison.Ordinal);
        Assert.Contains("FullTunnel = $true", loadAction, StringComparison.Ordinal);
        Assert.Contains("TunCorrelation", loadAction, StringComparison.Ordinal);
        Assert.Contains("$tunCorrelation = $false", loadAction, StringComparison.Ordinal);
        Assert.Contains("if ([bool]$result.Success)", loadAction, StringComparison.Ordinal);
        Assert.Contains("$candidate -in @('PayloadHashMismatch'", loadAction, StringComparison.Ordinal);
        Assert.Contains("PayloadStatus = [string]$metrics.Status", loadAction, StringComparison.Ordinal);
        Assert.Contains("'Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure', 'InternalFailure'", loadAction, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", payloadBuilder, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", payloadBuilder, StringComparison.Ordinal);
        Assert.True(loadAction.IndexOf("Add-Type -AssemblyName System.Net.Http", StringComparison.Ordinal) < loadAction.IndexOf("New-Object System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.DoesNotContain("Set-Vpn", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SaveSettings", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-PSSession", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Command", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Profile =", coordinator, StringComparison.Ordinal);
        Assert.Contains("Metrics =", coordinator, StringComparison.Ordinal);
        Assert.Contains("Verifier returned an invalid lifecycle enum", coordinator, StringComparison.Ordinal);
        Assert.Contains("FullTunnel", coordinator, StringComparison.Ordinal);
        Assert.Contains("TunCorrelation", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Token =", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Target =", coordinator, StringComparison.Ordinal);
        Assert.Contains("response.Length <= packet.Buffer.Length", target, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooling_ProtocolSelection_UsesOnlySafeClassNamesAndDisconnectedSubList()
    {
        var verifier = ReadRepoFile("tools", "brat-verify.ps1");
        var operation = Slice(verifier, "            'SelectProtocol' {", "            'ScrollIntoView' {");

        Assert.Contains("$target.Current.AutomationId -ne 'SubList'", operation, StringComparison.Ordinal);
        Assert.Contains("SelectProtocol requires a disconnected VPNRouter-TUN state", operation, StringComparison.Ordinal);
        Assert.Contains("VlessReality", operation, StringComparison.Ordinal);
        Assert.Contains("Hysteria2", operation, StringComparison.Ordinal);
        Assert.Contains("AmneziaWG", operation, StringComparison.Ordinal);
        Assert.Contains("TreeWalker]::ControlViewWalker.GetParent", operation, StringComparison.Ordinal);
        Assert.Contains("SelectionItemPattern", operation, StringComparison.Ordinal);
        Assert.Contains("SendWait('{HOME}')", operation, StringComparison.Ordinal);
        Assert.Contains("SendWait('{PGDN}')", operation, StringComparison.Ordinal);
        Assert.Contains("$originalSelection", operation, StringComparison.Ordinal);
        Assert.Contains("$selectionMutationAttempted = $true", operation, StringComparison.Ordinal);
        Assert.Contains("$restore.Select()", operation, StringComparison.Ordinal);
        Assert.Contains("$remove.RemoveFromSelection()", operation, StringComparison.Ordinal);
        Assert.Contains("original empty selection restoration did not stick", operation, StringComparison.Ordinal);
        Assert.Contains("ProtocolClass = [string]$req.ProtocolClass", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("$row.Current.Name", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("SendWait([string]$req", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerViewModel", operation, StringComparison.Ordinal);
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
