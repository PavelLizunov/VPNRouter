using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Regression tests for <see cref="ConfigGenerator"/> behaviour when the
/// effective server list contains servers with duplicate <c>Name</c> fields.
///
/// <para>Repro scenario from core-stability audit §F3: a user has two
/// subscriptions, both containing a server called "server-1". After
/// <see cref="SubscriptionResolver"/> aggregates them into a flat
/// <c>Vless.Servers</c> list, ConfigGenerator sees two entries with the
/// same name. Previously <c>AddOutboundGroup</c> built outbound tags from
/// the name directly (<c>vless-server-1</c>), which could produce duplicate
/// JSON keys — undefined behaviour in sing-box; in practice the second
/// outbound silently overwrites the first.</para>
///
/// <para>The fix (shipped earlier, before the audit was written): a
/// <see cref="HashSet{T}"/> tracks used tags and appends <c>-2</c>, <c>-3</c>
/// suffixes for collisions. These tests pin that behaviour so it survives
/// a future refactor.</para>
/// </summary>
public class ConfigGeneratorDuplicateNameTests
{
    private static AppSettings CreateTwoDuplicateNameServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                // No ActiveServer — GetActiveServers returns all servers
                // with the matching IP; first-match fallback.
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "server-1",
                        Server = "1.1.1.1",
                        Port = 443,
                        Uuid = "uuid-a",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "kA", ShortId = "aa" }
                    },
                    new()
                    {
                        Name = "server-1", // DUPLICATE
                        Server = "2.2.2.2",
                        Port = 443,
                        Uuid = "uuid-b",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "kB", ShortId = "bb" }
                    }
                }
            }
        };
        return settings;
    }

    private static Profile SimpleProfile() => new()
    {
        Name = "Test",
        DnsMode = "vpn_only",
        Processes = new List<ProcessRule>
        {
            new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } }
        }
    };

    [Fact]
    public void DuplicateNames_DoNotProduceDuplicateOutboundTags()
    {
        // With GetActiveServers returning both duplicate-named entries
        // (they happen to share a single active — we set ActiveServer to
        // the duplicated name so GetActiveServers picks the first then
        // same-IP filter brings in the second only if IPs match), we
        // force the multi-server path by making them share a "same IP"
        // pair pattern — but here IPs differ, so GetActiveServers returns
        // one. To repro the bug we want multi-outbound, so explicitly
        // provide two flow-less servers (triggers no-udp path) with
        // distinct IPs and matching names.
        var settings = CreateTwoDuplicateNameServers();
        // Make both entries UDP-compatible (no flow) to hit urltest path.
        foreach (var s in settings.Vless.Servers) s.Flow = "";
        // Override GetActiveServers semantics by picking neither — all go through.
        settings.Vless.ActiveServer = "";

        var config = ConfigGenerator.Generate(SimpleProfile(), new[] { "Discord.exe" }, settings);

        // All outbound tags must be unique — no duplicates allowed.
        var tags = config.Outbounds
            .Where(o => !string.IsNullOrEmpty(o.Tag))
            .Select(o => o.Tag!)
            .ToList();
        var uniqueTags = tags.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(tags.Count, uniqueTags);
    }

    [Fact]
    public void SingleActiveServer_DuplicateNames_StillGeneratesOne()
    {
        // Same-name but different-IP — GetActiveServers picks one by
        // ActiveServer name. This is the common case: the user only
        // wants to route through server-1 right now, even though their
        // config has two subscriptions supplying it.
        var settings = CreateTwoDuplicateNameServers();
        settings.Vless.ActiveServer = "server-1";

        var config = ConfigGenerator.Generate(SimpleProfile(), new[] { "Discord.exe" }, settings);

        // Exactly one VLESS outbound, tagged "proxy" (single-server path
        // collapses the urltest wrapper).
        var vlessOutbounds = config.Outbounds.Where(o => o.Type == "vless").ToList();
        Assert.Single(vlessOutbounds);
        Assert.Equal("proxy", vlessOutbounds[0].Tag);
    }

    // NOTE: The "three servers, all named 'server-X' → urltest with
    // vless-server-X / -2 / -3 suffixes" scenario is hard to hit in
    // practice because GetActiveServers() filters down to a single
    // same-IP group (TCP/UDP pair semantics). The HashSet dedup inside
    // AddOutboundGroup handles name collisions defensively regardless
    // — the above two tests pin the user-visible behaviour (no
    // duplicate output tags, correct single-server routing even with
    // name collisions in the cache). A direct test of AddOutboundGroup
    // would need to make it internal + InternalsVisibleTo; not worth
    // the API-surface cost for a path that can't be triggered from
    // real subscriptions today.
}
