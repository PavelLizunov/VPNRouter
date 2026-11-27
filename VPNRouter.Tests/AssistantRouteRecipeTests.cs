using VPNRouter.App.Services;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

public sealed class AssistantRouteRecipeTests
{
    [Theory]
    [InlineData("Хочу, чтобы игры шли мимо VPN", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("I want all games to go outside the VPN", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("Сделать ру сайты вне впн, пожалуйста", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("Хочу чтобы российские сайты открывались без VPN", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("пустить все игры вне впн", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("Пусти все игры напрямую", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("Все игры мимо VPN", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("пусть все игры идут вне впн", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("убери все игры из впн", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("send all games outside the VPN", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("Route all games directly", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("bypass VPN for all games", nameof(AssistantRouteRecipeKind.GamesDirect))]
    [InlineData("сделать ру сайты вне впн", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("Сайты РФ вне ВПН", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("убери русские сайты из VPN", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("Пусть русские сайты идут напрямую", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("включи обход российского трафика", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("российский трафик вне VPN", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("make RU sites outside the VPN", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("route russian websites directly", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("enable the russian traffic bypass", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    [InlineData("russian traffic without the vpn", nameof(AssistantRouteRecipeKind.RussianSitesDirect))]
    public void Natural_ru_and_en_recipes_are_recognized(string goal, string kind)
    {
        var recipe = AssistantRouteRecipe.Parse(goal);
        Assert.NotNull(recipe);
        Assert.Equal(kind, recipe.Kind.ToString());
    }

    [Theory]
    [InlineData("Не пусти все игры вне впн")]
    [InlineData("Почему все игры вне впн?")]
    [InlineData("Можно ли пустить все игры вне впн?")]
    [InlineData("Пусти все игры вне впн и включи VPN")]
    [InlineData("Route all games directly and connect the VPN")]
    [InlineData("Make Russian sites outside the VPN and games too")]
    [InlineData("Сделать ру сайты вне впн, а игры через VPN")]
    [InlineData("все игры и ру сайты вне впн")]
    [InlineData("Как сделать ру сайты вне впн")]
    [InlineData("Какие ру сайты вне впн")]
    [InlineData("Route Edge directly if Steam closes")]
    public void Compound_question_negated_or_ambiguous_text_is_not_a_recipe(string goal) =>
        Assert.Null(AssistantRouteRecipe.Parse(goal));

    [Fact]
    public void Existing_chrome_and_application_commands_stay_with_the_existing_intent_parser()
    {
        Assert.Null(AssistantRouteRecipe.Parse("Пусти Chrome напрямую"));
        Assert.Equal(new AssistantIntent("RouteApplicationDirect", null, "Chrome"),
            AssistantIntent.Parse("Пусти Chrome напрямую"));
        Assert.Null(AssistantRouteRecipe.Parse("Хром напрямую"));
        Assert.Equal(new AssistantIntent("RouteApplicationDirect", null, "Хром"),
            AssistantIntent.Parse("Хром напрямую"));
        Assert.Null(AssistantRouteRecipe.Parse("Route only Firefox through the VPN"));
        Assert.Equal(new AssistantIntent("RouteOnlyApplication", null, "Firefox"),
            AssistantIntent.Parse("Route only Firefox through the VPN"));
    }

    [Fact]
    public void Games_plan_switches_to_exclude_preserves_saved_entries_and_uses_exact_casing()
    {
        string previous = Strings.Lang;
        try
        {
            Strings.Lang = "ru";
            var settings = NewSettings();
            settings.App.RoutingMode = "split";
            settings.App.RoutingAppsMode = "include";
            settings.App.RoutingAppsInclude = ["firefox.exe"];
            settings.App.RoutingAppsExclude = ["OLDgame.exe", "Chat.exe"];

            var result = AssistantRouteRecipe.PlanGames(settings,
                ["GameB.exe", "gameA.exe", "GameA.exe", "OldGame.exe", "../bad.exe", "readme.txt"],
                "SplitProfile", connected: false);

            Assert.Null(result.Message);
            var plan = result.Plan!;
            Assert.Equal("split", plan.Candidate.App.RoutingMode);
            Assert.Equal("exclude", plan.Candidate.App.RoutingAppsMode);
            Assert.Equal(new[] { "OldGame.exe", "Chat.exe", "gameA.exe", "GameB.exe" }, plan.Candidate.App.RoutingAppsExclude);
            Assert.Equal(new[] { "firefox.exe" }, plan.Candidate.App.RoutingAppsInclude);
            Assert.False(plan.Candidate.App.BypassRussianTraffic);
            Assert.Equal(string.Empty, plan.Candidate.ActiveProfile);
            Assert.Equal(new[] { "gameA.exe", "GameB.exe", "OldGame.exe" }, plan.VerifiedProcesses);
            Assert.Contains("Steam", plan.Preview);
            Assert.Contains("gameA.exe, GameB.exe", plan.Preview);
            Assert.Contains("Chat.exe", plan.Preview);
            Assert.Contains("Все, кроме выбранных", plan.Preview);
        }
        finally { Strings.Lang = previous; }
    }

    [Fact]
    public void Games_plan_from_full_tunnel_uses_the_split_profile_and_discloses_saved_exclusions()
    {
        string previous = Strings.Lang;
        try
        {
            Strings.Lang = "ru";
            var settings = NewSettings();
            settings.App.RoutingMode = "full";
            settings.App.RoutingAppsMode = "exclude";
            settings.App.RoutingAppsExclude = ["Old.exe"];
            settings.App.BypassRussianTraffic = true;

            var plan = AssistantRouteRecipe.PlanGames(settings, ["New.exe"], "SplitProfile",
                connected: true).Plan!;

            Assert.Equal("split", plan.Candidate.App.RoutingMode);
            Assert.Equal("SplitProfile", plan.Candidate.ActiveProfile);
            Assert.Equal(new[] { "Old.exe", "New.exe" }, plan.Candidate.App.RoutingAppsExclude);
            Assert.True(plan.Candidate.App.BypassRussianTraffic);
            Assert.Contains("Весь трафик", plan.Preview);
            Assert.Contains("Old.exe", plan.Preview);
            Assert.Contains("профиль раздельной маршрутизации", plan.Preview);
            Assert.Contains(Strings.AssistantRecipeRestartNotice, plan.Preview);
        }
        finally { Strings.Lang = previous; }
    }

    [Fact]
    public void Games_plan_reports_empty_already_excluded_and_unsupported_platform_without_a_candidate()
    {
        string previous = Strings.Lang;
        try
        {
            Strings.Lang = "ru";
            var settings = NewSettings();

            var empty = AssistantRouteRecipe.PlanGames(settings, [], "SplitProfile",
                connected: false);
            Assert.Equal(Strings.AssistantGamesNotFound, empty.Message);
            Assert.Null(empty.Plan);

            settings.App.RoutingAppsMode = "exclude";
            settings.App.RoutingAppsExclude = ["Old.exe"];
            var already = AssistantRouteRecipe.PlanGames(settings, ["Old.exe"], "SplitProfile",
                connected: false);
            Assert.Equal(Strings.AssistantGamesAlreadySet, already.Message);
            Assert.Null(already.Plan);

            Assert.Equal(OperatingSystem.IsWindows(), AssistantRouteRecipe.SupportsGameDetection);
        }
        finally { Strings.Lang = previous; }
    }

    [Fact]
    public void Russian_plan_changes_only_the_bypass_and_reports_the_exact_rule_set_semantics()
    {
        string previous = Strings.Lang;
        try
        {
            Strings.Lang = "ru";
            var settings = NewSettings();
            settings.App.RoutingMode = "full";
            settings.App.RoutingAppsExclude = ["Old.exe"];

            var plan = AssistantRouteRecipe.PlanRussianSites(settings, ruleSetsAvailable: true,
                connected: false).Plan!;

            Assert.True(plan.Candidate.App.BypassRussianTraffic);
            Assert.Equal("full", plan.Candidate.App.RoutingMode);
            Assert.Equal(new[] { "Old.exe" }, plan.Candidate.App.RoutingAppsExclude);
            Assert.Empty(plan.VerifiedProcesses);
            Assert.Contains(".ru, .su, .рф", plan.Preview);
            Assert.Contains("Остальной трафик", plan.Preview);
        }
        finally { Strings.Lang = previous; }
    }

    [Fact]
    public void Russian_plan_discloses_unavailable_lists_before_saving()
    {
        string previous = Strings.Lang;
        try
        {
            Strings.Lang = "ru";
            var settings = NewSettings();
            settings.App.BypassRussianTraffic = true;
            var already = AssistantRouteRecipe.PlanRussianSites(settings, ruleSetsAvailable: true, connected: false);
            Assert.Equal(Strings.AssistantRuAlreadyOn, already.Message);
            Assert.Null(already.Plan);

            var pending = AssistantRouteRecipe.PlanRussianSites(settings, ruleSetsAvailable: false, connected: false);
            Assert.Equal(Strings.AssistantRuRuleSetsPending, pending.Message);
            Assert.Null(pending.Plan);

            settings.App.BypassRussianTraffic = false;
            var missing = AssistantRouteRecipe.PlanRussianSites(settings, ruleSetsAvailable: false, connected: false);
            Assert.Contains("следующем подключении", missing.Plan!.Preview);
            Assert.False(settings.App.BypassRussianTraffic);
        }
        finally { Strings.Lang = previous; }
    }

    [Fact]
    public void English_previews_name_the_mode_and_the_exact_rule_sets()
    {
        string previous = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            var settings = NewSettings();
            var games = AssistantRouteRecipe.PlanGames(settings, ["GameA.exe"], "SplitProfile",
                connected: false).Plan!;
            Assert.Contains("All except selected", games.Preview);
            Assert.Contains("GameA.exe", games.Preview);
            Assert.Contains("installed Steam", games.Preview);

            var ru = AssistantRouteRecipe.PlanRussianSites(settings, ruleSetsAvailable: true,
                connected: false).Plan!;
            Assert.Contains("Russian IP", ru.Preview);
        }
        finally { Strings.Lang = previous; }
    }

    [Fact]
    public void Games_candidate_generates_direct_process_rules_and_a_proxy_final()
    {
        var settings = NewSettings();
        var plan = AssistantRouteRecipe.PlanGames(settings, ["GameA.exe", "GameB.exe"],
            "SplitProfile", connected: false).Plan!;

        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            var config = ConfigGenerator.Generate(NewProfile(), [], plan.Candidate, null, null,
                resolveRuleSet: null, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);

            var rule = Assert.Single(config.Route.Rules.Where(item =>
                item.ProcessName?.Contains("GameA.exe") == true));
            Assert.Equal("route", rule.Action);
            Assert.Equal("direct", rule.Outbound);
            Assert.Contains("GameB.exe", rule.ProcessName!);
            Assert.Equal("proxy", config.Route.Final);
            var tunnel = new AcceptedTunnelInfo(plan.Candidate, ConfigGenerator.Serialize(config), "", null,
                new Dictionary<string, VlessServerEntry>());
            Assert.True(tunnel.RoutesGamesDirect(plan.VerifiedProcesses));
            Assert.False(tunnel.RoutesGamesDirect(["Wrong.exe"]));
            Assert.DoesNotContain(config.Route.RuleSet ?? new List<RuleSetEntry>(),
                item => item.Tag.StartsWith("vpnrouter-geo", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Russian_candidate_generates_the_verified_direct_rule_set_and_dns_route()
    {
        var settings = NewSettings();
        var plan = AssistantRouteRecipe.PlanRussianSites(settings, ruleSetsAvailable: true,
            connected: false).Plan!;

        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            var config = ConfigGenerator.Generate(NewProfile(), [], plan.Candidate, null, null,
                resolveRuleSet: null, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);

            var rule = Assert.Single(config.Route.Rules.Where(item =>
                item.RuleSet?.Contains(ConfigGenerator.GeoSiteRuleSetTag) == true));
            Assert.Equal(new[] { "vpnrouter-geosite-ru", "vpnrouter-geoip-ru" }, rule.RuleSet!);
            Assert.Equal("route", rule.Action);
            Assert.Equal("direct", rule.Outbound);

            var dns = Assert.Single(config.Dns.Rules.Where(item =>
                item.RuleSet?.Contains(ConfigGenerator.GeoSiteRuleSetTag) == true));
            Assert.Equal("vpn-dns", dns.Server);

            var accepted = new AcceptedTunnelInfo(plan.Candidate, ConfigGenerator.Serialize(config), "", null,
                new Dictionary<string, VlessServerEntry>());
            Assert.True(accepted.RoutesRussianTrafficDirect());

            var plain = ConfigGenerator.Generate(NewProfile(), [], settings, null, null,
                resolveRuleSet: null, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
            var unchanged = new AcceptedTunnelInfo(settings, ConfigGenerator.Serialize(plain), "", null,
                new Dictionary<string, VlessServerEntry>());
            Assert.False(unchanged.RoutesRussianTrafficDirect());
        });
    }

    [Theory]
    [InlineData("""{"action":"route","outbound":"proxy"}""", "")]
    [InlineData("", ""","invert":true""")]
    [InlineData("", ""","process_name":["browser.exe"]""")]
    public void Earlier_proxy_and_conditional_regional_rules_cannot_confirm_a_bypass(string preceding, string extra)
    {
        const string tags = """["vpnrouter-geosite-ru","vpnrouter-geoip-ru"]""";
        var previous = preceding.Length == 0 ? "" : preceding + ",";
        var config = """{"route":{"rules":[""" + previous + """{"action":"route","outbound":"direct","rule_set":""" + tags + extra + "}]}}";
        var accepted = new AcceptedTunnelInfo(NewSettings(), config, "", null, new Dictionary<string, VlessServerEntry>());
        Assert.False(accepted.RoutesRussianTrafficDirect());
    }

    private static AppSettings NewSettings() => new()
    {
        App = new AppConfig { LogLevel = "info", BypassRussianTraffic = false },
        Tun = new TunSettings(),
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Vless = new VlessConfig
        {
            Servers =
            [
                new VlessServerEntry
                {
                    Name = "main",
                    Server = "1.2.3.4",
                    Port = 443,
                    Uuid = "test-uuid",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "testkey", ShortId = "abcd" },
                },
            ],
        },
    };

    private static Profile NewProfile() => new()
    {
        Name = "TestProfile",
        DnsMode = "vpn_only",
        Processes = [],
    };
}
