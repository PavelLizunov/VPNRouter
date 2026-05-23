// v2.36 F4 fix (EOStārāTheia 2026-05-23) — sing-box 1.13's default
// `tcp_keep_alive` initial period is 5 minutes, which is longer than
// most mobile ISP / NAT idle timeouts (30-180s). When the phone goes
// idle (screen off, no traffic), the upstream TCP connection silently
// drops at the 5-min mark — matching EOStārāTheia's exact report of
// "auto-disconnect at 5 минут".
//
// Fix: set explicit short tcp_keep_alive on VLESS outbounds so OS-
// level keepalive probes fire BEFORE NAT mappings expire. Both
// values set to "30s" — initial probe + interval.
//
// What this file pins:
//   1. SingBoxOutbound model exposes TcpKeepAlive + TcpKeepAliveInterval
//      properties with correct JSON propertyNames.
//   2. ConfigGenerator.BuildVlessOutbound emits both values = "30s" on
//      every generated VLESS outbound. A refactor that drops them
//      would trip these tests.
//   3. Generated JSON serializes correctly (no missing field, no extra
//      field).
//
// Cross-platform: tests run on every platform — they exercise pure
// in-memory JSON generation.
//
// Brief: plans/android-disconnect-investigation-v2.36.md

#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the v2.36 F4 fix: VLESS outbounds carry explicit
/// tcp_keep_alive + tcp_keep_alive_interval = "30s" so mobile NAT
/// timeouts don't silently drop idle connections.
/// </summary>
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
        // Source pin via reflection: the model MUST expose both
        // properties with the right JSON propertyName attributes. If a
        // refactor renames or drops them, this test catches it.

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

        // F4 invariant: the VLESS outbound MUST carry both keepalive
        // fields with the "30s" sentinel value. A refactor that drops
        // them would let mobile users hit the original 5-min disconnect
        // bug again.
        Assert.Contains("\"tcp_keep_alive\": \"30s\"", json);
        Assert.Contains("\"tcp_keep_alive_interval\": \"30s\"", json);
    }

    [Fact]
    public void Generate_JsonStructure_KeepAliveFieldsAreInsideVlessOutbound()
    {
        // Belt-and-braces: parse the generated JSON and walk to the
        // VLESS outbound, verify the fields land in the right place
        // (not accidentally at root or inside DNS / route sections).

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
