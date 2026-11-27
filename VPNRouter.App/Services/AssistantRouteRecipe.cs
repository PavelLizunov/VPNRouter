using System.Text.RegularExpressions;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Services;

internal enum AssistantRouteRecipeKind
{
    GamesDirect,
    RussianSitesDirect,
}

internal sealed record AssistantRouteRecipe(AssistantRouteRecipeKind Kind)
{
    private const string Games = @"(?:все )?игры";
    private const string Region = @"(?:российские|русские|ру|ru|рф)";
    private const string Sites = @"(?:все )?(?:" + Region + @" (?:веб-?)?сайты|сайты " + Region + @")";
    private const string Traffic = @"(?:российского трафика|русского трафика|российский трафик|русский трафик|ру трафик)";
    private const string RuDirect = @"(?:напрямую|(?:вне|мимо|без|в обход|не через) (?:впн|vpn))";
    private const string EnDirect = @"(?:direct(?:ly)?|(?:outside(?: of)?|around|without|bypassing) (?:the )?vpn)";
    private const string EnSites = @"(?:all )?(?:russian|ru) (?:sites|websites)";
    private const string RuStart = @"(?:(?:пустить|пусти|сделать|сделай|направить|направь|отправить|отправь|оставить|оставь) |(?:хочу,? чтобы|пусть) )?";
    private const string RuMotion = @"(?: (?:шли|идут|пойдут|работали|работают|открывались|открываются))? ";
    private const string EnStart = @"(?:(?:send|route|put|keep|make|move|set|let|i want) )?";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex GamesRu = Command(RuStart + Games + RuMotion + RuDirect +
        @"|(?:убери|убрать|исключи|исключить) " + Games + @" из (?:впн|vpn)|обход (?:впн|vpn) для (?:всех )?игр");
    private static readonly Regex GamesEn = Command(EnStart + @"(?:all )?games (?:(?:to )?(?:go|run|connect) )?" + EnDirect +
        @"|bypass (?:the )?vpn for (?:all )?games|(?:all )?games bypass (?:the )?vpn");
    private static readonly Regex SitesRu = Command(RuStart + Sites + RuMotion + RuDirect +
        @"|(?:убери|убрать|исключи|исключить) " + Sites + @" из (?:впн|vpn)|(?:(?:включи|включить|сделай|сделать) )?обход (?:" +
        Traffic + @"|" + Sites + @")|" + RuStart + Traffic + " " + RuDirect);
    private static readonly Regex SitesEn = Command(EnStart + EnSites + @" (?:(?:to )?(?:open|go) )?" + EnDirect +
        @"|bypass (?:the )?vpn for " + EnSites + @"|" + EnSites + @" bypass (?:the )?vpn" +
        @"|(?:turn on|enable|activate) (?:the )?(?:russian|ru) (?:traffic )?bypass|(?:russian|ru) traffic " + EnDirect);

    internal static bool SupportsGameDetection => OperatingSystem.IsWindows();

    internal static AssistantRouteRecipe? Parse(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 384 || goal.Any(char.IsControl)) return null;
        string command = Whitespace.Replace(goal.Trim(), " ");
        if (command.EndsWith('.') || command.EndsWith('!')) command = command[..^1].TrimEnd();
        if (GamesRu.IsMatch(command) || GamesEn.IsMatch(command))
            return new AssistantRouteRecipe(AssistantRouteRecipeKind.GamesDirect);
        if (SitesRu.IsMatch(command) || SitesEn.IsMatch(command))
            return new AssistantRouteRecipe(AssistantRouteRecipeKind.RussianSitesDirect);
        return null;
    }

    internal static IReadOnlyList<string> ReadDetectedGameProcesses() =>
        SteamLibraryScanner.FindInstalledGames().Select(game => game.ProcessName).ToArray();

    private static IReadOnlyList<string> NormalizeDetectedGames(IEnumerable<string> names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in names)
        {
            string name = (raw ?? string.Empty).Trim();
            if (!ValidProcessName(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(name)) result.Add(name);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    internal static AssistantRoutePlanResult PlanGames(AppSettings saved, IReadOnlyList<string> detected,
        string splitProfile, bool connected)
    {
        var names = NormalizeDetectedGames(detected);
        if (names.Count == 0) return AssistantRoutePlanResult.Fail(Strings.AssistantGamesNotFound);

        var savedApp = saved.App;
        bool wasFull = string.Equals(savedApp.RoutingMode, "full", StringComparison.OrdinalIgnoreCase);
        bool wasExclude = !wasFull
            && string.Equals(savedApp.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);
        bool wasInclude = !wasFull && !wasExclude;
        var existing = savedApp.RoutingAppsExclude ?? [];

        var candidate = SettingsLoader.Copy(saved);
        candidate.App.RoutingMode = "split";
        if (wasFull) candidate.ActiveProfile = splitProfile;
        candidate.App.RoutingAppsMode = "exclude";
        var detectedNames = names.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        var remaining = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<string>();
        var previousExceptions = new List<string>();
        foreach (string previous in existing)
        {
            if (detectedNames.TryGetValue(previous, out var actual))
            {
                if (remaining.Remove(actual)) exclusions.Add(actual);
            }
            else
            {
                exclusions.Add(previous);
                previousExceptions.Add(previous);
            }
        }
        exclusions.AddRange(names.Where(remaining.Contains));
        if (wasExclude && existing.SequenceEqual(exclusions))
            return AssistantRoutePlanResult.Fail(Strings.AssistantGamesAlreadySet);

        candidate.App.RoutingAppsExclude = exclusions;
        var preview = Strings.AssistantGamesPreview(names, previousExceptions, wasFull, wasInclude, connected);
        return AssistantRoutePlanResult.Ready(new AssistantRoutePlan(candidate, preview, names));
    }

    internal static AssistantRoutePlanResult PlanRussianSites(AppSettings saved, bool ruleSetsAvailable,
        bool connected)
    {
        if (saved.App.BypassRussianTraffic)
            return AssistantRoutePlanResult.Fail(ruleSetsAvailable
                ? Strings.AssistantRuAlreadyOn : Strings.AssistantRuRuleSetsPending);

        var candidate = SettingsLoader.Copy(saved);
        candidate.App.BypassRussianTraffic = true;
        return AssistantRoutePlanResult.Ready(new AssistantRoutePlan(candidate,
            Strings.AssistantRuPreview(connected, ruleSetsAvailable), []));
    }

    internal static string CompletedMessage(AssistantRouteRecipeKind kind, bool connected, bool keptNewerSettings) =>
        keptNewerSettings
            ? (connected ? Strings.AssistantRecipeAppliedKeptNewer : Strings.AssistantRecipeSavedKeptNewer)
            : kind switch
            {
                AssistantRouteRecipeKind.GamesDirect => connected
                    ? Strings.AssistantGamesApplied : Strings.AssistantGamesSaved,
                _ => connected ? Strings.AssistantRuApplied : Strings.AssistantRuSaved,
            };

    private static Regex Command(string body) => new(@"\A(?:(?:пожалуйста|please),? )?(?:" + body + @")(?:,? (?:пожалуйста|please))?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool ValidProcessName(string name) => name.Length is > 4 and <= 255
        && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0
        && !name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
}

internal sealed record AssistantRoutePlan(AppSettings Candidate, string Preview,
    IReadOnlyList<string> VerifiedProcesses);

internal sealed record AssistantRoutePlanResult(AssistantRoutePlan? Plan, string? Message)
{
    internal static AssistantRoutePlanResult Ready(AssistantRoutePlan plan) => new(plan, null);
    internal static AssistantRoutePlanResult Fail(string message) => new(null, message);
}
