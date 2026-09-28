namespace VPNRouter.App.ViewModels;

internal static class AutoSelectStatus
{
    public static (string? name, string? ip) ResolveSubscribeLabel(
        bool autoSelectOn,
        bool hasAutoNode,
        string? autoName,
        string? autoIp,
        string autoLabel,
        string? nominalName,
        string? nominalIp)
    {
        if (autoSelectOn)
            return hasAutoNode ? (autoName, autoIp) : (autoLabel, (string?)null);
        return (nominalName, nominalIp);
    }
}
