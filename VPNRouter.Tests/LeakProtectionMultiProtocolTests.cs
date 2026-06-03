using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// LeakProtection — v2.30.1-r4 per-protocol outbound validation
//
// Pre-r4 the validator unconditionally called ValidateVlessOutbound on every
// urltest child, rejecting valid Hysteria2 / TUIC / SS entries with bogus
// "uuid is empty" errors. The fix dispatches by outbound type.
// ═══════════════════════════════════════════════════════════════════════════════

public class LeakProtectionMultiProtocolTests
{
    private static VPNRouter.Core.Models.SingBoxConfig BaseConfig()
    {
        // Minimal config with sniff/hijack-dns/private route prefix and
        // a TUN inbound — covers the "well-formed config skeleton" the
        // validator's other checks expect.
        return new VPNRouter.Core.Models.SingBoxConfig
        {
            Log = new VPNRouter.Core.Models.SingBoxLog { Level = "info" },
            Dns = new VPNRouter.Core.Models.SingBoxDns
            {
                Servers = new List<VPNRouter.Core.Models.DnsServer>
                {
                    new() { Tag = "vpn-dns", Type = "udp", Server = "1.1.1.1" },
                    new() { Tag = "local-dns", Type = "udp", Server = "8.8.8.8" }
                },
                Rules = new List<VPNRouter.Core.Models.DnsRule>
                {
                    new() { Action = "route", Server = "vpn-dns" }
                }
            },
            Inbounds = new List<VPNRouter.Core.Models.SingBoxInbound>
            {
                new() { Type = "tun", Tag = "tun-in", Address = new List<string> { "172.19.0.1/30" }, StrictRoute = false }
            },
            Route = new VPNRouter.Core.Models.SingBoxRoute
            {
                Rules = new List<VPNRouter.Core.Models.RouteRule>
                {
                    new() { Action = "sniff" },
                    new() { Action = "hijack-dns", Protocol = "dns" },
                    new() { IpIsPrivate = true, Action = "route", Outbound = "direct" }
                },
                Final = "proxy",
                AutoDetectInterface = true,
                DefaultDomainResolver = "local-dns"
            },
            Outbounds = new List<VPNRouter.Core.Models.SingBoxOutbound>
            {
                new() { Type = "direct", Tag = "direct" },
                new() { Type = "direct", Tag = "dns-direct", UdpFragment = true }
            },
            Experimental = new VPNRouter.Core.Models.SingBoxExperimental()
        };
    }

    [Fact]
    public void Hysteria2_AsSingleProxyOutbound_PassesValidation()
    {
        var cfg = BaseConfig();
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "hysteria2",
            Tag = "proxy",
            Server = "1.2.3.4",
            ServerPort = 443,
            Password = "pw",
            Tls = new VPNRouter.Core.Models.TlsConfig { Enabled = true, ServerName = "x.com" }
        });

        var result = VPNRouter.Core.Services.LeakProtection.ValidateConfig(cfg);
        Assert.True(result.IsValid, "expected hysteria2 outbound to validate cleanly. Errors: " + string.Join("; ", result.Errors));
    }

    [Fact]
    public void Tuic_AsSingleProxyOutbound_PassesValidation()
    {
        var cfg = BaseConfig();
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "tuic",
            Tag = "proxy",
            Server = "1.2.3.4",
            ServerPort = 443,
            Uuid = "abc-uuid",
            Password = "pw",
            CongestionControl = "bbr",
            UdpRelayMode = "native",
            Tls = new VPNRouter.Core.Models.TlsConfig { Enabled = true, ServerName = "x.com", Alpn = new List<string> { "h3" } }
        });

        var result = VPNRouter.Core.Services.LeakProtection.ValidateConfig(cfg);
        Assert.True(result.IsValid, "expected tuic outbound to validate. Errors: " + string.Join("; ", result.Errors));
    }

    [Fact]
    public void Shadowsocks_AsSingleProxyOutbound_PassesValidation()
    {
        var cfg = BaseConfig();
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "shadowsocks",
            Tag = "proxy",
            Server = "1.2.3.4",
            ServerPort = 8388,
            // v2.40.0-r9 (#8): a standard AEAD cipher (SS2022 key-length validation is
            // covered by its own property test). The placeholder password is fine here.
            Method = "aes-256-gcm",
            Password = "secret"
        });

        var result = VPNRouter.Core.Services.LeakProtection.ValidateConfig(cfg);
        Assert.True(result.IsValid, "expected shadowsocks outbound to validate. Errors: " + string.Join("; ", result.Errors));
    }

    [Fact]
    public void Urltest_WithMixedProtocols_AllChildrenValidatedByOwnRules()
    {
        // Repro of the user-reported bug: urltest with VLESS + Hysteria2
        // children. Pre-r4 the validator ran VLESS rules on the Hy2 child
        // and complained about the missing uuid.
        var cfg = BaseConfig();
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "vless",
            Tag = "vless-main",
            Server = "1.1.1.1",
            ServerPort = 443,
            Uuid = "uuid-1",
        });
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "hysteria2",
            Tag = "vless-hy2-test",  // tag prefix is misleading by design (cosmetic)
            Server = "2.2.2.2",
            ServerPort = 9443,
            Password = "hy2pw",
        });
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "urltest",
            Tag = "proxy",
            Outbounds = new List<string> { "vless-main", "vless-hy2-test" },
            Url = "http://www.gstatic.com/generate_204",
            Interval = "3m",
            Tolerance = 150,
        });

        var result = VPNRouter.Core.Services.LeakProtection.ValidateConfig(cfg);
        Assert.True(result.IsValid, "expected mixed VLESS+Hy2 urltest to validate. Errors: " + string.Join("; ", result.Errors));
    }

    [Fact]
    public void Hysteria2_MissingPassword_IsRejected()
    {
        var cfg = BaseConfig();
        cfg.Outbounds.Add(new VPNRouter.Core.Models.SingBoxOutbound
        {
            Type = "hysteria2",
            Tag = "proxy",
            Server = "1.2.3.4",
            ServerPort = 443,
            // Password intentionally missing.
        });

        var result = VPNRouter.Core.Services.LeakProtection.ValidateConfig(cfg);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Hysteria2") && e.Contains("password"));
    }
}
