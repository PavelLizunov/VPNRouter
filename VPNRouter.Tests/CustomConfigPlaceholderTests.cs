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

    [Fact]
    public void Inject_PlaceholderShortId_Throws()
    {
        var json = BuildConfig(shortId: PlaceholderShortId);

        var ex = Assert.Throws<PlaceholderConfigException>(() =>
            CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings()));

        Assert.Equal("reality.short_id", ex.OffendingField);
        Assert.Equal(PlaceholderShortId, ex.OffendingValue);
    }

    [Fact]
    public void Inject_PlaceholderServerIp_Throws()
    {
        var json = BuildConfig(server: PlaceholderServer);

        var ex = Assert.Throws<PlaceholderConfigException>(() =>
            CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings()));

        Assert.Equal("server", ex.OffendingField);
        Assert.Equal(PlaceholderServer, ex.OffendingValue);
    }

    [Fact]
    public void Inject_CleanConfig_PassesThrough()
    {
        var json = BuildConfig();

        var result = CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings());

        Assert.False(string.IsNullOrWhiteSpace(result));
        var parsed = JsonObject.Parse(result);
        Assert.Contains("\"Discord.exe\"", result);
        Assert.NotNull(parsed["route"]);
    }

    [Fact]
    public void Inject_PlaceholderInSecondOutbound_FirstProxyOnly()
    {
        var config = new JsonObject
        {
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["tag"] = "local", ["type"] = "udp", ["server"] = "1.0.0.1" },
                },
                ["rules"] = new JsonArray(),
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct",
                },
                new JsonObject
                {
                    ["type"] = "block",
                    ["tag"] = "block",
                },
                new JsonObject
                {
                    ["type"] = "vless",
                    ["tag"] = "proxy",
                    ["server"] = CleanServer,
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
                            ["public_key"] = PlaceholderPubkey,
                            ["short_id"] = CleanShortId,
                        },
                    },
                },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(),
                ["final"] = "direct",
            },
        };

        var ex = Assert.Throws<PlaceholderConfigException>(() =>
            CustomConfigInjector.Inject(config.ToString(), new[] { "Discord.exe" }, CreateSettings()));

        Assert.Equal("reality.public_key", ex.OffendingField);
    }

    [Fact]
    public void Inject_NoProxyOutbound_DoesNotThrow_ForPlaceholder()
    {
        var config = new JsonObject
        {
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["tag"] = "local", ["type"] = "udp", ["server"] = "1.0.0.1" },
                },
                ["rules"] = new JsonArray(),
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(),
                ["final"] = "direct",
            },
        };

        var caught = Record.Exception(() =>
            CustomConfigInjector.Inject(config.ToString(), new[] { "Discord.exe" }, CreateSettings()));

        Assert.False(caught is PlaceholderConfigException,
            $"Expected no PlaceholderConfigException, got: {caught?.GetType().Name}: {caught?.Message}");
    }

    [Fact]
    public void ConfigSanityCheck_InspectOutbound_PublicHelperMatches()
    {
        var pubkeyOutbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = CleanServer,
            ["tls"] = new JsonObject
            {
                ["reality"] = new JsonObject
                {
                    ["public_key"] = PlaceholderPubkey,
                    ["short_id"] = CleanShortId,
                },
            },
        };
        var shortIdOutbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = CleanServer,
            ["tls"] = new JsonObject
            {
                ["reality"] = new JsonObject
                {
                    ["public_key"] = CleanPubkey,
                    ["short_id"] = PlaceholderShortId,
                },
            },
        };
        var serverOutbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = PlaceholderServer,
            ["tls"] = new JsonObject
            {
                ["reality"] = new JsonObject
                {
                    ["public_key"] = CleanPubkey,
                    ["short_id"] = CleanShortId,
                },
            },
        };
        var cleanOutbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = CleanServer,
            ["tls"] = new JsonObject
            {
                ["reality"] = new JsonObject
                {
                    ["public_key"] = CleanPubkey,
                    ["short_id"] = CleanShortId,
                },
            },
        };

        Assert.Equal("reality.public_key", ConfigSanityCheck.InspectOutbound(pubkeyOutbound));
        Assert.Equal("reality.short_id", ConfigSanityCheck.InspectOutbound(shortIdOutbound));
        Assert.Equal("server", ConfigSanityCheck.InspectOutbound(serverOutbound));
        Assert.Null(ConfigSanityCheck.InspectOutbound(cleanOutbound));

        Assert.Equal(
            PlaceholderDefense.Inspect(PlaceholderPubkey, CleanShortId, CleanServer),
            ConfigSanityCheck.InspectOutbound(pubkeyOutbound));
        Assert.Equal(
            PlaceholderDefense.Inspect(CleanPubkey, PlaceholderShortId, CleanServer),
            ConfigSanityCheck.InspectOutbound(shortIdOutbound));
        Assert.Equal(
            PlaceholderDefense.Inspect(CleanPubkey, CleanShortId, PlaceholderServer),
            ConfigSanityCheck.InspectOutbound(serverOutbound));
    }
}
