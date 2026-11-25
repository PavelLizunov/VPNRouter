using System.Text.RegularExpressions;

namespace VPNRouter.App.Services;

// A free-text request can authorize at most one locally understood operation.
internal sealed record AssistantIntent(string Control, bool? Desired = null, string? Application = null)
{
    private const string App = @"(?<app>[\p{L}\p{N}][\p{L}\p{N}._-]*(?: [\p{L}\p{N}][\p{L}\p{N}._-]*){0,5})";
    private static Regex Command(string body) => new(@"\A(?:" + body + @")\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Toggle = Command(
        @"(?<verb>connect|disconnect|enable|disable|turn on|turn off|start|stop|включи|выключи|отключи|подключи|запусти|останови) (?:the )?" +
        @"(?<setting>vpn|впн|strict dns|строгий dns|строгий днс|dns flushing on startup|очистку dns при запуске|сброс dns при запуске)");
    private static readonly Regex Direct = Command(
        @"(?<verb>route |send |пусти |направь )?" + App + @" (?:directly|напрямую|без vpn|мимо vpn)" +
        @"|(?<verb>bypass) (?:the )?vpn for " + App);
    private static readonly Regex Exclusive = Command(
        @"(?:(?:route |send |пусти |направь )?(?:only|только) " + App + @" (?:through (?:the )?vpn|via vpn|через vpn|в туннель)" +
        @"|vpn (?:only for|только для) " + App + @")");
    private static readonly Regex Page = Command(@"(?:open|show|открой|покажи) (?:the |вкладку |раздел )?(?<page>" +
        @"server settings|серверы|subscriptions|подписки|network settings|сеть|applications|приложения|tools|инструменты|free configurations|бесплатные конфигурации)");

    internal static AssistantIntent? Parse(string goal)
    {
        if (goal.Length > 384 || goal.Any(char.IsControl)) return null;
        string command = goal.Trim();
        if (command.EndsWith('.') || command.EndsWith('!')) command = command[..^1].TrimEnd();
        Match toggle = Toggle.Match(command);
        if (toggle.Success)
        {
            string setting = toggle.Groups["setting"].Value.ToLowerInvariant();
            string name = setting switch
            {
                "vpn" or "впн" => "SetVpnConnected",
                "strict dns" or "строгий dns" or "строгий днс" => "SetStrictDns",
                _ => "SetFlushDnsOnStart",
            };
            string verb = toggle.Groups["verb"].Value.ToLowerInvariant();
            if (name != "SetVpnConnected" && verb is ("connect" or "disconnect" or "start" or "stop" or "подключи" or "запусти" or "останови")) return null;
            bool enable = verb is "connect" or "enable" or "turn on" or "start" or "включи" or "подключи" or "запусти";
            return new(name, enable);
        }

        Match route = Direct.Match(command);
        if (route.Success)
        {
            string app = route.Groups["app"].Value;
            if (!route.Groups["verb"].Success && app.Contains(' ') && !AssistantApplicationTarget.IsKnownAppName(app))
                return null;
            return new("RouteApplicationDirect", Application: app);
        }
        route = Exclusive.Match(command);
        if (route.Success) return new("RouteOnlyApplication", Application: route.Groups["app"].Value);

        Match page = Page.Match(command);
        if (!page.Success) return null;
        return new(page.Groups["page"].Value.ToLowerInvariant() switch
        {
            "server settings" or "серверы" => "OpenServerSettings",
            "subscriptions" or "подписки" => "OpenSubscriptions",
            "network settings" or "сеть" => "OpenNetworkSettings",
            "applications" or "приложения" => "OpenApplications",
            "tools" or "инструменты" => "OpenTools",
            _ => "OpenFreeConfigurations",
        });
    }
}
