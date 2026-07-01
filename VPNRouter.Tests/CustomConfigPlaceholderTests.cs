using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// CustomConfigInjector placeholder gate — Phase 2c (v2.32.3-r1, 2026-05-17)
//
// Pinning: a user who pastes a raw sing-box JSON into "Custom Config (JSON)" on
// the Servers page must NOT be allowed to ship a placeholder-bait outbound to
// sing-box. The first proxy-typed outbound (vless / hysteria2 / tuic /
// shadowsocks / trojan) is inspected via PlaceholderGuard at Inject time, and
// PlaceholderConfigException is thrown when any of:
//   - tls.reality.public_key matches KnownPlaceholderPubkeys (e.g. the Android
//     PlaceholderVlessUri smoke-test "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU")
//   - tls.reality.short_id matches KnownPlaceholderShortIds (e.g. "78ca7952")
//   - server matches KnownPlaceholderServers (e.g. "195.135.255.216")
//
// Single source of truth: ConfigSanityCheck.InspectOutbound — shared between
// runtime sanity check (CheckBeforeStart) and the input gate here.
// ═══════════════════════════════════════════════════════════════════════════════

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

    /// <summary>
    /// Builds a minimal but valid sing-box JSON with a single VLESS proxy
    /// outbound + direct fallback. Caller can override the Reality pubkey,
    /// short_id, and server to test each placeholder field independently.
    /// </summary>
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

    // ── Reject paths ─────────────────────────────────────────────────────

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
        // Pubkey clean, short_id placeholder.
        var json = BuildConfig(shortId: PlaceholderShortId);

        var ex = Assert.Throws<PlaceholderConfigException>(() =>
            CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings()));

        Assert.Equal("reality.short_id", ex.OffendingField);
        Assert.Equal(PlaceholderShortId, ex.OffendingValue);
    }

    [Fact]
    public void Inject_PlaceholderServerIp_Throws()
    {
        // Both Reality fields clean, server IP placeholder.
        var json = BuildConfig(server: PlaceholderServer);

        var ex = Assert.Throws<PlaceholderConfigException>(() =>
            CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings()));

        Assert.Equal("server", ex.OffendingField);
        Assert.Equal(PlaceholderServer, ex.OffendingValue);
    }

    // ── Pass paths ───────────────────────────────────────────────────────

    [Fact]
    public void Inject_CleanConfig_PassesThrough()
    {
        // Same shape as the placeholder tests but with real-looking
        // credentials. Must reach the existing injection code path and
        // return successfully (no exception, valid JSON output).
        var json = BuildConfig();

        var result = CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings());

        Assert.False(string.IsNullOrWhiteSpace(result));
        var parsed = JsonObject.Parse(result);
        // Confirm injection happened — process_name rule should be present.
        Assert.Contains("\"Discord.exe\"", result);
        Assert.NotNull(parsed["route"]);
    }

    // ── Multi-outbound walking ───────────────────────────────────────────

    [Fact]
    public void Inject_PlaceholderInSecondOutbound_FirstProxyOnly()
    {
        // Config with two outbounds: first is direct (skipped — not a proxy
        // type), second is vless with placeholder pubkey. The walker should
        // find the vless one (the first PROXY-typed outbound) and throw.
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
                // Direct comes first — must be walked past.
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct",
                },
                // Block also doesn't count as a proxy.
                new JsonObject
                {
                    ["type"] = "block",
                    ["tag"] = "block",
                },
                // VLESS with placeholder pubkey — this is the one we must catch.
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
        // Config has only direct + block outbounds — no proxy-typed outbound
        // at all. The placeholder gate should NOT trip (there's nothing to
        // inspect). Note: Inject may still throw OTHER errors (missing
        // process routing target, etc.) — what matters here is that the
        // specific PlaceholderConfigException is NOT raised.
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

        // Either succeeds OR throws something other than PlaceholderConfigException.
        var caught = Record.Exception(() =>
            CustomConfigInjector.Inject(config.ToString(), new[] { "Discord.exe" }, CreateSettings()));

        Assert.False(caught is PlaceholderConfigException,
            $"Expected no PlaceholderConfigException, got: {caught?.GetType().Name}: {caught?.Message}");
    }

    // ── Helper parity ────────────────────────────────────────────────────

    [Fact]
    public void ConfigSanityCheck_InspectOutbound_PublicHelperMatches()
    {
        // The extracted helper must return the same field name as
        // PlaceholderDefense.Inspect for each placeholder field. This pins
        // the "single source of truth" promise: both layers agree on what
        // a placeholder looks like and what the field name is.
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

        // Cross-check against PlaceholderGuard directly — same field names.
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
