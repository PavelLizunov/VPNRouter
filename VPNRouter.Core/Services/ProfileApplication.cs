using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed class ProfileApplyPlan
{
    public List<string>? AndroidPackages { get; init; }

    public string? PerAppMode { get; init; }

    public string? PerAppLastMode { get; init; }

    public string? RoutingMode { get; init; }

    public bool? BlockOnVpnFail { get; init; }

    public string? ActiveProfileName { get; init; }
}

public static class ProfileApplication
{
    public static ProfileApplyPlan Plan(Profile? profile)
    {
        if (profile is null)
        {
            return new ProfileApplyPlan
            {
                ActiveProfileName = null,
                RoutingMode = "full",
            };
        }

        return new ProfileApplyPlan
        {
            ActiveProfileName = profile.Name,
            RoutingMode = "split",
            AndroidPackages = new List<string>(profile.AndroidPackages ?? new()),
            PerAppMode = "include",
            PerAppLastMode = "include",
            BlockOnVpnFail = profile.BlockOnVpnFail,
        };
    }
}
