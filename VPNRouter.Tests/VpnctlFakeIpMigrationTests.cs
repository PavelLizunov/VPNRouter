#nullable enable

using System;
using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Unit tests for VPNCTL-06 fakeip migration in <see cref="CustomConfigInjector"/>.
/// Tests migration of legacy global dns.fakeip to sing-box 1.12+ typed format,
/// rejection of conflicts and malformed definitions, preservation of pools and rules,
/// and prevention of detour: "dns-direct" injection onto fakeip servers.
/// </summary>
public class VpnctlFakeIpMigrationTests
{
    private static AppSettings CreateSettings() => new()
    {
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" }
    };

    private static JsonObject InjectConfig(string rawJson, AppSettings? settings = null)
    {
        var result = CustomConfigInjector.Inject(rawJson, Array.Empty<string>(), settings ?? CreateSettings());
        return JsonNode.Parse(result)!.AsObject();
    }

    // ── disabled: enabled:false ──────────────────────────────────────────────

    [Fact]
    public void Inject_FakeIpDisabled_NoLegacyServer_RemovesGlobalFakeIpObject()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": false
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        Assert.Equal("local-dns", (string?)servers![0]?["tag"]);
    }

    [Fact]
    public void Inject_FakeIpDisabled_WithLegacyServer_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": false
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("legacy fakeip", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("false", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_FakeIpEmptyObject_NoLegacyServer_TreatedAsDisabledAndRemovesObject()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {}
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        Assert.False(root["dns"]!.AsObject().ContainsKey("fakeip"));
    }

    [Fact]
    public void Inject_FakeIpEmptyObject_WithLegacyServer_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {}
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("legacy fakeip", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("false", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_FakeIpNull_NoLegacyServer_RemovesExplicitNull()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": null
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        Assert.False(root["dns"]!.AsObject().ContainsKey("fakeip"));
    }

    [Fact]
    public void Inject_FakeIpNull_WithLegacyServer_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": null
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── enabled: explicit ranges, tags, rules, and reference preservation ────

    [Fact]
    public void Inject_FakeIpEnabled_ExplicitRangesAndTagAndRules_Preserved()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "my-fakeip-tag", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "rules": [
              { "domain_suffix": ["example.com"], "server": "my-fakeip-tag" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.1.0/24",
              "inet6_range": "fc00:1::/32"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);

        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "my-fakeip-tag");
        Assert.NotNull(fakeipServer);
        Assert.Equal("fakeip", (string?)fakeipServer!["type"]);
        Assert.Null(fakeipServer["address"]);
        Assert.Equal("198.18.1.0/24", (string?)fakeipServer["inet4_range"]);
        Assert.Equal("fc00:1::/32", (string?)fakeipServer["inet6_range"]);
        Assert.Null(fakeipServer["detour"]);

        var rules = root["dns"]?["rules"]?.AsArray();
        Assert.NotNull(rules);
        var fakeipRule = rules!.OfType<JsonObject>().FirstOrDefault(r => (string?)r["server"] == "my-fakeip-tag");
        Assert.NotNull(fakeipRule);
        var suffixes = fakeipRule!["domain_suffix"]?.AsArray();
        Assert.NotNull(suffixes);
        Assert.Equal("example.com", (string?)suffixes![0]);
    }

    [Fact]
    public void Inject_FakeIpEnabled_ExplicitV4Only_NoSynthesizedV6()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.0.0/15"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);

        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Equal("fakeip", (string?)fakeipServer!["type"]);
        Assert.Equal("198.18.0.0/15", (string?)fakeipServer["inet4_range"]);
        Assert.Null(fakeipServer["inet6_range"]);
        Assert.Null(fakeipServer["detour"]);
    }

    [Fact]
    public void Inject_FakeIpEnabled_OmittedRanges_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("requires at least one of 'inet4_range' or 'inet6_range'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── typed: no extra dialer / detour, no remote DNS transport classification ──

    [Fact]
    public void Inject_TypedFakeIp_NoExtraDialerOrDetour_NotClassifiedAsRemoteTransport()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "type": "fakeip", "inet4_range": "198.18.0.0/15", "inet6_range": "fc00::/18" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ]
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var settings = CreateSettings();
        settings.App.RoutingMode = "full"; // wantRemoteDns = true
        var root = InjectConfig(json, settings);

        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Null(fakeipServer!["detour"]);

        // dns.final must NOT be set to fakeip-dns
        var finalDns = (string?)root["dns"]?["final"];
        Assert.NotEqual("fakeip-dns", finalDns);
    }

    // ── mixed conflict ───────────────────────────────────────────────────────

    [Fact]
    public void Inject_MixedLegacyAndTypedFakeIp_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-legacy", "address": "fakeip" },
              { "tag": "fakeip-typed", "type": "fakeip", "inet4_range": "198.18.0.0/15", "inet6_range": "fc00::/18" }
            ],
            "fakeip": {
              "enabled": true
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("mixed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_TypedFakeIp_ConflictingGlobalRange_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "type": "fakeip", "inet4_range": "198.18.0.0/16", "inet6_range": "fc00::/18" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.0.0/15",
              "inet6_range": "fc00::/18"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("Conflicting", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IPv4", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_TypedFakeIp_CompatibleGlobalRange_KeepsAuthoredOptionsAndRemovesGlobal()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "type": "fakeip", "inet4_range": "198.18.0.0/15", "inet6_range": "fc00::/18" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.0.0/15",
              "inet6_range": "fc00::/18"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Equal("198.18.0.0/15", (string?)fakeipServer!["inet4_range"]);
        Assert.Equal("fc00::/18", (string?)fakeipServer["inet6_range"]);
    }

    [Fact]
    public void Inject_TypedFakeIp_MissingRangesInTyped_FilledFromGlobalOnly_PreservesOmitted()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "type": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.5.0/24"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Equal("198.18.5.0/24", (string?)fakeipServer!["inet4_range"]);
        Assert.Null(fakeipServer["inet6_range"]);
    }

    [Fact]
    public void Inject_TypedFakeIp_MissingBothRangesInTyped_FilledFromGlobal()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "type": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.0.0/15",
              "inet6_range": "fc00::/18"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Equal("198.18.0.0/15", (string?)fakeipServer!["inet4_range"]);
        Assert.Equal("fc00::/18", (string?)fakeipServer["inet6_range"]);
    }

    [Fact]
    public void Inject_TypedFakeIp_MalformedNonStringRange_ExplicitlyRejected()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "type": "fakeip", "inet4_range": 123 },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.0.0/15"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("must be a valid IPv4 CIDR", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── malformed enabled / ranges ───────────────────────────────────────────

    [Theory]
    [InlineData("""{"dns": {"fakeip": "invalid"}, "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""", "expected a JSON object")]
    [InlineData("""{"dns": {"servers": [{"tag": "f", "address": "fakeip"}], "fakeip": {"enabled": "true"}}, "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""", "must be a boolean")]
    [InlineData("""{"dns": {"servers": [{"tag": "f", "address": "fakeip"}], "fakeip": {"enabled": null}}, "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""", "must be a boolean")]
    [InlineData("""{"dns": {"servers": [{"tag": "f", "address": "fakeip"}], "fakeip": {"enabled": true, "inet4_range": 123}}, "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""", "must be a valid IPv4 CIDR")]
    [InlineData("""{"dns": {"servers": [{"tag": "f", "address": "fakeip"}], "fakeip": {"enabled": true, "inet6_range": true}}, "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""", "must be a valid IPv6 CIDR")]
    [InlineData("""{"dns": {"servers": [{"tag": "f", "address": "fakeip"}], "fakeip": {"enabled": true, "inet4_range": "not-a-cidr"}}, "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""", "must be a valid IPv4 CIDR")]
    public void Inject_MalformedFakeIp_ThrowsActionableException(string json, string expectedSubstring)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains(expectedSubstring, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── idempotent ───────────────────────────────────────────────────────────

    [Fact]
    public void Inject_FakeIpMigration_IsIdempotent()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true,
              "inet4_range": "198.18.2.0/24",
              "inet6_range": "fc00:2::/32"
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var firstResult = CustomConfigInjector.Inject(json, Array.Empty<string>(), CreateSettings());
        var root1 = JsonNode.Parse(firstResult)!.AsObject();
        Assert.Null(root1["dns"]?["fakeip"]);

        var secondResult = CustomConfigInjector.Inject(firstResult, Array.Empty<string>(), CreateSettings());
        var root2 = JsonNode.Parse(secondResult)!.AsObject();
        Assert.Null(root2["dns"]?["fakeip"]);

        var fakeipServer = root2["dns"]?["servers"]?.AsArray()?.OfType<JsonObject>()
            .FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Equal("fakeip", (string?)fakeipServer!["type"]);
        Assert.Equal("198.18.2.0/24", (string?)fakeipServer["inet4_range"]);
        Assert.Equal("fc00:2::/32", (string?)fakeipServer["inet6_range"]);
        Assert.Null(fakeipServer["detour"]);
    }

    // ── absent fakeip / generated no addition ────────────────────────────────

    [Fact]
    public void Inject_AbsentFakeIp_NoFakeIpServer_NoAddition()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ]
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        Assert.DoesNotContain(servers!.OfType<JsonObject>(), s => (string?)s["type"] == "fakeip");
    }

    [Fact]
    public void Inject_AbsentFakeIp_WithLegacyServer_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ]
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("absent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_FakeIpEnabled_WithoutServer_ThrowsActionableException()
    {
        var json = /*lang=json*/ """
        {
          "dns": {
            "servers": [
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ],
            "fakeip": {
              "enabled": true
            }
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("no fakeip DNS server", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
