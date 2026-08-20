using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Bug-r10-F-D (2026-05-11) regression pin: <see cref="LeakProtection.ValidateConfig"/>
/// scope-aware validation. Refines the post-r9 union-based defensive check
/// (which let stas-class leaks pass) into a per-<c>config_mode</c> contract:
///
/// <list type="bullet">
///   <item><c>generated</c> + enabled subscriptions → outbound MUST come from
///     a subscription. Legacy <c>vless.servers[]</c> entries are NOT trusted.
///     Mismatch is a critical Error.</item>
///   <item><c>generated</c> + no subscriptions → fall back to
///     <c>vless.servers[]</c> as allow-list (legacy direct VLESS). Mismatch
///     is a Warning (existing behaviour).</item>
///   <item><c>custom</c> → only check proxy outbound presence + well-formed
///     <c>(server, server_port)</c>. Don't compare against
///     <c>config.yaml</c> because user pasted the JSON directly.</item>
/// </list>
///
/// <para>Reference: <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §1 Fix-D
/// + <c>plans/stas-evidence-config.yaml</c> + <c>plans/stas-evidence-current.json</c>.</para>
/// </summary>
public sealed class LeakProtectionScopeAwareTests
{
    // ───────── helpers ────────────────────────────────────────────────────

    private static SingBoxConfig CreateValidConfig(
        string proxyServer = "1.2.3.4",
        int proxyPort = 443,
        string proxyUuid = "test-uuid")
    {
        return new SingBoxConfig
        {
            Dns = new SingBoxDns
            {
                Strategy = "ipv4_only",
                Final = "local-dns",
                Servers = new List<DnsServer>
                {
                    new() { Tag = "vpn-dns", Type = "https", Server = "1.1.1.1", Detour = "proxy" },
                    new() { Tag = "local-dns", Type = "local" }
                },
                Rules = new List<DnsRule>
                {
                    new() { ProcessName = new List<string> { "Discord.exe" }, Action = "route", Server = "vpn-dns" }
                }
            },
            Inbounds = new List<SingBoxInbound>
            {
                new()
                {
                    Type = "tun",
                    Tag = "tun-in",
                    StrictRoute = false,
                    Address = new List<string> { "172.19.0.1/30" }
                }
            },
            Outbounds = new List<SingBoxOutbound>
            {
                new()
                {
                    Type = "vless",
                    Tag = "proxy",
                    Server = proxyServer,
                    ServerPort = proxyPort,
                    Uuid = proxyUuid,
                },
                new() { Type = "direct", Tag = "direct" }
            },
            Route = new SingBoxRoute
            {
                Rules = new List<RouteRule>
                {
                    new() { Action = "sniff", Timeout = "300ms" },
                    new() { Protocol = "dns", Action = "hijack-dns" },
                    new()
                    {
                        ProcessName = new List<string> { "Discord.exe" },
                        Action = "route",
                        Outbound = "proxy"
                    }
                },
                Final = "direct"
            }
        };
    }

    /// <summary>
    /// Stas-evidence fixture, distilled. Generated mode + one enabled
    /// subscription with 2 real servers + legacy <c>vless.servers</c>
    /// entries pointing at a placeholder dead IP.
    /// </summary>
    private static AppSettings BuildStasLikeSettings()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";

        // Real subscription (de-01 + is-01 from stas's config).
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "simple",
            Url = "https://example.com/api/v1/app/config/abc",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "de-01 443 Khunrath",
                    Server = "104.194.156.93",
                    Port = 443,
                    Uuid = "9029d44f-232f-4283-b055-d39f8448f43b",
                },
                new()
                {
                    Name = "is-01 443 Khunrath",
                    Server = "93.95.226.167",
                    Port = 443,
                    Uuid = "b9c26f53-d1bf-4f8e-8aa4-68684aa0e0f0",
                },
            }
        });

        // Legacy placeholder still floating in vless.servers[].
        settings.Vless.Server = "195.135.255.216";
        settings.Vless.Port = 443;
        settings.Vless.Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "khunrath_ln",
                Server = "195.135.255.216",
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
            },
        };
        settings.Vless.ActiveServer = "khunrath_ln";

        return settings;
    }

    // ───────── 1) generated + enabled subs: legacy IP → FAIL ──────────────

    [Fact]
    public void GeneratedMode_WithSubscription_LegacyVlessServerOutbound_FailsValidation()
    {
        // Exact stas reproduction: generated mode, enabled subscription has
        // working servers, but the outbound points at the legacy
        // vless.servers[] placeholder. Pre-F-D the union-based check
        // missed this (placeholder was in the union). F-D scopes the
        // allow-list to subscription-only and elevates to Error.
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "195.135.255.216",
            proxyPort: 443,
            proxyUuid: "352714f4-7ecc-4c22-805f-ed5c5239f5bb");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("195.135.255.216")
            && (e.Contains("scope") || e.Contains("legacy") || e.Contains("subscription")));
    }

    // ───────── 2) generated + enabled subs: matching IP → PASS ────────────

    [Fact]
    public void GeneratedMode_WithSubscription_ValidOutbound_Passes()
    {
        var settings = BuildStasLikeSettings();
        // Outbound matches sub's de-01 entry exactly (server + port + uuid).
        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "9029d44f-232f-4283-b055-d39f8448f43b");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("scope") || e.Contains("legacy vless.servers") ||
            e.Contains("subscription"));
    }

    // ───────── 3) generated + NO subs: legacy fallback allowed ────────────

    [Fact]
    public void GeneratedMode_NoSubscriptions_VlessServerOutbound_Passes()
    {
        // Legacy direct-VLESS user (pre-subscription days). No enabled
        // subscriptions → vless.servers is the only source of truth.
        // Outbound matches a vless.servers entry → allowed.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "test-uuid",
            },
        };

        var config = CreateValidConfig(
            proxyServer: "1.2.3.4",
            proxyPort: 443,
            proxyUuid: "test-uuid");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("not in your VLESS server list"));
    }

    // ───────── 4) custom mode — well-formed outbound passes ───────────────

    [Fact]
    public void CustomMode_WellFormedProxyOutbound_Passes()
    {
        // User pasted full JSON via custom mode. We must NOT compare its
        // server IP against config.yaml — that would force them to also
        // register the server in the Servers tab (UX confusion + spurious
        // false-positive flags).
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";

        // Some random IP not in any vless.servers / subscriptions.
        var config = CreateValidConfig(
            proxyServer: "203.0.113.42",  // TEST-NET-3
            proxyPort: 443,
            proxyUuid: "custom-uuid");

        var result = LeakProtection.ValidateConfig(config, settings);

        // In custom mode we tolerate any server / port / uuid combo as
        // long as the proxy outbound is well-formed. Some existing
        // protocol-level validators may still flag the test-uuid in
        // ValidateConcreteOutbound (e.g. VLESS uuid). The scope-aware
        // path itself should NOT add errors here.
        Assert.DoesNotContain(result.Errors, e => e.Contains("scope")
            || e.Contains("legacy") || e.Contains("config_mode=custom"));
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("not in your VLESS server list"));
    }

    // ───────── 5) custom mode — missing proxy outbound → FAIL ─────────────

    [Fact]
    public void CustomMode_MissingProxyOutbound_Fails()
    {
        // User pasted JSON without a proxy outbound. Route rules pointing
        // at "proxy" would silently fail to direct → privacy leak.
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";

        var config = CreateValidConfig();
        // Drop the proxy outbound entirely (keep direct only).
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "direct", Tag = "direct" },
        };

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("config_mode=custom") && e.Contains("proxy"));
    }

    // ───────── 6) custom mode — empty server in proxy → FAIL ──────────────

    [Fact]
    public void CustomMode_EmptyProxyServer_Fails()
    {
        // proxy outbound exists but server field is empty — sing-box would
        // refuse to start, but we catch it earlier with a clearer message.
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";

        var config = CreateValidConfig(proxyServer: "");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("config_mode=custom")
            && (e.Contains("server") || e.Contains("empty")));
    }

    // ───────── 7) scope-aware uuid mismatch on same IP → FAIL ─────────────

    [Fact]
    public void GeneratedMode_WithSubscription_SameIpDifferentUuid_FailsValidation()
    {
        // Defense-in-depth: same IP, different uuid → probably a different
        // physical server / placeholder. F-D matches on (server, port,
        // uuid) tuple so this should fail.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Server = "104.194.156.93",
                    Port = 443,
                    Uuid = "9029d44f-232f-4283-b055-d39f8448f43b",
                },
            }
        });

        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "00000000-0000-0000-0000-000000000000");  // wrong uuid

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("104.194.156.93")
            && (e.Contains("scope") || e.Contains("legacy")));
    }

    // ───────── 8) cached subscription servers count as subscription scope ─

    [Fact]
    public void GeneratedMode_CachedSubscriptionServersAllowed()
    {
        // SubscriptionResolver populates App.SubscriptionServers during
        // offline startup. They're same-trust-tier as live subs and
        // therefore part of the subscription-scope allow-list.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.SubscriptionServers = new List<VlessServerEntry>
        {
            new()
            {
                Server = "10.20.30.40",
                Port = 443,
                Uuid = "cached-uuid",
            },
        };

        var config = CreateValidConfig(
            proxyServer: "10.20.30.40",
            proxyPort: 443,
            proxyUuid: "cached-uuid");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ─── G-1 (r10 r9 audit) brat-class union: generated + sub + non-placeholder vless.servers ───
    //
    // The r7 fix that unblocked brat (manual Free Config entry in
    // generated mode with sub enabled) needs an explicit "what the user
    // would check" assertion: their picked entry passes validation.
    // Pre-r7 union returned ONLY subscription; brat's IP was rejected.
    // r7 added: in generated mode, also include non-placeholder
    // vless.servers entries in the allowed list. Stas-class placeholders
    // are still rejected via VlessServersResolver.IsPlaceholderEntry.
    //
    // This test pins THAT specific behaviour (the
    // GeneratedMode_WithSubscription_ValidOutbound_Passes test above
    // happens to exercise the same path, but its setup uses subscription
    // server as outbound — not a manual vless.servers entry. Brat's
    // case uses a manual entry that ISN'T in any subscription, which is
    // semantically different).
    [Fact]
    public void GeneratedMode_WithSubscription_NonPlaceholderManualVlessServer_Passes_BratRegression()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        // Subscription has 2 working servers
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main-brat",
                Url = "https://example.com/sub",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    new() { Name = "de-01 main-brat", Server = "1.2.3.4", Port = 443, Uuid = "sub-uuid-1" },
                }
            }
        };
        // User added a Free Config manually — real IP, real pubkey, NOT
        // in any subscription, NOT a placeholder.
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "⚡ [US] 193.233.217.174:443",
                Server = "193.233.217.174",
                Port = 443,
                Uuid = "real-uuid-free-config",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "real-pubkey-not-placeholder",
                    ShortId = "deadbeef"
                }
            }
        };
        settings.Vless.ActiveServer = "⚡ [US] 193.233.217.174:443";

        var config = CreateValidConfig(
            proxyServer: "193.233.217.174",
            proxyPort: 443,
            proxyUuid: "real-uuid-free-config");

        var result = LeakProtection.ValidateConfig(config, settings);

        // The whole point of r7 fix — should PASS, not Error
        Assert.True(result.IsValid, "Validation should pass for legitimate manual Free Config entry in generated mode. Errors: " + string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("193.233.217.174")
            && (e.Contains("scope") || e.Contains("legacy") || e.Contains("subscription")));
    }

    // ─── DNS-tunnel (slipstream) loopback proxy outbound — exempt from scope ───
    //
    // v2.42.0 regression: the DNS-tunnel transport rewrites the proxy outbound
    // to target the local slipstream-client front (127.0.0.1:7001); the REAL
    // server is reached THROUGH that local client (validated by
    // SlipstreamManager from the dns-tunnel profile, not by LeakProtection).
    // Pre-fix the scope-aware check saw "127.0.0.1:7001 not in the active
    // subscription scope" and hard-failed VpnEngine.StartAsync (user's exact
    // "Latvia DNS ~main-brat" log, 2026-06-11). A loopback target can't carry
    // traffic off-box, so it's a fail-closed local relay, never a remote leak —
    // and is now exempt from the subscription-server allow-list.

    [Fact]
    public void GeneratedMode_WithSubscription_DnsTunnelLoopbackOutbound_Passes()
    {
        // Reproduces the user's scenario: generated mode, an enabled
        // subscription with real servers, but the proxy outbound is the
        // DNS-tunnel local front (127.0.0.1:7001) carrying the real UUID.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "main-brat",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Name = "lv-01 main-brat", Server = "213.155.15.93", Port = 443, Uuid = "sub-uuid-lv" },
            }
        });

        // BuildDnsTunnelOutbound shape: vless over 127.0.0.1:7001, real uuid.
        var config = CreateValidConfig(
            proxyServer: "127.0.0.1",
            proxyPort: 7001,
            proxyUuid: "sub-uuid-lv");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid,
            "DNS-tunnel loopback proxy outbound must pass scope validation. Errors: "
            + string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("127.0.0.1")
            && (e.Contains("scope") || e.Contains("legacy") || e.Contains("subscription")));
    }

    [Theory]
    [InlineData("127.0.0.1")]   // IPv4 loopback (the dns-tunnel default)
    [InlineData("127.5.6.7")]   // anywhere in 127.0.0.0/8
    [InlineData("::1")]         // IPv6 loopback
    [InlineData("localhost")]   // hostname form
    public void GeneratedMode_WithSubscription_LoopbackVariants_AllExempt(string loopback)
    {
        // Lock the IsLoopbackServer helper's coverage across every loopback
        // spelling a local-front transport might emit, so none of them trips
        // the subscription-scope leak error.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "1.2.3.4", Port = 443, Uuid = "sub-uuid" },
            }
        });

        var config = CreateValidConfig(proxyServer: loopback, proxyPort: 7001, proxyUuid: "any");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("scope") || e.Contains("not in the active subscription"));
    }

    // ─── AWG Endpoint scope validation ───

    [Fact]
    public void GeneratedMode_WithSubscription_AwgEndpointOutOfScope_FailsValidation()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "9029d44f-232f-4283-b055-d39f8448f43b");

        // Add AWG endpoint pointing to out-of-scope server
        config.Endpoints = new List<SingBoxEndpoint>
        {
            new()
            {
                Type = "wireguard",
                Tag = "proxy-awg",
                Address = new List<string> { "10.66.0.2/32" },
                PrivateKey = "aPrivateKeyBase64==",
                Peers = new List<WireGuardPeer>
                {
                    new() { Address = "203.0.113.99", Port = 51820, PublicKey = "peerPubKey==" }
                }
            }
        };

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("203.0.113.99") && e.Contains("AWG endpoint") && e.Contains("active subscription"));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_AwgEndpointInScope_PassesValidation()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "9029d44f-232f-4283-b055-d39f8448f43b");

        // Add AWG endpoint pointing to in-scope server from BuildStasLikeSettings (104.194.156.93:443)
        config.Endpoints = new List<SingBoxEndpoint>
        {
            new()
            {
                Type = "wireguard",
                Tag = "proxy-awg",
                Address = new List<string> { "10.66.0.2/32" },
                PrivateKey = "aPrivateKeyBase64==",
                Peers = new List<WireGuardPeer>
                {
                    new() { Address = "104.194.156.93", Port = 443, PublicKey = "peerPubKey==" }
                }
            }
        };

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }
}
