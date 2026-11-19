using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class CustomDnsttTests
{
    private const string Imported = """
        {
          "dns": { "servers": [
            {"type":"https","tag":"remote","server":"1.1.1.1","detour":"ssh-over-dnstt"},
            {"type":"local","tag":"local"}
          ] },
          "inbounds": [{"type":"tun","tag":"tun-in","address":["198.18.69.1/30"]}],
          "outbounds": [
            {"type":"dnstt","tag":"dns-transport","server":"192.0.2.53","server_port":53,
             "domain":"tunnel.example.invalid","public_key":"8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"},
            {"type":"ssh","tag":"ssh-over-dnstt","detour":"dns-transport","server":"127.0.0.1","server_port":22,
             "user":"fixture","password":"fixture"},
            {"type":"direct","tag":"direct"}
          ],
          "route": {"final":"ssh-over-dnstt","auto_detect_interface":true,"default_domain_resolver":"local"}
        }
        """;

    [Theory]
    [InlineData("full", "include", "ssh-over-dnstt", null)]
    [InlineData("split", "exclude", "ssh-over-dnstt", "direct")]
    [InlineData("split", "include", "direct", "ssh-over-dnstt")]
    public void ImportPreservesSshDetourAndAppliesRoutingMode(
        string routingMode, string appsMode, string finalTag, string? processTag)
    {
        Assert.True(CustomConfigInjector.Validate(Imported).IsValid);
        var config = JsonNode.Parse(Inject(routingMode, appsMode))!;
        var outbounds = config["outbounds"]!.AsArray();
        var ssh = Assert.Single(outbounds, outbound => (string?)outbound?["type"] == "ssh");
        Assert.Equal("dns-transport", (string?)ssh!["detour"]);
        Assert.Equal(finalTag, (string?)config["route"]!["final"]);
        var processRules = config["route"]!["rules"]!.AsArray().Where(rule => rule?["process_name"] != null);
        if (processTag == null)
            Assert.Empty(processRules);
        else
            Assert.Equal(processTag, (string?)Assert.Single(processRules)!["outbound"]);

        var dns = config["dns"]!;
        var resolver = Assert.Single(dns["servers"]!.AsArray(),
            server => (string?)server?["tag"] == (string?)dns["final"]);
        Assert.Equal("ssh-over-dnstt", (string?)resolver!["detour"]);
    }

    [Fact]
    public void DnsttProfileGlyphRequiresTheSelectedSshDetour()
    {
        Assert.True(CustomConfigInjector.HasDnsttSshRoute(Imported));

        var config = JsonNode.Parse(Imported)!;
        config["route"]!["final"] = "direct";
        Assert.False(CustomConfigInjector.HasDnsttSshRoute(config.ToJsonString()));

        config["route"]!["final"] = "ssh-over-dnstt";
        config["outbounds"]![1]!["detour"] = "missing";
        Assert.False(CustomConfigInjector.HasDnsttSshRoute(config.ToJsonString()));

        Assert.False(CustomConfigInjector.HasDnsttSshRoute("{"));
        Assert.False(CustomConfigInjector.HasDnsttSshRoute("""{"outbounds":[],"outbounds":[]}"""));
    }

    [Fact]
    public async Task Native_DnsttImport_Check()
    {
        var runtime = Environment.GetEnvironmentVariable("VPNCTL_TEST_CORE")
            ?? Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(runtime), "Requires the pinned sing-box runtime");
        Assert.True(File.Exists(runtime), "Supplied sing-box runtime was not found");
        var request = new ProcessRequest(runtime!, ["check", "-c", "stdin"],
            StdinInput: Inject("full", "include"), Timeout: TimeSpan.FromSeconds(15));

        var result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private static string Inject(string routingMode, string appsMode)
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = appsMode;
        settings.App.RoutingAppsExclude = ["fixture-browser"];
        settings.App.BypassRussianTraffic = false;
        settings.App.StrictDns = true;
        return CustomConfigInjector.Inject(Imported, ["fixture-browser"], settings);
    }
}
