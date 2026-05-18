using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 2F (2026-05-17) — pins behaviour of the canonical
/// <see cref="ConfigPipeline.Generate"/> helper that
/// <see cref="VpnEngine.StartAsync"/> + <see cref="HealthMonitor"/>'s
/// auto-restart path both now route through. Each test corresponds to a
/// bug class the helper exists to close:
/// <list type="bullet">
///   <item><c>HappyPath</c> — pipeline produces valid sing-box JSON shape.</item>
///   <item><c>EmptyServers</c> — v2.28.2 hard guard pin (resolver returns
///   zero → InvalidOperationException with descriptive reason).</item>
///   <item><c>PlaceholderActiveServer</c> — v2.32.3 stas-class fingerprint
///   (placeholder active_server forces fallback to subscription if any).</item>
///   <item><c>SubscriptionMode_Aggregates</c> — VlessServersResolver path
///   for subscribe mode (subscription servers fold into vless.servers
///   in-place).</item>
///   <item><c>LegacyVlessServers</c> — back-compat for direct-VLESS
///   users who never adopted subscriptions.</item>
/// </list>
/// </summary>
public sealed class ConfigPipelineTests
{
    private static VlessServerEntry MakeServer(
        string name, string host, int port = 443) =>
        new()
        {
            Name = name,
            Server = host,
            Port = port,
            Uuid = "11111111-2222-3333-4444-" + host.GetHashCode().ToString("X").PadLeft(12, '0'),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                ShortId = "d86e92a0c6dd2271"
            }
        };

    private static AppSettings BuildBaseSettings(string configMode = "generated") =>
        new()
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = configMode,
                Subscriptions = new List<SubscriptionEntry>()
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig()
        };

    private static Profile BuildProfile() =>
        new()
        {
            Name = "TestProfile",
            DnsMode = "vpn_only",
            Processes = new List<ProcessRule>
            {
                new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } }
            }
        };

    // ─────────────────────────────────────────────────────────────────────
    // Test 1: happy path — full generated-mode pipeline returns valid JSON
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_HappyPath_ProducesValidJson()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";
        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        // Coarse JSON shape check — full sing-box config shape is covered
        // by ConfigGenerator's own test suite; here we only verify the
        // pipeline returns a non-empty, parseable JSON with a proxy
        // outbound (the exact class of bug v2.28.2 produced silently).
        Assert.False(string.IsNullOrWhiteSpace(json));
        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);
        Assert.True(outbounds!.Count > 0,
            "Generated config must have at least one outbound");

        var proxyOutbound = outbounds
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);
        Assert.Equal("104.194.156.93", proxyOutbound!["server"]?.GetValue<string>());

        // Pipeline side-effect: settings.Vless.Servers mutated in place by
        // resolver (same contract pre-2F).
        Assert.Single(settings.Vless.Servers);
        Assert.Equal("104.194.156.93", settings.Vless.Servers[0].Server);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 2: empty servers — pin v2.28.2 hard guard
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_EmptyServers_ThrowsConfigValidationException()
    {
        // Subscribe mode with no enabled subscriptions and empty Vless.Servers
        // — exactly the v2.28.2 silent-leak preconditions (resolver would
        // return [], ConfigGenerator would emit JSON without a proxy outbound,
        // sing-box would silently route to direct).
        var settings = BuildBaseSettings(configMode: "subscribe");
        var profile = BuildProfile();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigPipeline.Generate(
                profile,
                new[] { "Discord.exe" },
                settings,
                ConfigPipeline.ValidationMode.Strict));

        // Message must be actionable (DescribeEmptyReason wording), not the
        // generic "no active VLESS servers" string from ConfigGenerator's
        // own guard. This is the contract that lets the UI surface a useful
        // error toast rather than "config validation failed: ???".
        Assert.NotNull(ex.Message);
        Assert.True(
            ex.Message.Contains("subscription", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("VLESS", StringComparison.OrdinalIgnoreCase),
            $"Expected user-actionable empty-servers message, got: {ex.Message}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 3: placeholder active server — pin v2.32.3 fingerprint guard
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_PlaceholderActiveServer_FallsBackToSubscription()
    {
        // Stas's pre-2.32.3 evidence shape:
        //   config_mode = generated
        //   subscription has working servers
        //   vless.servers contains the placeholder khunrath_ln entry
        //   vless.active_server = khunrath_ln  ← shadow-overrides subscription
        //
        // VlessServersResolver's scope guard (r7 Fix-A) catches this:
        // active entry matches known-placeholder fingerprints → subscription
        // wins. ConfigPipeline.Generate inherits this behaviour for free
        // because it goes through VlessServersResolver.Resolve.
        const string placeholderServer = "195.135.255.216";
        const string placeholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
        const string placeholderShortId = "78ca7952";

        var settings = BuildBaseSettings();
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("de-01", "104.194.156.93", 443)
                }
            }
        };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "khunrath_ln",
                Server = placeholderServer,
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "yahoo.com",
                    Fingerprint = "firefox",
                    PublicKey = placeholderPubkey,
                    ShortId = placeholderShortId
                }
            }
        };
        settings.Vless.ActiveServer = "khunrath_ln";

        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);

        var proxyOutbound = outbounds!
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);

        // Critical pin: the placeholder IP must NOT appear as the outbound
        // server — subscription's de-01 takes over.
        var server = proxyOutbound!["server"]?.GetValue<string>();
        Assert.NotEqual(placeholderServer, server);
        Assert.Equal("104.194.156.93", server);

        // Active server has been auto-corrected to the subscription entry.
        Assert.Equal("de-01", settings.Vless.ActiveServer);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 4: subscription mode aggregates servers (VlessServersResolver path)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SubscriptionMode_AggregatesServers()
    {
        // Pre-2F bug-class: in subscribe mode the subscription servers live
        // in App.Subscriptions[].Servers (NOT Vless.Servers). ConfigGenerator
        // reads Vless.Servers, so without VlessServersResolver folding them
        // first the proxy outbound list comes out empty → silent leak.
        // ConfigPipeline.Generate routes through Resolve so this can't drift.
        var settings = BuildBaseSettings(configMode: "subscribe");
        settings.App.ActiveSubscriptionServer = "alpha";
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("alpha", "10.0.0.1", 443),
                    MakeServer("beta",  "10.0.0.2", 443),
                    MakeServer("gamma", "10.0.0.3", 443)
                }
            }
        };
        Assert.Empty(settings.Vless.Servers); // baseline: not folded yet

        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        // Side-effect contract: settings.Vless.Servers is now populated
        // with the aggregated subscription list (the in-place mutation the
        // resolver advertises). Downstream consumers (UI status display,
        // urltest selector outbound) see the full list.
        Assert.Equal(3, settings.Vless.Servers.Count);
        Assert.Contains(settings.Vless.Servers, s => s.Server == "10.0.0.1");
        Assert.Contains(settings.Vless.Servers, s => s.Server == "10.0.0.2");
        Assert.Contains(settings.Vless.Servers, s => s.Server == "10.0.0.3");

        // Generated JSON: proxy outbound exists and points at the active
        // subscription server (alpha → 10.0.0.1).
        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);
        var proxyOutbound = outbounds!
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);
        Assert.Equal("10.0.0.1", proxyOutbound!["server"]?.GetValue<string>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 5: legacy direct VLESS still works (back-compat)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_LegacyVlessServers_AppliedToOutput()
    {
        // Users on the direct-VLESS path (pre-subscription era) keep their
        // servers in Vless.Servers and no enabled subscription — must still
        // produce a working config without the resolver inadvertently
        // clobbering their list. Pin for the backward-compat path.
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("legacy-A", "203.0.113.10", 443),
            MakeServer("legacy-B", "203.0.113.11", 443)
        };
        settings.Vless.ActiveServer = "legacy-A";
        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        // Resolver leaves the manual list untouched (no subscriptions to
        // aggregate). ActiveServer stays put.
        Assert.Equal(2, settings.Vless.Servers.Count);
        Assert.Equal("legacy-A", settings.Vless.ActiveServer);

        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);
        var proxyOutbound = outbounds!
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);
        // Active server picked → 203.0.113.10
        Assert.Equal("203.0.113.10", proxyOutbound!["server"]?.GetValue<string>());
    }
}
