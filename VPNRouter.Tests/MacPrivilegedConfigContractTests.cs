using System.Text.Json.Nodes;
using VPNRouter.Core.Platform.macOS;
using Xunit;

namespace VPNRouter.Tests;

public class MacPrivilegedConfigContractTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vpnrouter-mac-contract-" + Guid.NewGuid().ToString("N"));
    private readonly string _geo;
    private readonly string _cache;

    public MacPrivilegedConfigContractTests()
    {
        _geo = Path.Combine(_root, "geo");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_geo);
        Directory.CreateDirectory(_cache);
        File.WriteAllBytes(Path.Combine(_geo, "geo.srs"), new byte[] { (byte)'S', (byte)'R', (byte)'S', 5, 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_cache, "ad.srs"), new byte[] { (byte)'S', (byte)'R', (byte)'S', 5, 1, 2, 3, 4 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string BuildConfig(string? ruleSetPath = null, string controller = "127.0.0.1:9090")
    {
        var ruleSet = ruleSetPath is null
            ? string.Empty
            : $",\"rule_set\":[{{\"type\":\"local\",\"format\":\"binary\",\"tag\":\"geo\",\"path\":{System.Text.Json.JsonSerializer.Serialize(ruleSetPath)}}}]";
        return
            "{" +
            "\"log\":{\"level\":\"info\",\"output\":\"/Users/alice/Library/Logs/singbox.log\"}," +
            "\"experimental\":{" +
            "\"cache_file\":\"/Users/alice/cache.db\"," +
            "\"services\":[]," +
            "\"clash_api\":{\"external_controller\":\"" + controller + "\",\"secret\":\"0123456789abcdef0123456789abcdef\",\"external_ui\":\"/Users/alice/ui\"}}," +
            "\"inbounds\":[{\"type\":\"tun\",\"tag\":\"tun-in\"}]," +
            "\"route\":{\"final\":\"direct\"" + ruleSet + "}," +
            "\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]" +
            "}";
    }

    [Fact]
    public void Prepare_strips_root_write_paths_and_moves_rule_sets_to_root_assets()
    {
        var source = Path.Combine(_geo, "geo.srs");
        var prepared = MacPrivilegedConfigContract.Prepare(BuildConfig(source), 501, _geo, _cache);
        var root = JsonNode.Parse(prepared)!.AsObject();

        Assert.Equal(string.Empty, root["log"]!.AsObject()["output"]!.GetValue<string>());
        var experimental = root["experimental"]!.AsObject();
        Assert.Null(experimental["cache_file"]);
        Assert.Null(experimental["external_ui"]);
        Assert.Null(experimental["services"]);
        Assert.Equal("127.0.0.1:9090",
            experimental["clash_api"]!.AsObject()["external_controller"]!.GetValue<string>());
        var secret = experimental["clash_api"]!.AsObject()["secret"]!.GetValue<string>();
        Assert.True(secret.Length >= 32);
        Assert.Equal(secret,
            JsonNode.Parse(MacPrivilegedConfigContract.EnsureControllerSecret(prepared))!
                ["experimental"]!["clash_api"]!["secret"]!.GetValue<string>());

        var path = root["route"]!.AsObject()["rule_set"]!.AsArray()[0]!.AsObject()["path"]!.GetValue<string>();
        Assert.Equal(MacPrivilegePaths.UserAssetPath(501, "geo.srs"), path);
        Assert.True(MacPrivilegePaths.IsRootAssetPath(path, 501));
    }

    [Theory]
    [InlineData("0.0.0.0:9090")]
    [InlineData("192.168.1.2:9090")]
    [InlineData("example.com:9090")]
    public void Prepare_rejects_non_loopback_controller(string controller)
    {
        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(BuildConfig(controller: controller), 501, _geo, _cache));
    }

    [Theory]
    [InlineData("127.0.0.1:9090")]
    [InlineData("[::1]:9090")]
    [InlineData("localhost:9090")]
    public void Loopback_controller_is_accepted(string controller)
    {
        Assert.True(MacPrivilegedConfigContract.IsLoopbackController(controller));
    }

    [Fact]
    public void Prepare_rejects_non_tun_inbound()
    {
        var json = BuildConfig().Replace(
            "{\"type\":\"tun\",\"tag\":\"tun-in\"}",
            "{\"type\":\"mixed\",\"tag\":\"mixed-in\"}",
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_rule_set_outside_geo_and_cache()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside.srs");
        File.WriteAllBytes(outside, new byte[] { (byte)'S', (byte)'R', (byte)'S', 5, 1, 2, 3, 4 });
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                MacPrivilegedConfigContract.Prepare(BuildConfig(outside), 501, _geo, _cache));
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void Prepare_rejects_missing_rule_set()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(
                BuildConfig(Path.Combine(_geo, "missing.srs")), 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_certificate_paths()
    {
        var json = BuildConfig().Replace(
            "\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]",
            "\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\",\"tls\":{\"certificate_path\":\"/Users/alice/cert.pem\"}}]",
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_output_outside_log()
    {
        var json = BuildConfig().Replace(
            "\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]",
            "\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\",\"output\":\"/tmp/x\"}]",
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_unmanaged_experimental_api()
    {
        var json = BuildConfig().Replace(
            "\"clash_api\":",
            "\"v2ray_api\":{},\"clash_api\":",
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_accepts_generated_runtime_components()
    {
        const string json = """
            {
              "inbounds": [{"type":"tun"}],
              "outbounds": [
                {"type":"turnrelay"},{"type":"dnstt"},{"type":"ssh"},{"type":"direct"},{"type":"urltest"},
                {"type":"vless"},{"type":"vmess"},{"type":"hysteria2"},{"type":"tuic"},
                {"type":"shadowsocks"},{"type":"trusttunnel"},{"type":"mieru"},{"type":"selector"}
              ],
              "endpoints": [{"type":"wireguard"}],
              "dns": {"servers":[{"type":"https"},{"type":"local"}]}
            }
            """;

        Assert.NotEmpty(MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Theory]
    [InlineData(""" "outbounds":[{"type":"fixture-unsupported"}] """)]
    [InlineData(""" "outbounds":[{"type":"direct","private_key_path":""}] """)]
    [InlineData(""" "outbounds":[{"type":"direct","Protect_Path":""}] """)]
    [InlineData(""" "outbounds":[{"type":"shadowsocks","plugin":"fixture"}] """)]
    [InlineData(""" "outbounds":[{"type":"shadowsocks","plugin_opts":""}] """)]
    [InlineData(""" "outbounds":[{"type":"shadowsocks","Plugin":"fixture"}] """)]
    [InlineData(""" "outbounds":[{"type":"shadowsocks","Plugin_Opts":""}] """)]
    [InlineData(""" "Outbounds":[{"type":"direct"}] """)]
    [InlineData(""" "outbounds":[{"Type":"direct"}] """)]
    [InlineData(""" "outbounds":[{}] """)]
    [InlineData(""" "outbounds":{} """)]
    [InlineData(""" "endpoints":[{"type":"fixture-unsupported"}] """)]
    [InlineData(""" "dns":{"servers":[{"type":"fixture-unsupported"}]} """)]
    [InlineData(""" "dns":{"Servers":[{"type":"https"}]} """)]
    [InlineData(""" "dns":null """)]
    [InlineData(""" "certificate_providers":[] """)]
    [InlineData(""" "log":null """)]
    [InlineData(""" "route":null """)]
    [InlineData(""" "route":{"rule_set":null} """)]
    [InlineData(""" "route":{"dhcp_lease_files":[]} """)]
    [InlineData(""" "experimental":{"debug":{}} """)]
    [InlineData(""" "experimental":{"clash_api":{"cache_file":""}} """)]
    [InlineData(""" "experimental":{"clash_api":{"cache_id":""}} """)]
    [InlineData(""" "experimental":{"clash_api":{"store_fakeip":false}} """)]
    public void Prepare_rejects_unapproved_runtime_components(string property)
    {
        var json = "{\"inbounds\":[{\"type\":\"tun\"}]," + property + "}";

        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Controller_secret_is_added_once_and_preserved()
    {
        var first = MacPrivilegedConfigContract.EnsureControllerSecret(BuildConfig());
        var secret = JsonNode.Parse(first)!["experimental"]!["clash_api"]!["secret"]!
            .GetValue<string>();

        Assert.True(secret.Length >= 32);
        Assert.Equal(secret,
            JsonNode.Parse(MacPrivilegedConfigContract.EnsureControllerSecret(first))!
                ["experimental"]!["clash_api"]!["secret"]!.GetValue<string>());
    }

    [Fact]
    public void Short_controller_secret_is_replaced()
    {
        var json = BuildConfig().Replace(
            "\"secret\":\"0123456789abcdef0123456789abcdef\"",
            "\"secret\":\"short\"",
            StringComparison.Ordinal);
        var prepared = MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache);
        var secret = JsonNode.Parse(prepared)!["experimental"]!["clash_api"]!["secret"]!
            .GetValue<string>();

        Assert.True(secret.Length >= 32);
        Assert.NotEqual("short", secret);
    }

    [Fact]
    public void Prepare_rejects_escaped_object_key()
    {
        var json = BuildConfig().Replace("\"log\"", "\"l\\u006fg\"", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
        Assert.Contains("canonical", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"log\"", "\"Log\"")]
    [InlineData("\"output\"", "\"Output\"")]
    [InlineData("\"external_controller\"", "\"External_Controller\"")]
    [InlineData("\"secret\"", "\"Secret\"")]
    public void Prepare_rejects_noncanonical_structural_key_casing(string canonical, string variant)
    {
        var json = BuildConfig().Replace(canonical, variant, StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
        Assert.Contains("structural", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_rejects_variant_structural_key_beside_canonical_key()
    {
        var json = BuildConfig().Insert(1, "\"Inbounds\":[],");

        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_preserves_non_structural_key_casing_and_values()
    {
        const string json = """
            {
              "inbounds": [{"type":"tun"}],
              "outbounds": [{"type":"vless","transport":{"type":"ws",
                "headers":{"Host":"example.com","X-Trace":"MiXeD Inbounds"}
              }}]
            }
            """;

        var prepared = MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache);
        var root = JsonNode.Parse(prepared)!.AsObject();
        var headers = root["outbounds"]![0]!["transport"]!["headers"]!;

        Assert.Equal("example.com", headers["Host"]!.GetValue<string>());
        Assert.Equal("MiXeD Inbounds", headers["X-Trace"]!.GetValue<string>());
    }

    [Fact]
    public void Prepare_rejects_a_key_escaped_by_the_output_serializer()
    {
        var json = BuildConfig().Insert(1, "\"dns\":{\"label<key\":1},");

        var error = Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
        Assert.Contains("canonical", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Prepare_enforces_the_helper_nesting_limit(int depth, bool accepted)
    {
        var nested = new string('[', depth - 2) + "0" + new string(']', depth - 2);
        var json = BuildConfig().Insert(1, "\"dns\":{\"fixture\":" + nested + "},");

        if (accepted)
            Assert.NotEmpty(MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
        else
            Assert.Throws<InvalidOperationException>(() =>
                MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_duplicate_object_keys()
    {
        var json = BuildConfig().Replace(
            "\"external_controller\":\"127.0.0.1:9090\"",
            "\"external_controller\":\"127.0.0.1:9090\"," +
            "\"external_controller\":\"127.0.0.1:9090\"",
            StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
        Assert.Contains("duplicate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_rejects_duplicate_keys_inside_arrays()
    {
        var json = BuildConfig().Replace(
            "\"type\":\"tun\",\"tag\":\"tun-in\"",
            "\"type\":\"tun\",\"type\":\"tun\",\"tag\":\"tun-in\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_non_ascii_object_key()
    {
        var json = BuildConfig().Replace("\"log\"", "\"l\u00f6g\"", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_control_character_in_key()
    {
        var json = BuildConfig().Replace("\"log\"", "\"lo\\u0009g\"", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_rejects_empty_key()
    {
        var json = BuildConfig().Replace("\"log\"", "\"\"", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache));
    }

    [Fact]
    public void Prepare_preserves_valid_text_values()
    {
        var json = BuildConfig().Replace(
            "\"tag\":\"tun-in\"",
            "\"tag\":\"tun-in\\u0020\u00e9\"",
            StringComparison.Ordinal);

        var prepared = MacPrivilegedConfigContract.Prepare(json, 501, _geo, _cache);
        var root = JsonNode.Parse(prepared)!.AsObject();

        Assert.Equal("tun-in \u00e9",
            root["inbounds"]!.AsArray()[0]!.AsObject()["tag"]!.GetValue<string>());
    }
}
