using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class BusinessNodeMetadataTests
{
    [Fact]
    public async Task ProducerCanonicalEndpointFixture_IsAcceptedByTheActualHttpConsumer()
    {
        using var stream = typeof(BusinessNodeMetadataTests).Assembly.GetManifestResourceStream(
            "VPNRouter.Tests.Fixtures.BusinessNodeCanonicalEndpointSnapshot.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(json))));
        using var client = new BusinessNodeMetadataClient(http);
        var snapshot = await client.FetchAsync(new("https://example.invalid/node-status/synthetic"), CancellationToken.None);

        var row = Assert.Single(snapshot.Nodes).Value;
        Assert.Equal("node.example.invalid", row.EndpointHost);
        Assert.True(snapshot.TryGetNode("NODE.EXAMPLE.INVALID.", out var matched));
        Assert.Same(row, matched);
        Assert.Null(row.CountryIso);
        Assert.Null(row.MemoryUsedPercent);
        Assert.Null(row.Load1MinX100);
        Assert.Null(snapshot.GetAgeSeconds(row));
        Assert.Equal(BusinessNodeFreshness.Unknown, snapshot.GetFreshness(row));
    }

    private static string Snapshot(string? rows = null, string generated = "2000", string ttl = "1320") =>
        $$"""{"schema":1,"generated_at":{{generated}},"max_age_seconds":{{ttl}},"nodes":[{{rows ?? Row()}}]}""";

    private static string Row(string host = "node.example.invalid", string country = "\"fi\"",
        string total = "1024", string available = "512", string load = "125", string observed = "1900",
        string age = "100", string freshness = "fresh") =>
        string.Concat("{\"endpoint_host\":", JsonSerializer.Serialize(host), ",\"country_iso\":", country,
            ",\"memory_total_mib\":", total, ",\"memory_available_mib\":", available, ",\"load_1min_x100\":", load,
            ",\"observed_at\":", observed, ",\"age_seconds\":", age, ",\"freshness\":", JsonSerializer.Serialize(freshness), "}");

    [Fact]
    public void Parse_UsesExplicitCountry_AndPreservesUnknownCountryForOpaqueProviderLabels()
    {
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot(Row("known.example.invalid") + "," +
            Row("opaque-provider.example.invalid", country: "null")));

        Assert.Equal("FI", snapshot.Nodes["KNOWN.EXAMPLE.INVALID"].CountryIso);
        Assert.Null(snapshot.Nodes["opaque-provider.example.invalid"].CountryIso);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, BusinessNodeMetadata>>(snapshot.Nodes);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, BusinessNodeMetadata>)snapshot.Nodes)
            .Add("another.example.invalid", snapshot.Nodes.Values.First()));
    }

    [Theory]
    [InlineData("1024", "512", 50.0)]
    [InlineData("1024", "0", 100.0)]
    [InlineData("1024", "1024", 0.0)]
    [InlineData("0", "0", null)]
    [InlineData("-1", "0", null)]
    [InlineData("1024", "-1", null)]
    [InlineData("1024", "1025", null)]
    [InlineData("null", "512", null)]
    [InlineData("1024", "null", null)]
    public void Parse_RamPercentageRequiresAValidPair_AndZeroIsARealSample(string total, string available, double? used)
    {
        var row = Assert.Single(BusinessNodeSnapshot.Parse(Snapshot(Row(total: total, available: available, load: "0"))).Nodes).Value;

        Assert.Equal(used, row.MemoryUsedPercent);
        Assert.Equal<uint?>(0, row.Load1MinX100);
        if (available == "0") Assert.Equal<long?>(0, row.MemoryAvailableMib);
    }

    [Fact]
    public void Freshness_ExactTtlBoundaryUsesMonotonicTime()
    {
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot());
        var row = Assert.Single(snapshot.Nodes).Value;
        var boundary = snapshot.ReceivedTimestamp + 1220 * Stopwatch.Frequency;
        Assert.Equal<ulong?>(1320, snapshot.GetAgeSeconds(row, boundary));
        Assert.Equal(BusinessNodeFreshness.Fresh, snapshot.GetFreshness(row, boundary));
        Assert.Equal<ulong?>(1321, snapshot.GetAgeSeconds(row, boundary + Stopwatch.Frequency));
        Assert.Equal(BusinessNodeFreshness.Stale, snapshot.GetFreshness(row, boundary + Stopwatch.Frequency));
    }

    [Fact]
    public void Parse_NullAndMissingSamplesStayUnknown_RatherThanBecomingZero()
    {
        var row = Row(total: "null", available: "null", load: "null", observed: "null", age: "null", freshness: "unknown")
            .Replace("\"memory_total_mib\":null,", "").Replace("\"load_1min_x100\":null,", "");
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot(row));
        var node = Assert.Single(snapshot.Nodes).Value;

        Assert.Null(node.MemoryTotalMib);
        Assert.Null(node.MemoryAvailableMib);
        Assert.Null(node.Load1MinX100);
        Assert.Null(node.MemoryUsedPercent);
        Assert.Null(snapshot.GetAgeSeconds(node));
        Assert.Equal(BusinessNodeFreshness.Unknown, snapshot.GetFreshness(node));
    }

    [Theory]
    [InlineData("1900", "100", "fresh", "Fresh")]
    [InlineData("1900", "100", "stale", "Stale")]
    [InlineData("1900", "100", "unknown", "Unknown")]
    [InlineData("100", "1900", "fresh", "Stale")]
    [InlineData("2001", "0", "fresh", "Unknown")]
    [InlineData("null", "100", "fresh", "Unknown")]
    [InlineData("1900", "null", "fresh", "Unknown")]
    [InlineData("1900", "1", "fresh", "Unknown")]
    public void Parse_ServerSampleAgeAndFreshnessOverrideUnreliableOrMissingSamples(
        string observed, string age, string freshness, string expected)
    {
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot(Row(observed: observed, age: age, freshness: freshness)));
        Assert.Equal(expected, snapshot.GetFreshness(Assert.Single(snapshot.Nodes).Value).ToString());
    }

    [Fact]
    public void Snapshot_AgesFromItsOriginalMonotonicReceipt_WithoutConsultingGuestWallClock()
    {
        // A server date in the past is intentional: local date must not mark it stale.
        var before = Stopwatch.GetTimestamp();
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot());
        var node = Assert.Single(snapshot.Nodes).Value;
        Assert.InRange(snapshot.ReceivedTimestamp, before, Stopwatch.GetTimestamp());
        Assert.Equal<ulong?>(100, snapshot.GetAgeSeconds(node, snapshot.ReceivedTimestamp));
        Assert.Equal(BusinessNodeFreshness.Fresh, snapshot.GetFreshness(node, snapshot.ReceivedTimestamp));

        var later = snapshot.ReceivedTimestamp + 1221 * Stopwatch.Frequency;
        Assert.Equal<ulong?>(1321, snapshot.GetAgeSeconds(node, later));
        Assert.Equal(BusinessNodeFreshness.Stale, snapshot.GetFreshness(node, later));
        Assert.Equal<ulong?>(1321, snapshot.GetAgeSeconds(node, later));
        Assert.Equal<ulong?>(100, node.AgeSeconds);
        Assert.Equal(BusinessNodeFreshness.Fresh, node.Freshness);
    }

    [Theory]
    [InlineData("[2001:db8::1]", "2001:0db8:0:0:0:0:0:1")]
    [InlineData("BÜCHER.example", "xn--bcher-kva.example")]
    [InlineData("NODE.EXAMPLE.INVALID.", "node.example.invalid")]
    public void Parse_NormalizesEquivalentIpv6IdnAndDnsHosts_ForMatching(string source, string lookup)
    {
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot(Row(source)));
        Assert.True(snapshot.TryGetNode(lookup, out var node));
        Assert.Equal("FI", node.CountryIso);
        Assert.False(snapshot.TryGetNode("https://" + lookup, out _));
    }

    [Theory]
    [InlineData("node.example.invalid", "NODE.EXAMPLE.INVALID")]
    [InlineData("node.example.invalid.", "node.example.invalid")]
    [InlineData("[2001:db8::1]", "2001:0db8::1")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    public void Parse_RejectsAmbiguousDuplicateEndpoints_AfterNormalization(string first, string second) =>
        Assert.Throws<InvalidDataException>(() => BusinessNodeSnapshot.Parse(Snapshot(Row(first) + "," + Row(second))));

    [Theory]
    [InlineData("")]
    [InlineData("https://node.example.invalid")]
    [InlineData("user@node.example.invalid")]
    [InlineData("node.example.invalid:443")]
    [InlineData("node.example.invalid/path")]
    [InlineData("node.example.invalid?token=private-fixture")]
    [InlineData("node.example.invalid#fragment")]
    [InlineData("node.example.invalid\\path")]
    [InlineData("node..example.invalid")]
    [InlineData("node.example.invalid..")]
    [InlineData("-node.example.invalid")]
    [InlineData("node_.example.invalid")]
    [InlineData(" node.example.invalid")]
    [InlineData("[node.example.invalid]")]
    [InlineData("[node.example.invalid")]
    [InlineData("node.example.invalid]")]
    [InlineData("[2001:db8::1")]
    [InlineData("2001:db8::1]")]
    [InlineData("[[2001:db8::1]]")]
    [InlineData("[192.0.2.1]")]
    [InlineData("fe80::1%3")]
    [InlineData("999.1.1.1")]
    public void Parse_RejectsNonHostInputWithoutExposingPrivateValues(string host)
    {
        var error = Assert.Throws<InvalidDataException>(() => BusinessNodeSnapshot.Parse(Snapshot(Row(host))));
        Assert.Equal("Invalid node metadata snapshot.", error.Message);
        Assert.Null(error.InnerException);
    }

    public static IEnumerable<object[]> InvalidSnapshots()
    {
        yield return ["[]"];
        yield return ["not-json-private-fixture"];
        yield return [Snapshot().Replace("\"schema\":1", "\"schema\":2")];
        yield return [Snapshot().Replace("\"schema\":1", "\"schema\":\"1\"")];
        yield return [Snapshot().Replace("\"schema\":1", "\"schema\":1,\"schema\":1")];
        yield return [Snapshot().Replace("\"nodes\":[", "\"nodes\":null,\"unused\":[")];
        yield return [Snapshot(Row() + ",null")];
        yield return [Snapshot(string.Join(",", Enumerable.Range(0, 501).Select(i => Row($"node{i}.example.invalid"))))];
        yield return [Snapshot(Row().Replace("\"country_iso\":\"fi\"", "\"country_iso\":\"fi\",\"country_iso\":null"))];
        yield return [Snapshot(Row(country: "\"FIN\""))];
        yield return [Snapshot(Row(country: "\"1A\""))];
        yield return [Snapshot(Row(country: "\"ФИ\""))];
        yield return [Snapshot(Row(total: "\"1024\""))];
        yield return [Snapshot(Row(total: "9223372036854775808"))];
        yield return [Snapshot(Row(available: "1.5"))];
        yield return [Snapshot(Row(load: "-1"))];
        yield return [Snapshot(Row(load: "4294967296"))];
        yield return [Snapshot(Row(load: "false"))];
        yield return [Snapshot(Row(age: "-1"))];
        yield return [Snapshot(Row(age: "18446744073709551616"))];
        yield return [Snapshot(Row(age: "2001"))];
        yield return [Snapshot(Row(observed: "-1"))];
        yield return [Snapshot(Row(observed: "253402300800"))];
        yield return [Snapshot(Row(freshness: "available"))];
        yield return [Snapshot(generated: "-1")];
        yield return [Snapshot(generated: "253402300800")];
        yield return [Snapshot(generated: "null")];
        yield return [Snapshot(ttl: "0")];
        yield return [Snapshot(ttl: "86401")];
        yield return [Snapshot(ttl: "\"1320\"")];
        yield return [Snapshot() + new string(' ', BusinessNodeSnapshot.MaximumBytes)];
        yield return [Snapshot() + new string('é', BusinessNodeSnapshot.MaximumBytes / 2)];
        yield return [Snapshot() + "\ud800"];
    }

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void Parse_RejectsMalformedSchemaNumbersBoundsAndEncoding_WithSanitizedErrors(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => BusinessNodeSnapshot.Parse(json));
        Assert.Equal("Invalid node metadata snapshot.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Parse_AcceptsMaximumNodeCountAndUnsignedLoad_WithoutConvertingLoadToCpuPercent()
    {
        var snapshot = BusinessNodeSnapshot.Parse(Snapshot(string.Join(",", Enumerable.Range(0, 500)
            .Select(i => Row($"node{i}.example.invalid", load: "4294967295")))));
        Assert.Equal(500, snapshot.Nodes.Count);
        Assert.All(snapshot.Nodes.Values, row => Assert.Equal<uint?>(uint.MaxValue, row.Load1MinX100));
    }

    [Fact]
    public void Parse_AcceptsTheByteLimitAndAnEmptyGrant_WithoutInventingNodes()
    {
        var json = Snapshot(rows: "");
        json += new string(' ', BusinessNodeSnapshot.MaximumBytes - Encoding.UTF8.GetByteCount(json));
        Assert.Empty(BusinessNodeSnapshot.Parse(json).Nodes);
    }

    [Fact]
    public void RealHandler_DisablesRedirectsAndCookies()
    {
        using var handler = BusinessNodeMetadataClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task Fetch_SendsOnlyReadOnlyJsonRequest_AndDoesNotDisposeInjectedClient()
    {
        var calls = 0;
        var endpoint = new Uri("https://example.invalid/private-fixture/node-status");
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(endpoint, request.RequestUri);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("application/json", request.Headers.Accept.ToString());
            return Task.FromResult(Response(Snapshot()));
        }));
        using (var metadata = new BusinessNodeMetadataClient(client))
            Assert.Single((await metadata.FetchAsync(endpoint, CancellationToken.None)).Nodes);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var stillUsable = await client.GetAsync(endpoint);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http://example.invalid/nodes")]
    [InlineData("https://user:private-fixture@example.invalid/nodes")]
    [InlineData("https://example.invalid/nodes#private-fixture")]
    [InlineData("/nodes")]
    public async Task Fetch_RejectsUntrustedEndpointBeforeSending(string endpoint)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send.")));
        using var metadata = new BusinessNodeMetadataClient(client);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => metadata.FetchAsync(new(endpoint, UriKind.RelativeOrAbsolute), CancellationToken.None));
        Assert.Equal("Node metadata service must use HTTPS.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(401)]
    [InlineData(503)]
    public async Task Fetch_RejectsRedirectsAndErrors_WithoutConsumingTheBody(int status)
    {
        using var content = new NoLengthContent(Encoding.UTF8.GetBytes("private-fixture-error-body"));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = content,
            Headers = { Location = new Uri("http://other.example.invalid/private-fixture") }
        })));
        using var metadata = new BusinessNodeMetadataClient(client);
        var error = await Assert.ThrowsAsync<IOException>(() => metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None));
        Assert.Equal(0, content.BytesRead);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-fixture", error.ToString());
    }

    [Fact]
    public async Task Fetch_RejectsChangedFinalRequestUri_EvenForAnInjectedRedirectingClient()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Snapshot()),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://other.example.invalid/nodes")
        })));
        using var metadata = new BusinessNodeMetadataClient(client);
        await Assert.ThrowsAsync<IOException>(() => metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_EnforcesKnownAndUnknownLengthCaps_WithoutBufferingTheWholeResponse()
    {
        foreach (var knownLength in new[] { true, false })
        {
            using var content = new NoLengthContent(new byte[BusinessNodeSnapshot.MaximumBytes * 2]);
            if (knownLength) content.Headers.ContentLength = BusinessNodeSnapshot.MaximumBytes * 2;
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
            using var metadata = new BusinessNodeMetadataClient(client);
            await Assert.ThrowsAsync<IOException>(() => metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None));
            if (knownLength) Assert.Equal(0, content.BytesRead);
            else Assert.InRange(content.BytesRead, BusinessNodeSnapshot.MaximumBytes + 1, BusinessNodeSnapshot.MaximumBytes + 8192);
            Assert.False(content.SerializedInsteadOfStreamed);
        }
    }

    [Fact]
    public async Task Fetch_AcceptsAValidUnknownLengthUtf8Stream_AndRejectsInvalidUtf8()
    {
        foreach (var valid in new[] { true, false })
        {
            var payload = valid ? Encoding.UTF8.GetBytes(Snapshot(Row("bücher.example"))) : new byte[] { 0xff, 0xfe, 0x80 };
            using var content = new NoLengthContent(payload);
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
            using var metadata = new BusinessNodeMetadataClient(client);
            var task = metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None);
            if (valid) Assert.Single((await task).Nodes);
            else
            {
                var error = await Assert.ThrowsAsync<IOException>(() => task);
                Assert.Null(error.InnerException);
            }
            Assert.Equal(payload.Length, content.BytesRead);
            Assert.False(content.SerializedInsteadOfStreamed);
        }
    }

    [Fact]
    public async Task Fetch_SanitizesTransportFailures_WithoutRetainingPrivateExceptions()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("https://private-fixture.invalid/token")));
        using var metadata = new BusinessNodeMetadataClient(client);
        var error = await Assert.ThrowsAsync<IOException>(() => metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-fixture", error.ToString());
    }

    [Fact]
    public async Task Fetch_RejectsMalformedSuccessfulJson_WithoutReturningItsPrivateBody()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("private-fixture-broken-json"))));
        using var metadata = new BusinessNodeMetadataClient(client);
        var error = await Assert.ThrowsAsync<IOException>(() => metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-fixture", error.ToString());
    }

    [Fact]
    public async Task Fetch_LinksCallerCancellationThroughTheResponseStream()
    {
        using var entered = new SemaphoreSlim(0);
        using var caller = new CancellationTokenSource();
        using var content = new BlockingContent(entered);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var metadata = new BusinessNodeMetadataClient(client);
        var task = metadata.FetchAsync(new("https://example.invalid/nodes"), caller.Token);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        caller.Cancel();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Fetch_DeadlineBoundsTheRequest_AndReturnsASanitizedFailure()
    {
        using var entered = new SemaphoreSlim(0);
        using var content = new BlockingContent(entered);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var metadata = new BusinessNodeMetadataClient(client, TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<IOException>(() => metadata.FetchAsync(new("https://example.invalid/nodes"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("example.invalid", error.ToString());
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class NoLengthContent(byte[] payload) : HttpContent
    {
        internal int BytesRead { get; private set; }
        internal bool SerializedInsteadOfStreamed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializedInsteadOfStreamed = true;
            throw new InvalidOperationException("Fixture must stream without prebuffering.");
        }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingStream(payload, read => BytesRead += read));
    }

    private sealed class CountingStream(byte[] payload, Action<int> onRead) : MemoryStream(payload, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            onRead(read);
            return read;
        }
    }

    private sealed class BlockingContent(SemaphoreSlim entered) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockingStream(entered));
    }

    private sealed class BlockingStream(SemaphoreSlim entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            entered.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
