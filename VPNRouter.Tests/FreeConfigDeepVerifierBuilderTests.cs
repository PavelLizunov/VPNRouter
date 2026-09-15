using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FreeConfigDeepVerifierBuilderTests
{
    [Fact]
    public void BuildSingleOutboundConfig_TlsSecurity_EmitsTlsWithoutRealityBlock()
    {
        var entry = new VlessServerEntry
        {
            Server = "cloudflare.example.com",
            Port = 443,
            Uuid = "11111111-2222-3333-4444-555555555555",
            Security = "tls",
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = "my-sni.example.com",
            },
        };

        var json = FreeConfigDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090);
        using var doc = JsonDocument.Parse(json);
        var outbound = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("vless", outbound.GetProperty("type").GetString());
        var tls = outbound.GetProperty("tls");
        Assert.True(tls.GetProperty("enabled").GetBoolean());
        Assert.Equal("my-sni.example.com", tls.GetProperty("server_name").GetString());
        Assert.False(tls.TryGetProperty("reality", out _));
    }

    [Fact]
    public void BuildSingleOutboundConfig_RealitySecurity_EmitsRealityBlock()
    {
        var entry = new VlessServerEntry
        {
            Server = "reality.example.com",
            Port = 443,
            Uuid = "11111111-2222-3333-4444-555555555555",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "yahoo.com",
                PublicKey = "lQbgwNDYw6Zbjdim0JtXUarzb-3GSjDvtX6FJYZD9Qo",
                ShortId = "1234abcd",
            },
        };

        var json = FreeConfigDeepVerifier.BuildSingleOutboundConfig(entry, 10808, null);
        using var doc = JsonDocument.Parse(json);
        var outbound = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("vless", outbound.GetProperty("type").GetString());
        var tls = outbound.GetProperty("tls");
        Assert.True(tls.GetProperty("enabled").GetBoolean());
        Assert.True(tls.TryGetProperty("reality", out var reality));
        Assert.True(reality.GetProperty("enabled").GetBoolean());
        Assert.Equal("lQbgwNDYw6Zbjdim0JtXUarzb-3GSjDvtX6FJYZD9Qo", reality.GetProperty("public_key").GetString());
    }

    [Fact]
    public void BuildSingleOutboundConfig_Hysteria2_DispatchesToHysteria2Outbound()
    {
        var entry = new VlessServerEntry
        {
            Protocol = "hysteria2",
            Server = "hy2.example.com",
            Port = 8443,
            Password = "secret-password",
            Tls = new VlessTlsConfig { ServerName = "hy2.example.com" },
        };

        var json = FreeConfigDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090);
        using var doc = JsonDocument.Parse(json);
        var outbound = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("hysteria2", outbound.GetProperty("type").GetString());
        Assert.Equal("secret-password", outbound.GetProperty("password").GetString());
    }
}
