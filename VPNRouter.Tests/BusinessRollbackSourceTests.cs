using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Tests;

public sealed class BusinessRollbackSourceTests
{
    private static readonly Uri Feed = new("https://updates.example.invalid/business/latest.json?private=fixture");

    private static string Entry(string version) => JsonSerializer.Serialize(new
    {
        schema = 1, edition = "Business", architecture = "win-x64", version,
        size = 12345, sha256 = new string('a', 64),
        asset = $"VPNRouterBusiness-update-v{version}-win-x64.zip",
        url = $"https://updates.example.invalid/business/VPNRouterBusiness-update-v{version}-win-x64.zip"
    });

    private static string Catalog(params string[] entries)
    {
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Entry("2.50.9"))!;
        root["releases"] = JsonSerializer.SerializeToElement(entries.Select(entry => JsonSerializer.Deserialize<JsonElement>(entry)).ToArray());
        return JsonSerializer.Serialize(root);
    }

    private static string Mutate(string json, string property, object? value)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        data[property] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.Serialize(data);
    }

    [Fact]
    public async Task History_ListsOnlyOlderStableBusinessRowsNewestFirst_AndReusesTrustedEndpoint()
    {
        var handler = new Handler(() => Json(Catalog(Entry("2.50.5"), Entry("2.50.8"), Entry("2.50.7"), Entry("2.51.0"), Entry("2.50.6"))));
        using var http = new HttpClient(handler);
        var installer = new Installer();
        var source = Source(http, installer);

        var releases = await source.ListOlderAsync(2, includePrereleases: true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "2.50.7", "2.50.6" }, releases.Select(info => info.Version));
        Assert.All(releases, info => Assert.False(info.IsPrerelease));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Feed, request.Uri);
        Assert.Equal("VPNRouterBusiness", request.UserAgent);
        Assert.Empty(installer.Events);
        var list = Assert.IsAssignableFrom<IList<UpdateSourceInfo>>(releases);
        Assert.Throws<NotSupportedException>(() => { list[0] = releases[0] with { Version = "1.0.0" }; });
    }

    [Fact]
    public async Task MissingHistory_UsesLegacyLatestContract_WithoutInventingRollbackRows()
    {
        using var http = new HttpClient(new Handler(() => Json(Entry("2.50.9"))));
        var installer = new Installer();
        var source = Source(http, installer);

        Assert.Equal("2.50.9", (await source.CheckAsync(TestContext.Current.CancellationToken))!.Version);
        Assert.Empty(await source.ListStableAsync(3, TestContext.Current.CancellationToken));
        Assert.Empty(installer.Events);
    }

    [Theory]
    [InlineData("2.50.5")]
    [InlineData("2.50.6")]
    [InlineData("2.50.7")]
    public async Task OptionalHistory_DoesNotChangeLatestOfferContract(string current)
    {
        // Old deployed parsers also ignore this optional field; keep schema/latest fields intact.
        var json = Mutate(Catalog(Entry("2.50.6")), "releases", "invalid optional history");
        using var http = new HttpClient(new Handler(() => Json(json)));
        var source = Source(http, current: current);

        Assert.Equal("2.50.9", (await source.CheckAsync(TestContext.Current.CancellationToken))!.Version);
        await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitHistoryRow_ReusesDownloadPreparationAndApply_AndRejectsForgedCopies()
    {
        using var http = new HttpClient(new Handler(() => Json(Catalog(Entry("2.50.7"), Entry("2.50.6")))));
        var installer = new Installer();
        var source = Source(http, installer);
        var row = Assert.Single(await source.ListStableAsync(1, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAsync(row with { }, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ApplyAsync(row, "forged", TestContext.Current.CancellationToken));
        Assert.Empty(installer.Events);

        var staged = await source.DownloadAsync(row, ct: TestContext.Current.CancellationToken);
        Assert.True(await source.ApplyAsync(row, staged, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "download:2.50.7", "prepare", "apply:2.50.7" }, installer.Events);
        Assert.Same(row, installer.Applied);
        Assert.Equal(staged, installer.AppliedPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewHistoryAttempt_RevokesUnstartedOldRows(bool malformed)
    {
        var json = Catalog(Entry("2.50.7"));
        using var http = new HttpClient(new Handler(() => Json(json)));
        var installer = new Installer();
        var source = Source(http, installer);
        var old = Assert.Single(await source.ListStableAsync(3, TestContext.Current.CancellationToken));
        json = malformed ? Mutate(Catalog(), "releases", false) : Catalog(Entry("2.50.6"));

        if (malformed)
            await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
        else
            Assert.Equal("2.50.6", Assert.Single(await source.ListStableAsync(3, TestContext.Current.CancellationToken)).Version);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAsync(old, ct: TestContext.Current.CancellationToken));
        Assert.Empty(installer.Events);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingDownload_StaysPinnedAcrossNewCheckAndHistory(bool rollback, bool malformedLaterHistory)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var json = Catalog(Entry("2.50.7"));
        using var http = new HttpClient(new Handler(() => Json(json)));
        var installer = new Installer
        {
            Download = _ => { started.SetResult(); return release.Task; }
        };
        var source = Source(http, installer);
        var latest = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
        var old = Assert.Single(await source.ListStableAsync(3, TestContext.Current.CancellationToken));
        var selected = rollback ? old : latest;
        var pending = source.DownloadAsync(selected, ct: TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            json = Catalog(Entry("2.50.6"));
            if (malformedLaterHistory) json = Mutate(json, "releases", "malformed");
            var later = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
            Assert.NotSame(latest, later);
            if (malformedLaterHistory)
                await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
            else
                Assert.Equal("2.50.6", Assert.Single(await source.ListStableAsync(3, TestContext.Current.CancellationToken)).Version);
            Assert.Null(installer.Applied);
            release.SetResult("staged-original");
            var path = await pending;

            Assert.True(await source.ApplyAsync(selected, path, TestContext.Current.CancellationToken));
            Assert.Same(selected, installer.Applied);
            Assert.Equal("staged-original", installer.AppliedPath);
            await Assert.ThrowsAsync<InvalidDataException>(() => source.ApplyAsync(later, path, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "download:" + selected.Version, "prepare", "apply:" + selected.Version }, installer.Events);
        }
        finally { release.TrySetResult("staged-original"); }
    }

    public static IEnumerable<object[]> InvalidHistoricalEntries()
    {
        foreach (var pair in new (string Property, object? Value)[]
        {
            ("edition", "Standard"), ("architecture", "win-arm64"), ("schema", 2),
            ("sha256", "invalid"), ("size", 0), ("size", -1), ("size", 536870913L),
            ("asset", "VPNRouter-v2.50.7-win.zip"),
            ("url", "http://updates.example.invalid/business/VPNRouterBusiness-update-v2.50.7-win-x64.zip"),
            ("url", "https://other.example.invalid/business/VPNRouterBusiness-update-v2.50.7-win-x64.zip"),
            ("url", "https://updates.example.invalid/business/VPNRouterBusiness-update-v2.50.7-win-x64.zip?private=fixture"),
            ("url", "https://updates.example.invalid/business/VPNRouterBusiness-update-v2.50.7-win-x64.zip#fragment"),
            ("url", "https://user:fixture@updates.example.invalid/business/VPNRouterBusiness-update-v2.50.7-win-x64.zip"),
        }) yield return [Mutate(Entry("2.50.7"), pair.Property, pair.Value)];
        yield return [Entry("2.50.7-r1")];
        yield return [Entry("v2.50.7")];
        yield return [Entry("02.50.7")];
        yield return [Entry("2.50.7").Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal)];
        yield return ["{}"];
        yield return ["null"];
    }

    [Theory]
    [MemberData(nameof(InvalidHistoricalEntries))]
    public async Task InvalidHistoryEntry_FailsHistoryOnly_BeforeAnyInstallerCall(string entry)
    {
        using var http = new HttpClient(new Handler(() => Json(Catalog(entry))));
        var installer = new Installer();
        var source = Source(http, installer);

        Assert.Equal("2.50.9", (await source.CheckAsync(TestContext.Current.CancellationToken))!.Version);
        await AssertRedactedFailure(() => source.ListOlderAsync(3, true, TestContext.Current.CancellationToken));
        Assert.Empty(installer.Events);
    }

    [Fact]
    public async Task DuplicateHistoryVersions_AreRejectedEvenWithDifferentHashes()
    {
        using var http = new HttpClient(new Handler(() => Json(Catalog(Entry("2.50.7"),
            Mutate(Entry("2.50.7"), "sha256", new string('b', 64))))));
        var source = Source(http);
        Assert.Equal("2.50.9", (await source.CheckAsync(TestContext.Current.CancellationToken))!.Version);
        await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HistoryCount_IsBoundedBeforeFilteringAndHonorsTheLimit()
    {
        var rows = Enumerable.Range(0, BusinessUpdateSource.HistoryLimit).Select(i => Entry($"2.49.{i}")).ToArray();
        var json = Catalog(rows);
        using var http = new HttpClient(new Handler(() => Json(json)));
        var source = Source(http);
        var accepted = await source.ListStableAsync(int.MaxValue, TestContext.Current.CancellationToken);
        Assert.Equal(BusinessUpdateSource.HistoryLimit, accepted.Count);
        Assert.Equal("2.49.11", accepted[0].Version);
        Assert.Equal("2.49.0", accepted[^1].Version);

        json = Catalog(rows.Append(Entry("2.49.12")).ToArray());
        Assert.Equal("2.50.9", (await source.CheckAsync(TestContext.Current.CancellationToken))!.Version);
        await AssertRedactedFailure(() => source.ListStableAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingFeedUnsupportedPlatformAndNonpositiveCount_DoNotFetch()
    {
        var handler = new Handler(() => throw new InvalidOperationException("unexpected HTTP"));
        using var http = new HttpClient(handler);
        var missing = new BusinessUpdateSource(null, "2.50.8", new Installer(), http: http, windowsX64: true);
        var unsupported = new BusinessUpdateSource(Feed, "2.50.8", new Installer(), http: http, windowsX64: false);
        Assert.Empty(await missing.ListStableAsync(3, TestContext.Current.CancellationToken));
        Assert.Empty(await unsupported.ListOlderAsync(3, false, TestContext.Current.CancellationToken));
        var configured = Source(http);
        Assert.Empty(await configured.ListStableAsync(0, TestContext.Current.CancellationToken));
        Assert.Empty(await configured.ListOlderAsync(-1, false, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLengthHistoryResponse_IsBounded(bool oversized)
    {
        var json = oversized ? new string(' ', BusinessUpdateSource.ManifestLimit + 1) : Catalog(Entry("2.50.7"));
        using var http = new HttpClient(new Handler(() =>
        {
            var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(json)));
            content.Headers.ContentType = new("application/json");
            Assert.Null(content.Headers.ContentLength);
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var source = Source(http);
        if (oversized)
            await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
        else
            Assert.Equal("2.50.7", Assert.Single(await source.ListStableAsync(3, TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task ExcessiveDepth_IsRejectedWithoutLeakingTheTrustedFeed()
    {
        var entry = Entry("2.50.7");
        var deep = entry[..^1] + ",\"extra\":[[[[[[[0]]]]]]]}";
        using var http = new HttpClient(new Handler(() => Json(Catalog(deep))));
        var source = Source(http);
        await AssertRedactedFailure(() => source.CheckAsync(TestContext.Current.CancellationToken));
        await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TransportFailure_IsRedactedForCheckAndHistory()
    {
        using var http = new HttpClient(new Handler(() => throw new HttpRequestException("raw " + Feed.AbsoluteUri)));
        var source = Source(http);
        await AssertRedactedFailure(() => source.CheckAsync(TestContext.Current.CancellationToken));
        await AssertRedactedFailure(() => source.ListStableAsync(3, TestContext.Current.CancellationToken));
    }

    private static BusinessUpdateSource Source(HttpClient http, Installer? installer = null, string current = "2.50.8")
    {
        var delivery = installer ?? new Installer();
        return new(Feed, current, delivery,
            () => { delivery.Events.Add("prepare"); return Task.CompletedTask; }, http, true);
    }

    private static async Task AssertRedactedFailure(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(action);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Feed.AbsoluteUri, error.ToString());
        Assert.DoesNotContain("private=fixture", error.ToString());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal readonly List<(Uri? Uri, string UserAgent)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Headers.UserAgent.ToString()));
            return Task.FromResult(response());
        }
    }

    private sealed class Installer : IDesktopInstaller
    {
        internal readonly List<string> Events = [];
        internal Func<UpdateSourceInfo, Task<string>>? Download;
        internal UpdateSourceInfo? Applied;
        internal string? AppliedPath;
        public Task<string> DownloadAndStageAsync(UpdateSourceInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct)
        { Events.Add("download:" + info.Version); return Download?.Invoke(info) ?? Task.FromResult("staged"); }
        public Task<bool> ApplyStagedAsync(UpdateSourceInfo info, string stagedPath, CancellationToken ct)
        { Events.Add("apply:" + info.Version); Applied = info; AppliedPath = stagedPath; return Task.FromResult(true); }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
