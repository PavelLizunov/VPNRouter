using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// VlessServersResolver scope guard — r10 Fix-A (2026-05-11)
//
// Triggered by stas's evidence config.yaml:
// - config_mode = "generated"  (NOT subscribe!)
// - app.subscriptions[0].servers = [7 working entries: de-01, is-01, nk-01]
// - vless.servers = [khunrath_ln 195.135.255.216 (placeholder),
//                    is-01-grpc-test 93.95.226.167 (stale)]
// - vless.active_server = "khunrath_ln"
//
// Old behavior: ConfigMode=generated → resolver used Vless.GetEffectiveServers()
// directly, so vless.active_server="khunrath_ln" silently won over the working
// subscription servers. ConfigGenerator built outbound[proxy] from
// 195.135.255.216 → all VPN traffic went to a dead/leak server.
//
// New contract (Fix-A): when ConfigMode is "generated" OR "subscribe" AND there
// is at least one enabled subscription with fetched servers, ONLY subscription
// servers are returned. Legacy vless.servers[] is ignored. If vless.active_server
// points outside the scoped list, it falls back to scoped[0] + WARN log.
//
// Falls back to legacy Vless.Servers ONLY when subscriptions are absent/disabled/
// empty — preserving direct-VLESS-mode behavior for users who haven't added a
// subscription.
// ═══════════════════════════════════════════════════════════════════════════════

public class VlessServersResolverScopeGuardTests
{
    private const string StasPlaceholderServer = "195.135.255.216";
    private const string StasStaleTestEntry = "93.95.226.167";
    private const string StasPlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";

    private static VlessServerEntry MakeServer(string name, string host, int port = 443) =>
        new()
        {
            Name = name,
            Server = host,
            Port = port,
            Uuid = "uuid-" + host.GetHashCode().ToString("X"),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "pbk-" + host.GetHashCode().ToString("X"),
                ShortId = "abcd1234"
            }
        };

    /// <summary>
    /// Build settings that match stas's evidence-config.yaml shape:
    /// generated mode + enabled subscription with 3 working servers +
    /// legacy vless.servers with placeholder + stale test entries, plus
    /// vless.active_server pointing at the placeholder.
    /// </summary>
    private static AppSettings BuildStasEvidenceSettings(string activeServer = "khunrath_ln")
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                ActiveSubscriptionServer = "de-01 443 Khunrath",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "simple",
                        Url = "https://ninitux.com/api/v1/app/config/4e5a007b2ab25cb800d9a96d2f36bf37",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            MakeServer("de-01 443 Khunrath", "104.194.156.93", 443),
                            MakeServer("is-01 443 Khunrath", "93.95.226.167", 443),
                            MakeServer("nk-01 8443 Khunrath", "194.87.222.111", 8443)
                        }
                    }
                }
            },
            Vless = new VlessConfig
            {
                Server = StasPlaceholderServer,
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "yahoo.com",
                    Fingerprint = "firefox",
                    PublicKey = StasPlaceholderPubkey,
                    ShortId = "78ca7952"
                },
                Servers = new List<VlessServerEntry>
                {
                    // Stas's legacy placeholder entry — must be IGNORED
                    new()
                    {
                        Name = "khunrath_ln",
                        Server = StasPlaceholderServer,
                        Port = 443,
                        Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true,
                            ServerName = "yahoo.com",
                            Fingerprint = "firefox",
                            PublicKey = StasPlaceholderPubkey,
                            ShortId = "78ca7952"
                        }
                    },
                    // Stas's stale test entry — must be IGNORED too
                    MakeServer("is-01-grpc-test", StasStaleTestEntry, 8444)
                },
                ActiveServer = activeServer
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Required test case #1: scoped to subscription, ignores legacy entries
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_WithEnabledSubscription_IgnoresLegacyVlessServers()
    {
        var settings = BuildStasEvidenceSettings();

        var resolved = VlessServersResolver.Resolve(settings);

        // EXACTLY the 3 subscription servers — legacy entries dropped
        Assert.Equal(3, resolved.Count);

        // Placeholder + stale test entries must NOT appear in the result
        Assert.DoesNotContain(resolved, s => s.Server == StasPlaceholderServer);
        Assert.DoesNotContain(resolved, s =>
            s.Name == "khunrath_ln" || s.Name == "is-01-grpc-test");

        // Reality pubkey from placeholder must NOT appear either
        Assert.DoesNotContain(resolved, s => s.Reality?.PublicKey == StasPlaceholderPubkey);

        // The 3 subscription server hostnames must all be present
        Assert.Contains(resolved, s => s.Server == "104.194.156.93");
        Assert.Contains(resolved, s => s.Server == "93.95.226.167");
        Assert.Contains(resolved, s => s.Server == "194.87.222.111");

        // Side effect: settings.Vless.Servers is mutated to the scoped list
        Assert.Equal(3, settings.Vless.Servers.Count);
        Assert.DoesNotContain(settings.Vless.Servers, s => s.Name == "khunrath_ln");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Required test case #2: no subscriptions → fallback to legacy
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_NoSubscriptions_FallsBackToVlessServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>() // empty
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("A", "manual1.example.com"),
                    MakeServer("B", "manual2.example.com")
                },
                ActiveServer = "A"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("manual1.example.com", resolved[0].Server);
        Assert.Equal("manual2.example.com", resolved[1].Server);

        // ActiveServer untouched — "A" is in the legacy list, not stale
        Assert.Equal("A", settings.Vless.ActiveServer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Required test case #3: stale active_server → fallback + warn
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_StaleActiveServer_FallsBackToFirstScoped()
    {
        // Exactly stas's case: active_server="khunrath_ln" (not in subscriptions)
        var settings = BuildStasEvidenceSettings(activeServer: "khunrath_ln");

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);

        // ActiveServer overwritten with first subscription entry
        Assert.Equal("de-01 443 Khunrath", settings.Vless.ActiveServer);

        // Cross-check: GetActiveServers (used downstream by ConfigGenerator)
        // now correctly resolves to the subscription IP, NOT the placeholder.
        var activeServers = settings.Vless.GetActiveServers();
        Assert.NotEmpty(activeServers);
        Assert.Equal("104.194.156.93", activeServers[0].Server);
        Assert.NotEqual(StasPlaceholderServer, activeServers[0].Server);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Required test case #4: disabled subscription → fallback to legacy
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_DisabledSubscription_FallsBackToVlessServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "disabled-sub",
                        Url = "https://example.com/sub",
                        Enabled = false, // ← disabled
                        Servers = new List<VlessServerEntry>
                        {
                            MakeServer("sub-A", "sub.example.com")
                        }
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("manual", "manual.example.com")
                },
                ActiveServer = "manual"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        // Disabled subs do not count as "active subscription"; fallback to legacy
        Assert.Single(resolved);
        Assert.Equal("manual.example.com", resolved[0].Server);
        Assert.DoesNotContain(resolved, s => s.Server == "sub.example.com");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extra coverage: enabled-but-empty subscription is treated as fallback
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_EnabledSubscriptionWithoutServers_FallsBackToVlessServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "fresh-not-refreshed",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>() // empty — not refreshed yet
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("manual", "manual.example.com")
                },
                ActiveServer = "manual"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        // Enabled-but-empty subscription is NOT yet authoritative → fallback
        // to Vless.Servers so the user still has a working config rather than
        // an empty list.
        Assert.Single(resolved);
        Assert.Equal("manual.example.com", resolved[0].Server);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extra coverage: subscribe mode still benefits from scope guard
    // (active_subscription_server resolution + fallback when stale).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SubscribeMode_StaleActiveServer_FallsBackToFirstScoped()
    {
        var settings = BuildStasEvidenceSettings();
        settings.App.ConfigMode = "subscribe";
        settings.Vless.ActiveServer = "khunrath_ln"; // stale name

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);
        Assert.Equal("de-01 443 Khunrath", settings.Vless.ActiveServer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extra coverage: scoped active_server stays put when valid
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_ValidActiveServer_NotOverwritten()
    {
        var settings = BuildStasEvidenceSettings(activeServer: "nk-01 8443 Khunrath");

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);
        // Active was already in scope → should be kept as-is
        Assert.Equal("nk-01 8443 Khunrath", settings.Vless.ActiveServer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // r7 Bug-r10-E regression — brat case (2026-05-11)
    //
    // brat had a subscription + a Free Configs entry that he explicitly
    // clicked, which triggers ReconnectAsync.ManualVless: forced
    // ConfigMode=generated, Vless.Servers=[US entry], ActiveServer=US name.
    // Pre-r7 the scope guard would fire (isGenerated + hasSubs = true),
    // silently overwrite Vless.Servers with subscription pool + reset
    // ActiveServer to subscription's first → US never connected, UI
    // showed subscription IP. r7 fix: when active entry is in
    // vless.servers AND is NOT a known placeholder, treat as legitimate
    // manual choice and respect it.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedMode_LegitimateManualChoice_RespectsUserSelection_BratRegression()
    {
        // brat-shaped state: generated mode, subscription enabled with 7
        // working servers, AND vless.servers has 1 real Free-Configs entry
        // that user explicitly chose (active points to it).
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "main-brat",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            MakeServer("de-01 443 main-brat", "1.2.3.4", 443),
                            MakeServer("is-01 443 main-brat", "5.6.7.8", 443),
                        }
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    // Real Free-Configs entry — real IP, real pubkey (NOT in
                    // KnownPlaceholderPubkeys), real short_id (NOT in
                    // KnownPlaceholderShortIds).
                    new()
                    {
                        Name = "⚡ [US] 193.233.217.174:443",
                        Server = "193.233.217.174",
                        Port = 443,
                        Uuid = "f0e1d2c3-1234-5678-9abc-def012345678",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true,
                            ServerName = "www.cloudflare.com",
                            Fingerprint = "chrome",
                            PublicKey = "free-config-real-pubkey-not-placeholder",
                            ShortId = "deadbeef"
                        }
                    }
                },
                ActiveServer = "⚡ [US] 193.233.217.174:443"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        // Should respect the user's manual choice — return vless.servers
        // (the 1 US entry), not subscription pool.
        Assert.Single(resolved);
        Assert.Equal("193.233.217.174", resolved[0].Server);
        Assert.Equal("⚡ [US] 193.233.217.174:443", resolved[0].Name);

        // ActiveServer must NOT be silently swapped to subscription
        Assert.Equal("⚡ [US] 193.233.217.174:443", settings.Vless.ActiveServer);

        // Vless.Servers must NOT have been clobbered with subscription pool
        Assert.Single(settings.Vless.Servers);
        Assert.Equal("193.233.217.174", settings.Vless.Servers[0].Server);
        Assert.DoesNotContain(settings.Vless.Servers, s => s.Server == "1.2.3.4");
    }

    [Fact]
    public void GeneratedMode_PlaceholderActiveEvenIfInVlessServers_FallsBackToSubscription()
    {
        // Edge case between stas (placeholder, swap) and brat (real, keep):
        // confirms that even if the placeholder entry is in vless.servers
        // AND active points at it, we STILL swap to subscription because
        // the entry matches a known-placeholder pattern (server IP +
        // pubkey + short_id from F-E ConfigSanityCheck data).
        var settings = BuildStasEvidenceSettings(activeServer: "khunrath_ln");

        var resolved = VlessServersResolver.Resolve(settings);

        // Should NOT respect the placeholder — subscription wins
        Assert.Equal(3, resolved.Count);
        Assert.DoesNotContain(resolved, s => s.Server == StasPlaceholderServer);
        Assert.NotEqual("khunrath_ln", settings.Vless.ActiveServer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // P1.8 — subscribe-mode active-selector drift (audit handoff)
    // ─────────────────────────────────────────────────────────────────────────

    private static AppSettings SubscribeWith(string activeSubName, params (string Name, string Ip)[] servers)
        => new()
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = activeSubName,
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "sub",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = servers.Select(s => MakeServer(s.Name, s.Ip, 443)).ToList(),
                    },
                },
            },
            Vless = new VlessConfig { ActiveServer = activeSubName, Servers = new List<VlessServerEntry>() },
        };

    [Fact]
    public void SubscribeMode_StaleActiveSubscriptionServer_CorrectedInBothSelectors()
    {
        // ActiveSubscriptionServer points at a server the refresh dropped; the resolver
        // falls Vless.ActiveServer back to the first scoped server — and P1.8 must move
        // App.ActiveSubscriptionServer (the Subscribe-UI authoritative name) WITH it.
        var settings = SubscribeWith("old-server", ("new-server", "1.2.3.4"));

        VlessServersResolver.Resolve(settings);

        Assert.Equal("new-server", settings.Vless.ActiveServer);
        Assert.Equal("new-server", settings.App.ActiveSubscriptionServer);   // no drift
    }

    [Fact]
    public void SubscribeMode_InScopeActiveSubscriptionServer_Unchanged()
    {
        // Already-valid selector must not be rewritten to scoped[0].
        var settings = SubscribeWith("new-server", ("new-server", "1.2.3.4"), ("other", "5.6.7.8"));

        VlessServersResolver.Resolve(settings);

        Assert.Equal("new-server", settings.Vless.ActiveServer);
        Assert.Equal("new-server", settings.App.ActiveSubscriptionServer);
    }
}
