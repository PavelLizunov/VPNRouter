#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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

    // ── edge 1: legacy type variants (null, empty, "legacy") ─────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    public void Inject_LegacyFakeIp_TypeVariants_AllMigrate(string? typeValue)
    {
        var typeField = typeValue == null ? "\"type\": null" : $"\"type\": \"{typeValue}\"";
        var json = """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip", __TYPE__ },
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
        """.Replace("__TYPE__", typeField);

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeipServer);
        Assert.Equal("fakeip", (string?)fakeipServer!["type"]);
        Assert.Null(fakeipServer["address"]);
        Assert.Equal("198.18.0.0/15", (string?)fakeipServer["inet4_range"]);
    }

    // ── edge 1: legacy unsupported options requiring manual migration ────────

    [Theory]
    [InlineData("strategy", "\"strategy\": \"prefer_ipv4\"")]
    [InlineData("address_resolver", "\"address_resolver\": \"local-dns\"")]
    [InlineData("address_strategy", "\"address_strategy\": \"prefer_ipv4\"")]
    [InlineData("client_subnet", "\"client_subnet\": \"1.2.3.4/24\"")]
    public void Inject_LegacyFakeIp_UnsupportedOptions_ThrowsActionableException(string fieldName, string optionField)
    {
        var json = """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip", __OPTION__ },
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
        """.Replace("__OPTION__", optionField);

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("Legacy FakeIP DNS server uses options requiring manual migration:", ex.Message);
        Assert.Contains(fieldName, ex.Message);
    }

    [Theory]
    [InlineData("strategy")]
    [InlineData("address_resolver")]
    [InlineData("address_strategy")]
    [InlineData("client_subnet")]
    public void Inject_LegacyFakeIp_ExplicitNullOption_MigratesWithoutError(string fieldName)
    {
        var json = """
        {
          "dns": {
            "servers": [
              { "tag": "fakeip-dns", "address": "fakeip", "__KEY__": null },
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
        """.Replace("__KEY__", fieldName);

        var root = InjectConfig(json);
        Assert.Null(root["dns"]?["fakeip"]);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeip = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeip);
        Assert.Equal("fakeip", (string?)fakeip!["type"]);
        Assert.False(fakeip.ContainsKey(fieldName));
        Assert.False(fakeip.ContainsKey("strategy"));
    }

    [Fact]
    public void Inject_LegacyFakeIp_AllExplicitNullOptions_RemovesAllLegacyKeys()
    {
        var json = """
        {
          "dns": {
            "servers": [
              {
                "tag": "fakeip-dns",
                "address": "fakeip",
                "strategy": null,
                "address_resolver": null,
                "address_strategy": null,
                "client_subnet": null
              },
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
        var fakeip = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "fakeip-dns");
        Assert.NotNull(fakeip);
        Assert.Equal("fakeip", (string?)fakeip!["type"]);
        Assert.False(fakeip.ContainsKey("strategy"));
        Assert.False(fakeip.ContainsKey("address_resolver"));
        Assert.False(fakeip.ContainsKey("address_strategy"));
        Assert.False(fakeip.ContainsKey("client_subnet"));
    }

    // ── edge 2: full-mode vpnrouter-vpn-dns collision ────────────────────────

    [Fact]
    public void Inject_FullMode_ReservedVpnDnsTagCollision_ThrowsActionableException()
    {
        var json = """
        {
          "dns": {
            "servers": [
              { "tag": "vpnrouter-vpn-dns", "type": "fakeip", "inet4_range": "198.18.0.0/15" },
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
        settings.App.RoutingMode = "full";
        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json, settings));
        Assert.Contains("vpnrouter-vpn-dns", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("choose another FakeIP server tag", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── edge 2: bootstrap vpnrouter-dns-direct collision ─────────────────────

    [Fact]
    public void Inject_BootstrapDomainResolver_ReservedDnsDirectTagCollision_ThrowsActionableException()
    {
        var json = """
        {
          "dns": {
            "servers": [
              { "tag": "vpnrouter-dns-direct", "type": "fakeip", "inet4_range": "198.18.0.0/15" }
            ]
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "example.com", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => InjectConfig(json));
        Assert.Contains("vpnrouter-dns-direct", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("choose another FakeIP server tag", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inject_BootstrapDomainResolver_ExistingLocalDnsPresent_DoesNotThrowForDnsDirectTag()
    {
        var json = """
        {
          "dns": {
            "servers": [
              { "tag": "vpnrouter-dns-direct", "type": "fakeip", "inet4_range": "198.18.0.0/15" },
              { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
            ]
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "example.com", "server_port": 443, "uuid": "test" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var root = InjectConfig(json);
        Assert.NotNull(root);
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);
        var fakeipServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "vpnrouter-dns-direct");
        Assert.NotNull(fakeipServer);
        Assert.Equal("fakeip", (string?)fakeipServer!["type"]);
    }

    // ── Native sing-box check: VPNCTL_TEST_CORE integration ─────────────────

    public static IEnumerable<object[]> Native_MigratedFakeIp_Cases => new[]
    {
        new object[]
        {
            "Native_MigratedFakeIp_DisabledFalse_OldDnsObject",
            """
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
                { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 9 },
                { "type": "direct", "tag": "direct" }
              ]
            }
            """
        },
        new object[]
        {
            "Native_MigratedFakeIp_EnabledV4_Legacy",
            """
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
                { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 9 },
                { "type": "direct", "tag": "direct" }
              ]
            }
            """
        },
        new object[]
        {
            "Native_MigratedFakeIp_EnabledBoth_Legacy",
            """
            {
              "dns": {
                "servers": [
                  { "tag": "fakeip-dns", "address": "fakeip" },
                  { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
                ],
                "fakeip": {
                  "enabled": true,
                  "inet4_range": "198.18.0.0/15",
                  "inet6_range": "fc00::/18"
                }
              },
              "outbounds": [
                { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 9 },
                { "type": "direct", "tag": "direct" }
              ]
            }
            """
        },
        new object[]
        {
            "Native_MigratedFakeIp_TypedAlready",
            """
            {
              "dns": {
                "servers": [
                  { "tag": "fakeip-dns", "type": "fakeip", "inet4_range": "198.18.0.0/15", "inet6_range": "fc00::/18" },
                  { "tag": "local-dns", "type": "udp", "server": "8.8.8.8" }
                ]
              },
              "outbounds": [
                { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 9 },
                { "type": "direct", "tag": "direct" }
              ]
            }
            """
        }
    };

    [Theory]
    [MemberData(nameof(Native_MigratedFakeIp_Cases))]
    public async Task Native_MigratedFakeIp_Check(string caseName, string rawJson)
    {
        _ = caseName;
        var coreExe = Environment.GetEnvironmentVariable("VPNCTL_TEST_CORE");
        Assert.SkipWhen(string.IsNullOrEmpty(coreExe), "VPNCTL_TEST_CORE environment variable not provided");
        Assert.True(File.Exists(coreExe), $"VPNCTL_TEST_CORE executable does not exist at {coreExe}");

        var injectedJson = CustomConfigInjector.Inject(rawJson, Array.Empty<string>(), CreateSettings());
        var tempConfig = Path.Combine(Path.GetTempPath(), $"vpnrouter-native-fakeip-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(tempConfig, injectedJson);

            var psi = new ProcessStartInfo
            {
                FileName = coreExe,
                Arguments = $"check -c \"{tempConfig}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            Assert.NotNull(proc);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"sing-box check timed out after 15s for: {coreExe}");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(proc.ExitCode == 0, $"sing-box check failed with exit code {proc.ExitCode}:\nStdout:\n{stdout}\nStderr:\n{stderr}");
        }
        finally
        {
            if (File.Exists(tempConfig))
            {
                try { File.Delete(tempConfig); } catch { }
            }
        }
    }
}
