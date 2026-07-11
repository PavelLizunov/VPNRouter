using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// P1 clash_api secret (OPEN-DEFECTS, 2026-07-10): the Clash API on
/// 127.0.0.1:9090 was exposed with NO secret — any local process (a hostile
/// web page XHR-ing loopback; on Android any installed app) could read live
/// connection metadata and issue control calls (proxy switch, config reload).
/// These pins cover the chain: settings auto-generate → generated/injected
/// config carries <c>experimental.clash_api.secret</c> → every consumer
/// authenticates (HTTP <c>Authorization: Bearer</c>, WS <c>?token=</c>).
/// A consumer missing the header would 401 against the locked API and read a
/// HEALTHY tunnel as dead — which is why the consumer-side pins matter as much
/// as the config-side ones.
/// </summary>
public sealed class ClashApiSecretTests
{
    // ── settings: generation ────────────────────────────────────────────────

    [Fact]
    public void EnsureSane_generates_secret_when_empty_and_preserves_existing()
    {
        var fresh = new AppSettings().EnsureSane();
        Assert.False(string.IsNullOrEmpty(fresh.SingBox.ClashApiSecret));
        Assert.Equal(32, fresh.SingBox.ClashApiSecret.Length); // 16 random bytes → 32 hex
        Assert.True(fresh.SingBox.ClashApiSecret.All(Uri.IsHexDigit));

        var pinned = new AppSettings();
        pinned.SingBox.ClashApiSecret = "my-existing-secret";
        Assert.Equal("my-existing-secret", pinned.EnsureSane().SingBox.ClashApiSecret);
    }

    [Fact]
    public void GenerateClashApiSecret_is_random_per_call()
        => Assert.NotEqual(
            AppSettingsSane.GenerateClashApiSecret(),
            AppSettingsSane.GenerateClashApiSecret());

    // ── generated config carries the secret + the settings-backed controller ──

    private static AppSettings SubscribeSettings(string secret)
    {
        var s = new AppSettings().EnsureSane();
        s.App.ConfigMode = "subscribe";
        s.App.RoutingMode = "full";
        s.SingBox.ClashApi = "127.0.0.1:9091"; // non-default port → proves settings are honoured
        s.SingBox.ClashApiSecret = secret;
        s.Vless.Servers = new()
        {
            new VlessServerEntry { Name = "srv", Server = "1.2.3.4", Port = 443, Uuid = "u" },
        };
        s.Vless.ActiveServer = "srv";
        return s;
    }

    private static string GenerateJson(AppSettings settings)
    {
        var config = ConfigGenerator.Generate(new Profile { Name = "p" }, new[] { "Discord.exe" }, settings);
        return JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);
    }

    [Fact]
    public void Generate_emits_secret_and_settings_controller()
    {
        var settings = SubscribeSettings("cafebabe00112233445566778899aabb");
        var json = GenerateJson(settings);

        using var doc = JsonDocument.Parse(json);
        var clash = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal("127.0.0.1:9091", clash.GetProperty("external_controller").GetString());
        Assert.Equal("cafebabe00112233445566778899aabb", clash.GetProperty("secret").GetString());
    }

    [Fact]
    public void Generate_omits_secret_when_settings_have_none()
    {
        var settings = SubscribeSettings("x");
        settings.SingBox.ClashApiSecret = ""; // legacy shape — no secret key at all
        var json = GenerateJson(settings);

        using var doc = JsonDocument.Parse(json);
        var clash = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        Assert.False(clash.TryGetProperty("secret", out _));
    }

    // ── WS /logs token ──────────────────────────────────────────────────────

    [Fact]
    public void BuildLogsUri_appends_escaped_token_when_secret_present()
    {
        var plain = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090");
        Assert.DoesNotContain("token=", plain.ToString());

        var withToken = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", "s3cr+t&x");
        Assert.Equal("ws://127.0.0.1:9090/logs?level=info&token=s3cr%2Bt%26x", withToken.ToString());
    }

    // ── HTTP consumers send Authorization: Bearer ───────────────────────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"version\":\"1.13.14\"}"),
            });
        }
    }

    [Fact]
    public async Task ClashSingBoxApi_sends_bearer_header_on_every_call()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(http, "http://127.0.0.1:9090", secret: "tok123");

        _ = await api.GetVersionAsync();

        Assert.NotNull(handler.Last);
        Assert.Equal("Bearer", handler.Last!.Headers.Authorization?.Scheme);
        Assert.Equal("tok123", handler.Last.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ClashSingBoxApi_sends_no_auth_header_without_secret()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(http, "http://127.0.0.1:9090");

        _ = await api.GetVersionAsync();

        Assert.Null(handler.Last!.Headers.Authorization); // legacy wire shape preserved
    }

    [Fact]
    public void Post_start_probe_passes_settings_secret()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
            dir = dir.Parent;

        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs"));
        Assert.Contains(
            "clashPort, settings.SingBox.ClashApiSecret, probeCt", source);
    }

    // ── custom-config injection ─────────────────────────────────────────────

    private static AppSettings InjectorSettings()
    {
        var s = new AppSettings().EnsureSane();
        s.SingBox.ClashApi = "127.0.0.1:9090";
        s.SingBox.ClashApiSecret = "deadbeefdeadbeefdeadbeefdeadbeef";
        return s;
    }

    [Fact]
    public void Inject_adds_secret_when_it_creates_the_clash_block()
    {
        var raw = "{\"outbounds\":[{\"type\":\"vless\",\"tag\":\"proxy\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\"},{\"type\":\"direct\",\"tag\":\"direct\"}]}";
        var result = CustomConfigInjector.Inject(raw, Array.Empty<string>(), InjectorSettings());

        using var doc = JsonDocument.Parse(result);
        var clash = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", clash.GetProperty("secret").GetString());
    }

    [Fact]
    public void Inject_leaves_user_authored_clash_block_untouched()
    {
        // The user set their own controller (their dashboards, their auth
        // policy) — we must not graft our secret onto it.
        var raw = "{\"experimental\":{\"clash_api\":{\"external_controller\":\"127.0.0.1:9990\"}}," +
                  "\"outbounds\":[{\"type\":\"vless\",\"tag\":\"proxy\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\"},{\"type\":\"direct\",\"tag\":\"direct\"}]}";
        var result = CustomConfigInjector.Inject(raw, Array.Empty<string>(), InjectorSettings());

        using var doc = JsonDocument.Parse(result);
        var clash = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal("127.0.0.1:9990", clash.GetProperty("external_controller").GetString());
        Assert.False(clash.TryGetProperty("secret", out _));
    }
}
