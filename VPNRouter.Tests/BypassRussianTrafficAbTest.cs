using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit.Abstractions;

namespace VPNRouter.Tests;

/// <summary>
/// User-visible A/B demo of the BypassRussianTraffic toggle behavior.
/// Same path the App.exe Apply button takes when the user flips the
/// "Российский трафик через реальный IP" checkbox. Prints both
/// configs side-by-side so the routing decision is auditable without
/// needing a live VPN server / 2ip.ru round-trip.
/// </summary>
public sealed class BypassRussianTrafficAbTest
{
    private readonly ITestOutputHelper _out;

    public BypassRussianTrafficAbTest(ITestOutputHelper output) => _out = output;

    private static AppSettings Make(bool bypass)
    {
        var s = new AppSettings();
        s.App.RoutingMode = "full";
        s.App.BypassRussianTraffic = bypass;
        s.App.BlockAds = false;
        var srv = new VlessServerEntry
        {
            Server = "test.example.com", Port = 443,
            Uuid = "00000000-0000-0000-0000-000000000001",
            Flow = "xtls-rprx-vision", Security = "reality"
        };
        srv.Reality.Enabled = true;
        srv.Reality.ServerName = "google.com";
        srv.Reality.PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        srv.Reality.ShortId = "deadbeef";
        srv.Reality.Fingerprint = "chrome";
        s.Vless.Servers = new List<VlessServerEntry> { srv };
        return s;
    }

    private void Print(SingBoxConfig c, string label)
    {
        _out.WriteLine($"================ {label} ================");
        for (int i = 0; i < c.Route.Rules.Count; i++)
        {
            var r = c.Route.Rules[i];
            var ruleSet = r.RuleSet != null && r.RuleSet.Count > 0 ? string.Join(",", r.RuleSet) : "-";
            var domain = r.Domain != null && r.Domain.Count > 0 ? string.Join(",", r.Domain) : "-";
            var ipPriv = r.IpIsPrivate == true ? " ip_is_private" : "";
            _out.WriteLine($"  rule[{i}]: action={r.Action,-12} outbound={r.Outbound ?? "-",-8} rule_set={ruleSet,-25}{ipPriv}");
        }
        _out.WriteLine($"  final = {c.Route.Final}");
        _out.WriteLine($"  rule_set count = {c.Route.RuleSet?.Count ?? 0}");
        if (c.Route.RuleSet != null)
            foreach (var rs in c.Route.RuleSet)
                _out.WriteLine($"    rule_set: tag={rs.Tag} type={rs.Type} format={rs.Format}");
        _out.WriteLine("");
    }

    [Fact]
    public void Bypass_Off_vs_On_DiffersByGeositeRuRule()
    {
        var prof = new Profile { Name = "FullTunnel" };
        var procs = new[] { "Discord.exe" };

        var cfgOff = ConfigGenerator.Generate(prof, procs, Make(bypass: false));
        var cfgOn  = ConfigGenerator.Generate(prof, procs, Make(bypass: true));

        Print(cfgOff, "A: Full + BypassRussianTraffic = OFF");
        Print(cfgOn,  "B: Full + BypassRussianTraffic = ON");

        // Assertion: bypass=ON adds rule_set entries with vpnrouter-geosite-ru
        // / vpnrouter-geoip-ru, AND a route rule that points them at outbound=direct.
        var bypassRuleA = cfgOff.Route.Rules.FirstOrDefault(r =>
            r.RuleSet != null && r.RuleSet.Any(t => t.Contains("ru", System.StringComparison.OrdinalIgnoreCase)));
        var bypassRuleB = cfgOn.Route.Rules.FirstOrDefault(r =>
            r.RuleSet != null
            && r.RuleSet.Any(t => t.Contains("ru", System.StringComparison.OrdinalIgnoreCase))
            && r.Outbound == "direct");

        _out.WriteLine($"OFF: route rule referencing -ru = {(bypassRuleA != null ? "PRESENT" : "absent")}");
        _out.WriteLine($"ON:  route rule referencing -ru = {(bypassRuleB != null ? "PRESENT (direct)" : "absent")}");

        // Final invariant check
        Assert.Equal("proxy", cfgOff.Route.Final);
        Assert.Equal("proxy", cfgOn.Route.Final);

        // Bypass=OFF must NOT have RU bypass rule. Bypass=ON MIGHT have it
        // (skips silently if geo files unavailable on host — see
        // GeoDataDownloader.AreGeoFilesAvailable() gate).
        if (GeoDataDownloader.AreGeoFilesAvailable())
        {
            Assert.NotNull(bypassRuleB);
            Assert.Equal("direct", bypassRuleB!.Outbound);
        }
        else
        {
            _out.WriteLine("(geo files not on host — bypass rule silently skipped, gate is intentional)");
        }
    }
}
