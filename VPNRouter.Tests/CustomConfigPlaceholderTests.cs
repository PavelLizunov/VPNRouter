using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class CustomConfigPlaceholderTests
{
    private const string PlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string PlaceholderShortId = "78ca7952";
    private const string PlaceholderServer = "195.135.255.216";

    private const string CleanPubkey = "RealGoodPubKeyFromValidSub_abc123";
    private const string CleanShortId = "abcd1234";
    private const string CleanServer = "194.87.222.111";

    private static AppSettings CreateSettings() => new()
    {
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" }
    };

    private static string BuildConfig(
        string pubkey = CleanPubkey,
        string shortId = CleanShortId,
        string server = CleanServer)
    {
        var config = new JsonObject
        {
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "remote",
                        ["type"] = "https",
                        ["server"] = "1.1.1.1",
                        ["detour"] = "proxy",
                    },
                    new JsonObject
                    {
                        ["tag"] = "local",
                        ["type"] = "udp",
                        ["server"] = "1.0.0.1",
                    },
                },
                ["rules"] = new JsonArray(),
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "vless",
                    ["tag"] = "proxy",
                    ["server"] = server,
                    ["server_port"] = 443,
                    ["uuid"] = "2d54442d-158f-49e2-b225-67ba1a5b77f4",
                    ["flow"] = "xtls-rprx-vision",
                    ["tls"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["server_name"] = "yahoo.com",
                        ["reality"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["public_key"] = pubkey,
                            ["short_id"] = shortId,
                        },
                    },
                },
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct",
                },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(),
                ["final"] = "direct",
            },
        };
        return config.ToString();
    }

    [Fact]
    public void Inject_PlaceholderPubkey_ThrowsPlaceholderConfigException()
    {
        var json = BuildConfig(pubkey: PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() =>
            CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings()));

        Assert.Equal("reality.public_key", ex.OffendingField);
        Assert.Equal(PlaceholderPubkey, ex.OffendingValue);
    }
}
