using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// LeakProtection
// ═══════════════════════════════════════════════════════════════════════════════

public class LeakProtectionTests
{
    private static SingBoxConfig CreateValidConfig()
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
                    Server = "1.2.3.4",
                    ServerPort = 443,
                    Uuid = "test-uuid"
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

    [Fact]
    public void ValidConfig_Passes()
    {
        var config = CreateValidConfig();
        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void InvalidDnsStrategy_Fails()
    {
        var config = CreateValidConfig();
        config.Dns.Strategy = "prefer_ipv4";

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("strategy"));
    }

    [Fact]
    public void StrictRouteTrue_Fails()
    {
        var config = CreateValidConfig();
        config.Inbounds[0].StrictRoute = true;

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("strict_route"));
    }

    [Fact]
    public void MissingProxyOutbound_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("proxy"));
    }

    [Fact]
    public void MissingDirectOutbound_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "proxy", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("direct"));
    }

    [Fact]
    public void EmptyVlessServer_Fails()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Server = "";

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("server is empty"));
    }

    [Fact]
    public void EmptyVlessUuid_Fails()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Uuid = "";

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("uuid is empty"));
    }

    [Fact]
    public void UrltestWithOneChild_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-0", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-0" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at least 2"));
    }

    [Fact]
    public void UrltestWithValidChildren_Passes()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-main", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid-1" },
            new() { Type = "vless", Tag = "vless-backup", Server = "5.6.7.8", ServerPort = 443, Uuid = "uuid-2" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-main", "vless-backup" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void UrltestWithNonexistentChild_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-0", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-0", "vless-ghost" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("vless-ghost"));
    }

    [Fact]
    public void MissingDnsHijackRule_WarnsButPasses()
    {
        var config = CreateValidConfig();
        config.Route.Rules = config.Route.Rules
            .Where(r => r.Action != "hijack-dns")
            .ToList();

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("hijack-dns"));
    }

    [Fact]
    public void ProcessRoutedButNoDnsRule_WarnsAboutLeak()
    {
        var config = CreateValidConfig();
        config.Dns.Rules.Clear();

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid); // warnings don't cause failure
        Assert.Contains(result.Warnings, w => w.Contains("DNS may leak"));
    }

    // ───────────────────────────────────────────────────────────────────────
    // v2.31.5-r1+: extra coverage for protocol-aware dispatch (v2.30.1-r4)
    // and smart-mode DNS leak check (v2.31.x). These pin behaviour that the
    // older tests above didn't reach because they exercise only the VLESS
    // protocol branch.
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hysteria2_ValidConfig_Passes()
    {
        // Sanity: a non-VLESS proxy with all required fields validates
        // green. Pre-r4 (when ValidateVlessOutbound ran unconditionally)
        // this would have failed with "uuid is empty" because Hy2 has no
        // uuid by spec.
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new()
            {
                Type = "hysteria2",
                Tag = "proxy",
                Server = "1.2.3.4",
                ServerPort = 443,
                Password = "secret"
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Hysteria2_EmptyPassword_Fails()
    {
        // Hy2-specific required field. The protocol-aware validator
        // dispatches to ValidateHysteria2Outbound which checks Password —
        // a regression that reverts to the VLESS-only path would also
        // miss this branch.
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new()
            {
                Type = "hysteria2",
                Tag = "proxy",
                Server = "1.2.3.4",
                ServerPort = 443,
                Password = ""
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("password is empty"));
    }

    [Fact]
    public void Tuic_EmptyUuid_Fails()
    {
        // TUIC needs uuid (like VLESS) but optionally password (unlike
        // Shadowsocks). Pins the TUIC dispatch branch independently from
        // the VLESS-uuid test above.
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new()
            {
                Type = "tuic",
                Tag = "proxy",
                Server = "1.2.3.4",
                ServerPort = 443,
                Uuid = ""
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("TUIC", StringComparison.OrdinalIgnoreCase) && e.Contains("uuid"));
    }

    [Fact]
    public void MixedProtocolUrltest_VlessAndHysteria2_Passes()
    {
        // v2.30.1-r4 regression sentinel. Pre-r4: a urltest selector
        // containing both vless:// and hy2:// children failed validation
        // because the Hy2 child got run through ValidateVlessOutbound
        // and rejected for "uuid is empty". User report 2026-05-01.
        // Fix dispatched validation by outbound type per child; this
        // test pins that behaviour so a regression is caught at unit
        // level, not in the wild.
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-1", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid-1" },
            new() { Type = "hysteria2", Tag = "hy2-1", Server = "5.6.7.8", ServerPort = 443, Password = "secret" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-1", "hy2-1" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void SmartMode_LocalDnsServer_DoesNotWarnAboutLeak()
    {
        // v2.31.x dns_mode="smart" regression sentinel. Smart mode
        // routes process DNS to local-dns (resolves via direct, but
        // through TLS so still leak-resistant). Pre-fix the leak check
        // only accepted "vpn-dns" as a valid DNS rule target, so smart
        // mode unconditionally fired "DNS may leak" — confusing because
        // the config was actually fine.
        var config = CreateValidConfig();
        config.Dns.Rules[0].Server = "local-dns";

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("DNS may leak"));
    }

    [Fact]
    public void FullTunnel_DnsFinalNotVpnDns_WarnsButPasses()
    {
        // Full-tunnel mode (route.final="proxy") expects DNS to also
        // route through the proxy by default. If dns.final lands on
        // anything other than vpn-dns, we want a noisy warning so the
        // user can make an informed call about whether the leak is
        // intentional (rare) or a misconfig (common).
        var config = CreateValidConfig();
        config.Route.Final = "proxy";
        config.Dns.Final = "local-dns";

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid); // warnings don't block startup
        Assert.Contains(result.Warnings, w =>
            w.Contains("Full tunnel") || w.Contains("DNS may bypass"));
    }

    [Fact]
    public void ProxyUdp_AlsoValidated_FailsOnInvalidChild()
    {
        // Both "proxy" (TCP) and optional "proxy-udp" (UDP via Hy2/TUIC)
        // get validated. A regression that drops the proxy-udp branch
        // would let a malformed UDP outbound slip through and only
        // surface as a sing-box startup error — ValidateConfig is meant
        // to catch it at the pre-flight gate.
        var config = CreateValidConfig();
        config.Outbounds.Add(new SingBoxOutbound
        {
            Type = "vless",
            Tag = "proxy-udp",
            Server = "1.2.3.4",
            ServerPort = 443,
            Uuid = ""  // intentionally bad
        });

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("proxy-udp") && e.Contains("uuid"));
    }

    // ─── Bug-r9-F-DEFENSIVE (2026-05-11) — outbound IP cross-check ────────
    //
    // The outbound-IP check fires when an AppSettings is supplied. It walks
    // every proxy-like outbound and compares its `server` to a scope-aware
    // allow-list. Bug-r10-F-D (2026-05-11) refined the original Bug-r9-F-2
    // union-based check into per-config_mode scoping:
    //   - generated/subscribe + enabled subs → subscription servers ONLY
    //     (legacy vless.servers ignored — see stas's leak in
    //      plans/r10-stas-confirmed-and-apps-2mode.md §1).
    //   - generated/subscribe + no subs → vless.servers fallback.
    //   - custom → only check proxy outbound presence + well-formed.
    //
    // The two cases below retain the post-r9 semantics, but with stricter
    // severity (Error instead of Warning) when the scope is subscription —
    // a placeholder leak there is a P0 silent-traffic issue, not a
    // warning-on-startup affordance.

    [Fact]
    public void OutboundIpNotInSubscriptions_EmitsError()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Server = "195.135.255.216";  // intentionally stale / unknown

        var settings = new AppSettings();
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "104.194.156.93", Port = 443 },
                new() { Server = "194.87.222.111", Port = 443 },
            }
        });

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid); // F-D promotes to Error in sub scope
        Assert.Contains(result.Errors, e =>
            e.Contains("195.135.255.216")
            && (e.Contains("subscription") || e.Contains("scope") || e.Contains("legacy")));
    }

    [Fact]
    public void OutboundIpInSubscriptions_NoWarning()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Server = "104.194.156.93";  // matches sub-1's first entry

        var settings = new AppSettings();
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "104.194.156.93", Port = 443 },
                new() { Server = "194.87.222.111", Port = 443 },
            }
        });

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("not in your VLESS server list"));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("subscription scope") || e.Contains("legacy"));
    }
}
