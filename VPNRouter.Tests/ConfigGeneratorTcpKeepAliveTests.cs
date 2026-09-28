#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConfigGeneratorTcpKeepAliveTests
{
    private static (Profile profile, AppSettings settings) BuildOneServerInputs()
    {
        var profile = new Profile
        {
            Name = "test",
            DnsMode = "vpn_only",
            BlockOnVpnFail = false,
        };
        var settings = new AppSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new VlessServerEntry
            {
                Name = "test-server",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "00000000-0000-0000-0000-000000000000",
                Flow = "xtls-rprx-vision",
                Tls = new VlessTlsConfig
                {
                    Enabled = true,
                    ServerName = "example.com",
                },
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "X1Y2Z3aBcDeFgHiJkLmNoPqRsTuVwXyZaBcDeFgHi",
                    ShortId = "abcd1234",
                },
            },
        };
        return (profile, settings);
    }

    [Fact]
    public void Model_SingBoxOutbound_HasTcpKeepAliveProperties()
    {
        var prop1 = typeof(SingBoxOutbound).GetProperty(nameof(SingBoxOutbound.TcpKeepAlive));
        var prop2 = typeof(SingBoxOutbound).GetProperty(nameof(SingBoxOutbound.TcpKeepAliveInterval));
        Assert.NotNull(prop1);
        Assert.NotNull(prop2);
        Assert.Equal(typeof(string), prop1!.PropertyType);
        Assert.Equal(typeof(string), prop2!.PropertyType);
    }

    [Fact]
    public void Generate_SingleVlessServer_OutboundHasTcpKeepAlive()
    {
        var (profile, settings) = BuildOneServerInputs();

        var config = ConfigGenerator.Generate(profile, System.Array.Empty<string>(), settings);
        var json = ConfigGenerator.Serialize(config);

        Assert.Contains("\"tcp_keep_alive\": \"30s\"", json);
        Assert.Contains("\"tcp_keep_alive_interval\": \"30s\"", json);
    }

    [Fact]
    public void Generate_JsonStructure_KeepAliveFieldsAreInsideVlessOutbound()
    {
        var (profile, settings) = BuildOneServerInputs();
        var config = ConfigGenerator.Generate(profile, System.Array.Empty<string>(), settings);
        var json = ConfigGenerator.Serialize(config);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("outbounds", out var outbounds));

        bool foundVlessOutbound = false;
        foreach (var ob in outbounds.EnumerateArray())
        {
            if (!ob.TryGetProperty("type", out var type)) continue;
            if (type.GetString() != "vless") continue;
            foundVlessOutbound = true;

            Assert.True(ob.TryGetProperty("tcp_keep_alive", out var ka),
                "VLESS outbound missing tcp_keep_alive");
            Assert.Equal("30s", ka.GetString());

            Assert.True(ob.TryGetProperty("tcp_keep_alive_interval", out var kai),
                "VLESS outbound missing tcp_keep_alive_interval");
            Assert.Equal("30s", kai.GetString());
        }
        Assert.True(foundVlessOutbound, "No vless outbound found in generated config");
    }
}
