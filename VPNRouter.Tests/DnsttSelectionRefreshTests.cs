#nullable enable
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class DnsttSelectionRefreshTests
{
    private const string Feed = "https://feed.invalid/dnstt";

    private static VlessServerEntry Grant(
        string domain = "old.example.test",
        string noise = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string privateKey = "old-client-credential",
        string outboundId = "dnstt:senko",
        string name = "SENKO DNSTT",
        string server = "192.0.2.1") => new()
    {
        Name = name,
        Protocol = "dnstt",
        Security = "none",
        Server = server,
        Port = 53,
        OutboundId = outboundId,
        UseAsSingleOutbound = true,
        Dnstt = new DnsttProfile
        {
            Domain = domain,
            NoiseKey = noise,
            SshHostKey = "ssh-ed25519 fixture",
            SshPrivateKey = privateKey,
        },
    };

    private static AppSettings Settings(SubscriptionEntry feed, VlessServerEntry active)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.Servers = [active];
        settings.Vless.ActiveServer = active.Name;
        settings.App.Subscriptions = [feed];
        return settings;
    }

    private static SubscriptionEntry FeedEntry(params VlessServerEntry[] rows) => new()
    {
        Name = "registered",
        Url = Feed,
        Enabled = true,
        Servers = rows.ToList(),
    };

    private static SubscriptionCacheUpdate Publish(
        AppSettings settings, SubscriptionEntry feed, VlessServerEntry[] nextRows)
    {
        var previous = feed.Servers;
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = nextRows.ToList(),
        };
        var update = new SubscriptionCacheUpdate(feed, previous, candidate, false);
        Assert.True(SubscriptionCacheUpdate.TryPublish([update], settings.App.Subscriptions, null, out _));
        return update;
    }

    [Fact]
    public void BindingSurvivesTheSettingsRoundTrip()
    {
        var active = Grant();
        var settings = Settings(FeedEntry(Grant()), active);

        var binding = DnsttSelectionRefresh.Capture(SettingsLoader.Copy(settings));

        Assert.NotNull(binding);
        Assert.Equal(active.Name, binding.ActiveName);
        Assert.Equal(active.Dnstt!.Domain, binding.Active.Dnstt!.Domain);
        Assert.Equal(active.Dnstt.SshPrivateKey, binding.Active.Dnstt.SshPrivateKey);
    }

    [Fact]
    public void CaptureBindsTheSelectedProfileToItsSingleRegistration()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);

        var binding = DnsttSelectionRefresh.Capture(settings);

        Assert.NotNull(binding);
        Assert.Same(feed.Servers[0], Assert.Single(binding.PreviousRows));
        Assert.Same(active, binding.Active);
        Assert.Equal(active.Name, binding.ActiveName);
        Assert.Equal("dnstt:senko", binding.OutboundId);
    }

    [Fact]
    public void EvaluateBeforePublishAcceptsAUniqueRotation()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = [Grant(domain: "new.example.test")],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Applied,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void EvaluateBeforePublishRejectsASelectionChange()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = [Grant(domain: "new.example.test")],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        settings.Vless.ActiveServer = "another row";

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Invalid,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void EvaluateBeforePublishRejectsANewEnabledRegistration()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = [Grant(domain: "new.example.test")],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Url = "https://other.invalid/dnstt",
            Enabled = true,
            Servers = [Grant(domain: "other.example.test", outboundId: "dnstt:other")],
        });

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Invalid,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void EvaluateBeforePublishAcceptsAnUnchangedGrant()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = [Grant()],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Unchanged,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void EvaluateReportsAFailedOrEmptyFetchAsTransient()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = feed.Servers,
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, true);

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.FetchFailed,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void EvaluateReportsARemovedGrantAsMissing()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = [Grant(domain: "new.example.test", outboundId: "dnstt:other")],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Missing,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void EvaluateReportsDuplicateGrantIdsAsAmbiguous()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers =
            [
                Grant(domain: "new.example.test"),
                Grant(domain: "other.example.test"),
            ],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Ambiguous,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void ApplyRotatesOnlyTheSelectedGrant()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var next = Grant(domain: "new.example.test", noise: new string('b', 64),
            privateKey: "new-client-credential", server: "198.51.100.7");
        var update = Publish(settings, feed, [next]);

        Assert.True(DnsttSelectionRefresh.TryApply(settings, binding, update));

        Assert.Same(active, Assert.Single(settings.Vless.Servers));
        Assert.Equal("SENKO DNSTT", active.Name);
        Assert.Equal("dnstt:senko", active.OutboundId);
        Assert.Equal("198.51.100.7", active.Server);
        Assert.Equal(53, active.Port);
        Assert.Equal("new.example.test", active.Dnstt!.Domain);
        Assert.Equal(new string('b', 64), active.Dnstt.NoiseKey);
        Assert.Equal("new-client-credential", active.Dnstt.SshPrivateKey);
        Assert.Equal("SENKO DNSTT", settings.Vless.ActiveServer);
        Assert.Equal("new.example.test", Assert.Single(feed.Servers).Dnstt!.Domain);
    }

    [Fact]
    public void DisplayLabelIsNotPartOfTheGrantProfile()
    {
        var active = Grant();
        var feed = FeedEntry(Grant(name: "provider label"));
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var candidate = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Enabled = feed.Enabled,
            Servers = [Grant(name: "renamed provider label")],
        };
        var update = new SubscriptionCacheUpdate(feed, feed.Servers, candidate, false);

        Assert.NotNull(binding);
        Assert.Equal(
            DnsttSelectionRefresh.RefreshOutcome.Unchanged,
            DnsttSelectionRefresh.Evaluate(settings, binding, update));
    }

    [Fact]
    public void ClientCredentialMustMatchThePreviousCache()
    {
        var active = Grant(privateKey: "current-client-credential");
        var settings = Settings(FeedEntry(Grant(privateKey: "older-client-credential")), active);

        Assert.Null(DnsttSelectionRefresh.Capture(settings));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("cleartext")]
    [InlineData("multiple")]
    [InlineData("mixed")]
    public void CaptureRejectsUnsupportedRegistrationSets(string scenario)
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        feed.Enabled = scenario != "disabled";
        feed.Url = scenario == "cleartext" ? "http://feed.invalid/dnstt" : Feed;
        var settings = Settings(feed, active);
        if (scenario is "multiple" or "mixed")
            settings.App.Subscriptions.Add(new SubscriptionEntry
            {
                Url = scenario == "mixed" ? "http://other.invalid/dnstt" : "https://other.invalid/dnstt",
                Enabled = true,
                Servers = [Grant(domain: "other.example.test")],
            });

        Assert.Null(DnsttSelectionRefresh.Capture(settings));
    }

    [Fact]
    public void CaptureRejectsAnAmbiguousPreviousGrant()
    {
        var active = Grant();
        var settings = Settings(FeedEntry(Grant(), Grant(domain: "second.example.test")), active);

        Assert.Null(DnsttSelectionRefresh.Capture(settings));
    }

    [Fact]
    public void CaptureRejectsADuplicateSelectedLabel()
    {
        var active = Grant();
        var settings = Settings(FeedEntry(Grant()), active);
        settings.Vless.Servers.Add(new VlessServerEntry
        {
            Name = active.Name,
            Server = "198.51.100.9",
            Protocol = "vless",
        });

        Assert.Null(DnsttSelectionRefresh.Capture(settings));
    }

    [Fact]
    public void SubscriptionOwnedRowIsReconciledInPlace()
    {
        var active = Grant();
        var feed = FeedEntry(active);
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed, [Grant(domain: "new.example.test")]);

        Assert.True(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("new.example.test", active.Dnstt!.Domain);
    }

    [Fact]
    public void ApplyRejectsAUserEditAfterCapture()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed, [Grant(domain: "new.example.test")]);

        active.Server = "203.0.113.9";

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("203.0.113.9", active.Server);
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }

    [Fact]
    public void ApplyRejectsAChangedOutboundIdAfterCapture()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed, [Grant(domain: "new.example.test")]);

        active.OutboundId = "dnstt:other";

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("dnstt:other", active.OutboundId);
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }

    [Fact]
    public void ApplyRejectsAChangedSelectionAfterCapture()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed, [Grant(domain: "new.example.test")]);

        settings.Vless.ActiveServer = "another row";

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("url")]
    public void ApplyRejectsAChangedRegistrationIdentity(string change)
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed, [Grant(domain: "new.example.test")]);

        if (change == "id") update.Candidate.Id = "different-registration";
        else update.Candidate.Url = "https://other.invalid/dnstt";

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }

    [Fact]
    public void ApplyRejectsAnAmbiguousNewGrant()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed,
            [Grant(domain: "new.example.test"), Grant(domain: "other.example.test")]);

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }

    [Fact]
    public void ApplyRejectsADifferentOutboundId()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed,
            [Grant(domain: "new.example.test", outboundId: "dnstt:other")]);

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }

    [Fact]
    public void ApplyLeavesAnUnchangedGrantAlone()
    {
        var active = Grant();
        var feed = FeedEntry(Grant());
        var settings = Settings(feed, active);
        var binding = DnsttSelectionRefresh.Capture(settings);
        var update = Publish(settings, feed, [Grant()]);

        Assert.False(DnsttSelectionRefresh.TryApply(settings, binding, update));
        Assert.Equal("old.example.test", active.Dnstt!.Domain);
    }
}
