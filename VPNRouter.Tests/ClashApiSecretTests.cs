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

public sealed class ClashApiSecretTests
{
    [Fact]
    public void EnsureSane_generates_secret_when_empty_and_preserves_existing()
    {
        var fresh = new AppSettings().EnsureSane();
        Assert.False(string.IsNullOrEmpty(fresh.SingBox.ClashApiSecret));
        Assert.Equal(32, fresh.SingBox.ClashApiSecret.Length);
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

    private static AppSettings SubscribeSettings(string secret)
    {
        var s = new AppSettings().EnsureSane();
        s.App.ConfigMode = "subscribe";
        s.App.RoutingMode = "full";
        s.SingBox.ClashApi = "127.0.0.1:9091";
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
        settings.SingBox.ClashApiSecret = "";
        var json = GenerateJson(settings);

        using var doc = JsonDocument.Parse(json);
        var clash = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        Assert.False(clash.TryGetProperty("secret", out _));
    }

    [Fact]
    public void BuildLogsUri_appends_escaped_token_when_secret_present()
    {
        var plain = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090");
        Assert.DoesNotContain("token=", plain.ToString());

        var withToken = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", "s3cr+t&x");
        Assert.Equal("ws://127.0.0.1:9090/logs?level=info&token=s3cr%2Bt%26x", withToken.ToString());
    }

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

        Assert.Null(handler.Last!.Headers.Authorization);
    }

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
        var raw = "{\"experimental\":{\"clash_api\":{\"external_controller\":\"127.0.0.1:9990\"}}," +
                  "\"outbounds\":[{\"type\":\"vless\",\"tag\":\"proxy\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\"},{\"type\":\"direct\",\"tag\":\"direct\"}]}";
        var result = CustomConfigInjector.Inject(raw, Array.Empty<string>(), InjectorSettings());

        using var doc = JsonDocument.Parse(result);
        var clash = doc.RootElement.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal("127.0.0.1:9990", clash.GetProperty("external_controller").GetString());
        Assert.False(clash.TryGetProperty("secret", out _));
    }
}
