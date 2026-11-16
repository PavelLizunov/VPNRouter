using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyPhoneIntegrationTests
{
    [Fact]
    public void PhoneJournal_IsSeparateFromLegacySiteFloor_AndRetainsEmptyOffline()
    {
        using var fixture = new Fixture();
        var main = fixture.Store();
        main.Accept(fixture.Envelope(8));
        var original = File.ReadAllBytes(main.JournalPath);
        var phone = fixture.Store(phone: true);
        Assert.NotEqual(main.JournalPath, phone.JournalPath);
        phone.Accept(fixture.Envelope(3, phone: true, empty: true));
        Assert.Throws<InvalidDataException>(() => phone.Accept(fixture.Envelope(2, phone: true)));
        Assert.Throws<InvalidDataException>(() => phone.Accept(fixture.Envelope(3, phone: true)));
        Assert.Throws<InvalidDataException>(() => phone.Accept(fixture.Envelope(9)));
        Assert.Equal(original, File.ReadAllBytes(main.JournalPath));
        var recovered = fixture.Store(phone: true).Load()!.Phone!;
        Assert.Equal(3, recovered.Revision);
        Assert.Empty(recovered.Rules);
        Assert.Equal(8, fixture.Store().Load()!.Revision);
    }

    [Fact]
    public async Task Coordinator_MissingPhoneIsNormalUntilAccepted_ThenRetainsEmptyOnMissingAndOffline()
    {
        using var fixture = new Fixture();
        byte[]? phoneResponse = null;
        var offline = false;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromResult(fixture.Envelope(8)), phoneStore: fixture.Store(phone: true), embeddedRules: fixture.EmbeddedRules,
            fetchPhone: _ => offline ? Task.FromException<byte[]?>(new IOException()) : Task.FromResult(phoneResponse));
        var received = new List<BusinessPolicySnapshot>();
        coordinator.Accepted += received.Add;
        await coordinator.RefreshAsync();
        Assert.False(coordinator.IsUnavailable);
        Assert.Null(coordinator.Current!.Phone);
        var mainDigest = coordinator.Current.PayloadDigest;
        phoneResponse = fixture.Envelope(1, phone: true, empty: true, now: true);
        await coordinator.RefreshAsync();
        var combined = coordinator.Current!;
        Assert.Empty(combined.Phone!.Rules);
        Assert.True(combined.ApplyNow);
        Assert.Equal(mainDigest, combined.PayloadDigest);
        Assert.NotEqual(mainDigest, combined.EffectiveDigest);
        Assert.Equal(2, received.Count);
        await coordinator.RefreshAsync();
        Assert.Equal(2, received.Count);
        phoneResponse = null;
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsUnavailable);
        Assert.Same(combined, coordinator.Current);
        offline = true;
        await coordinator.RefreshAsync();
        Assert.Empty(coordinator.Current!.Phone!.Rules);
        await using var restarted = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromException<byte[]>(new IOException()), phoneStore: fixture.Store(phone: true), embeddedRules: fixture.EmbeddedRules,
            fetchPhone: _ => Task.FromException<byte[]?>(new IOException()));
        Assert.Empty(restarted.Load()!.Phone!.Rules);
        Assert.False(restarted.IsUnavailable);
    }

    [Fact]
    public async Task Coordinator_UpdatesIndependentFloor_WithoutReplayingUnchangedApplyNow()
    {
        using var fixture = new Fixture();
        var mainRevision = 8L;
        var phoneRevision = 1L;
        var mainFails = false;
        var phoneNow = true;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => mainFails ? Task.FromException<byte[]>(new IOException()) : Task.FromResult(fixture.Envelope(mainRevision)),
            phoneStore: fixture.Store(phone: true), embeddedRules: fixture.EmbeddedRules,
            fetchPhone: _ => Task.FromResult<byte[]?>(fixture.Envelope(phoneRevision, phone: true, now: phoneNow)));
        await coordinator.RefreshAsync();
        Assert.True(coordinator.Current!.ApplyNow);
        coordinator.AcknowledgeApplied(coordinator.Current);
        mainRevision++;
        await coordinator.RefreshAsync();
        Assert.Equal(9, coordinator.Current!.Revision);
        Assert.False(coordinator.Current.ApplyNow);
        mainFails = true;
        phoneRevision++;
        phoneNow = false;
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsUnavailable);
        Assert.Equal(9, coordinator.Current!.Revision);
        Assert.Equal(2, coordinator.Current.Phone!.Revision);
        Assert.False(coordinator.Current.ApplyNow);
        phoneRevision = 1;
        await coordinator.RefreshAsync();
        Assert.Equal(2, coordinator.Current.Phone.Revision);
    }

    [Fact]
    public async Task PhoneOnlyAcceptance_UsesEmbeddedSites_AndRecoversWithoutMainJournal()
    {
        using var fixture = new Fixture();
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromException<byte[]>(new IOException()), phoneStore: fixture.Store(phone: true),
            fetchPhone: _ => Task.FromResult<byte[]?>(fixture.Envelope(1, phone: true, empty: true, now: true)),
            embeddedRules: fixture.EmbeddedRules);
        await coordinator.RefreshAsync();
        Assert.True(coordinator.Current!.ApplyNow);
        Assert.Equal(0, coordinator.Current.Revision);
        Assert.Equal("example.com", Assert.Single(coordinator.Current.Rules).Domain);
        Assert.Empty(coordinator.Current.Phone!.Rules);
        await using var restarted = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromException<byte[]>(new IOException()), phoneStore: fixture.Store(phone: true),
            fetchPhone: _ => Task.FromException<byte[]?>(new IOException()), embeddedRules: fixture.EmbeddedRules);
        var loaded = restarted.Load()!;
        Assert.Equal(coordinator.Current.EffectiveDigest, loaded.EffectiveDigest);
        Assert.False(loaded.ApplyNow);
        Assert.Empty(loaded.Phone!.Rules);
    }

    [Fact]
    public async Task PendingImmediatePhone_SurvivesLaterSiteAcceptanceUntilItsReceipt()
    {
        using var fixture = new Fixture();
        var mainRevision = 8L;
        await using var coordinator = new BusinessPolicyCoordinator(fixture.Configuration, fixture.Store(),
            _ => Task.FromResult(fixture.Envelope(mainRevision)), phoneStore: fixture.Store(phone: true),
            fetchPhone: _ => Task.FromResult<byte[]?>(fixture.Envelope(1, phone: true, now: true)),
            embeddedRules: fixture.EmbeddedRules);
        await coordinator.RefreshAsync();
        var first = coordinator.Current!;
        mainRevision++;
        await coordinator.RefreshAsync();
        Assert.True(coordinator.Current!.ApplyNow);
        Assert.Equal(9, coordinator.Current.Revision);
        coordinator.AcknowledgeApplied(first);
        Assert.False(coordinator.Current.ApplyNow);
    }

    [Fact]
    public void UnsupportedPhoneRuntime_DoesNotPoisonLastGoodOfflineJournal()
    {
        using var fixture = new Fixture();
        var store = fixture.Store(phone: true);
        store.Accept(fixture.Envelope(1, phone: true, empty: true));
        Assert.Throws<InvalidDataException>(() => store.Accept(
            fixture.Envelope(2, phone: true, phoneValue: "2001:db8::/32")));
        Assert.Empty(fixture.Store(phone: true).Load()!.Phone!.Rules);
    }

    [Fact]
    public async Task PhoneHttp_UsesTrustedChildPathAndSameReaderIdentity_AndOnly404MeansAbsent()
    {
        using var fixture = new Fixture();
        var status = HttpStatusCode.OK;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(new Uri(fixture.Configuration.Endpoint.AbsoluteUri.TrimEnd('/') + "/telephony"), request.RequestUri);
            Assert.Equal(fixture.Configuration.ReadToken, request.Headers.Authorization!.Parameter);
            return new(status)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        }));
        using var http = new BusinessPolicyHttp(client);
        Assert.Equal("{}"u8.ToArray(), await http.FetchPhoneAsync(fixture.Configuration, CancellationToken.None));
        status = HttpStatusCode.NotFound;
        Assert.Null(await http.FetchPhoneAsync(fixture.Configuration, CancellationToken.None));
        status = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<IOException>(() => http.FetchPhoneAsync(fixture.Configuration, CancellationToken.None));
    }

    [Fact]
    public void Capture_RemovesOnlyLegacyOwnedBypass_AndRefusesRemainingPhysicalProxyConflict()
    {
        using var fixture = new Fixture();
        var main = BusinessPolicy.Parse(fixture.Envelope(8), fixture.Configuration.PolicyId, fixture.Configuration.Keys);
        var phone = BusinessPhonePolicy.Parse(fixture.Envelope(1, phone: true), fixture.Configuration.PolicyId, fixture.Configuration.Keys);
        var seed = new List<CustomRule>
        {
            new() { Type = "domain_suffix", Value = "example.com", Action = "proxy", Enabled = true },
            new() { Type = "ip_cidr", Value = "195.211.120.35/32", Action = "direct", Enabled = true }
        };
        var source = new AppSettings();
        source.Tun.RouteExcludeAddress = ["195.211.120.35/32", "203.0.113.0/24"];
        var captured = BusinessPolicySettings.Capture(source, seed, main.WithPhone(phone));
        Assert.Equal(["203.0.113.0/24"], captured.Tun.RouteExcludeAddress);
        Assert.Contains("195.211.120.35/32", source.Tun.RouteExcludeAddress);
        source.Tun.AutoDetectedExcludeAddress = ["195.211.120.0/24"];
        Assert.Throws<InvalidDataException>(() => BusinessPolicySettings.Capture(source, seed, main.WithPhone(phone)));
        var emptyPhone = BusinessPhonePolicy.Parse(fixture.Envelope(2, phone: true, empty: true), fixture.Configuration.PolicyId, fixture.Configuration.Keys);
        var empty = BusinessPolicySettings.Capture(source, seed, main.WithPhone(emptyPhone));
        Assert.DoesNotContain(empty.App.CustomRules, rule => rule.Type == "ip_cidr" && rule.Value == "195.211.120.35/32");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "corp-phone-integration-" + Guid.NewGuid().ToString("N"));
        internal BusinessPolicyConfiguration Configuration { get; }
        internal CustomRule[] EmbeddedRules => [new() { Type = "domain_suffix", Value = "example.com", Action = "direct", Enabled = true }];
        internal Fixture() => Configuration = BusinessPolicyConfigurationTests.Configuration(_key);
        internal BusinessPolicyStore Store(bool phone = false) => new(Configuration, _directory, telephony: phone);
        internal byte[] Envelope(long revision, bool phone = false, bool empty = false, bool now = false,
            string phoneValue = "195.211.120.35/32")
        {
            object rules = phone
                ? (empty ? Array.Empty<object>() : [new { type = "ip_cidr", value = phoneValue, action = "proxy", comment = "" }])
                : new[] { new { domain = "example.com", action = "proxy", comment = "" } };
            var fields = new Dictionary<string, object>
            {
                ["schema"] = 1, ["policy_id"] = "fixture-policy", ["key_id"] = "fixture-key",
                ["revision"] = revision, ["published_at"] = 1700000000,
                ["apply"] = now ? "now" : "next_connection", ["rules"] = rules
            };
            if (phone) fields["kind"] = "telephony";
            var payload = JsonSerializer.SerializeToUtf8Bytes(fields);
            var signature = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
        }
        public void Dispose()
        {
            _key.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
