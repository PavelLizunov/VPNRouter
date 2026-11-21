using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class MacPrivilegedClientTests : IDisposable
{
    private readonly string _config =
        Path.Combine(Path.GetTempPath(), "vpnrouter-mac-client-" + Guid.NewGuid().ToString("N") + ".json");

    public MacPrivilegedClientTests() => File.WriteAllText(_config, "{\"log\":{\"output\":\"\"}}");

    public void Dispose()
    {
        try { File.Delete(_config); } catch { }
    }

    private static ProcessResult Ok(string stdout = "") =>
        new(0, stdout, "", TimeSpan.Zero, false);

    private static string? Op(ProcessRequest call)
    {
        using var document = JsonDocument.Parse(call.StdinInput!);
        return document.RootElement.GetProperty("op").GetString();
    }

    [Fact]
    public void Probe_parses_authoritative_ready_state()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath,
            Ok("{\"ready\":true,\"legacy\":false,\"legacy_state\":\"absent\"," +
               "\"legacy_process\":false,\"policy\":2,\"uid\":501," +
               "\"helper_sha256\":\"aaaa\",\"singbox_sha256\":\"bbbb\",\"app_version\":\"2.50.6\"}"));
        var state = new MacPrivilegedClient(fake).Probe();

        Assert.True(state.Ready);
        Assert.False(state.Legacy);
        Assert.False(state.LegacyProcess);
        Assert.Equal("absent", state.LegacyState);
        Assert.Equal(2, state.Policy);
        Assert.Equal(501, state.Uid);
        Assert.Equal("aaaa", state.HelperSha256);
        Assert.Equal("bbbb", state.SingBoxSha256);
    }

    [Fact]
    public void Probe_failure_is_not_ready()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath,
            new ProcessResult(1, "", "sudo: a password is required", TimeSpan.Zero, false));

        Assert.False(new MacPrivilegedClient(fake).Probe().Ready);
    }

    [Fact]
    public void Probe_preserves_unknown_legacy_state()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath,
            Ok("{\"ready\":false,\"legacy\":false,\"legacy_state\":\"unknown\"," +
               "\"legacy_process\":false,\"policy\":2,\"uid\":501,\"reason\":\"legacy_unknown\"}"));

        var state = new MacPrivilegedClient(fake).Probe();

        Assert.False(state.Ready);
        Assert.False(state.Legacy);
        Assert.False(state.LegacyProcess);
        Assert.Equal("unknown", state.LegacyState);
        Assert.Equal("legacy_unknown", state.Reason);
    }

    [Fact]
    public void Probe_reports_a_legacy_root_process_as_a_distinct_blocker()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath,
            Ok("{\"ready\":false,\"legacy\":false,\"legacy_state\":\"absent\"," +
               "\"legacy_process\":true,\"policy\":2,\"uid\":501," +
               "\"reason\":\"legacy_process\"}"));

        var state = new MacPrivilegedClient(fake).Probe();

        Assert.False(state.Ready);
        Assert.True(state.LegacyProcess);
        Assert.Equal("legacy_process", state.Reason);
    }

    [Fact]
    public void Start_request_uses_hash_and_helper_without_user_path_arguments()
    {
        const string launchId = "0123456789abcdef0123456789abcdef";
        var request = new MacPrivilegedClient().CreateStartRequest(_config, launchId);

        Assert.Equal(MacPrivilegePaths.SudoPath, request.ExecutablePath);
        Assert.Equal(new[] { "-n", MacPrivilegePaths.HelperPath }, request.Arguments);
        Assert.DoesNotContain(_config, request.Arguments);
        using var document = JsonDocument.Parse(request.StdinInput!);
        Assert.Equal("start", document.RootElement.GetProperty("op").GetString());
        var expected = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(_config))).ToLowerInvariant();
        Assert.Equal(expected, document.RootElement.GetProperty("config_sha256").GetString());
        Assert.Equal(launchId, document.RootElement.GetProperty("launch_id").GetString());
    }

    [Fact]
    public void Stop_and_dns_requests_are_typed_json_on_stdin()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var client = new MacPrivilegedClient(fake);

        Assert.True(client.Stop(4321, 1234));
        Assert.True(client.DnsApply("Wi-Fi", "172.19.0.1"));
        Assert.True(client.DnsRestore());

        Assert.Equal(new[] { "-n", MacPrivilegePaths.HelperPath }, fake.RunCalls[0].Arguments);
        using var stop = JsonDocument.Parse(fake.RunCalls[0].StdinInput!);
        Assert.Equal("stop", stop.RootElement.GetProperty("op").GetString());
        Assert.Equal(4321, stop.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(1234, stop.RootElement.GetProperty("start_time").GetInt64());

        using var dns = JsonDocument.Parse(fake.RunCalls[1].StdinInput!);
        Assert.Equal("dns_apply", dns.RootElement.GetProperty("op").GetString());
        Assert.Equal("Wi-Fi", dns.RootElement.GetProperty("service").GetString());
        Assert.Equal("172.19.0.1", dns.RootElement.GetProperty("server").GetString());
        using var restore = JsonDocument.Parse(fake.RunCalls[2].StdinInput!);
        Assert.Equal(new[] { "version", "op" },
            restore.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("dns_restore", restore.RootElement.GetProperty("op").GetString());
    }

    [Fact]
    public void Stop_rejects_invalid_identity_without_invoking_helper()
    {
        var fake = new FakeProcessRunner();
        var client = new MacPrivilegedClient(fake);

        Assert.False(client.Stop(1, 1234));
        Assert.False(client.Stop(4321, 0));
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void Pf_requests_carry_only_the_typed_rule_grammar()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var client = new MacPrivilegedClient(fake);

        var session = new MacFirewallSession(103, 1700000000);
        Assert.True(client.PfApplyAnchor(session, "block drop out all\npass out quick on lo0 all\n"));
        Assert.True(client.PfApplyMain(session));
        Assert.True(client.PfRelease(session));
        Assert.True(client.PfEnable(session));
        Assert.True(client.PfRecover());

        using var anchor = JsonDocument.Parse(fake.RunCalls[0].StdinInput!);
        Assert.Equal("pf_apply", anchor.RootElement.GetProperty("op").GetString());
        Assert.Equal("anchor", anchor.RootElement.GetProperty("mode").GetString());
        Assert.Contains("block drop out all",
            anchor.RootElement.GetProperty("rules").GetString() ?? string.Empty);

        using var main = JsonDocument.Parse(fake.RunCalls[1].StdinInput!);
        Assert.Equal("main", main.RootElement.GetProperty("mode").GetString());
        Assert.False(main.RootElement.TryGetProperty("rules", out _));
        using var release = JsonDocument.Parse(fake.RunCalls[2].StdinInput!);
        Assert.Equal("pf_release", release.RootElement.GetProperty("op").GetString());
        Assert.Equal(103, release.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(1700000000L, release.RootElement.GetProperty("start_time").GetInt64());
        Assert.False(release.RootElement.TryGetProperty("mode", out _));
    }

    [Fact]
    public void Request_json_contains_no_sudo_wildcards()
    {
        var json = MacPrivilegedClient.RequestJson(new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["op"] = "probe",
        });
        Assert.DoesNotContain("*", json);
    }
}
