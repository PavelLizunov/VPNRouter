using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Pure description of what a storage layer should write when a user picks
/// a profile (or clears their selection). Nullable fields mean "leave the
/// existing stored value alone" — picking "No profile" should not clobber
/// per-app picks the user accumulated outside the profile system.
///
/// <para>Used by the Android Profiles overlay (AND-PROFILES) to translate
/// catalog entries into <c>AndroidStorage</c> writes. Mirrors what desktop
/// achieves through <c>MainWindowViewModel.LoadApps</c> +
/// <c>SaveSettings</c>, but produced as a value object so VPNRouter.Tests
/// can pin the rule without an Android runtime.</para>
/// </summary>
public sealed class ProfileApplyPlan
{
    /// <summary>
    /// Reverse-DNS package IDs to write into the per-app filter list. <c>null</c>
    /// = keep the existing list (used when "No profile" is selected — clearing
    /// the active profile shouldn't drop the user's manual app picks).
    /// </summary>
    public List<string>? AndroidPackages { get; init; }

    /// <summary>"include" when applying a profile, <c>null</c> when clearing.</summary>
    public string? PerAppMode { get; init; }

    /// <summary>"include" when applying a profile, <c>null</c> when clearing.</summary>
    public string? PerAppLastMode { get; init; }

    /// <summary>"split" when applying a profile, "full" when clearing.</summary>
    public string? RoutingMode { get; init; }

    /// <summary>
    /// Profile's <c>block_on_vpn_fail</c> value when applying. <c>null</c>
    /// when clearing — leak-protection state is orthogonal to the profile
    /// concept and shouldn't reset on profile clear.
    /// </summary>
    public bool? BlockOnVpnFail { get; init; }

    /// <summary>
    /// Profile name to mark as active. <c>null</c> when clearing.
    /// </summary>
    public string? ActiveProfileName { get; init; }
}

public static class ProfileApplication
{
    /// <summary>
    /// Compute the storage writes for either applying a profile or clearing
    /// the active selection. Pure function; safe to call from any thread.
    ///
    /// <para>"Apply profile" semantics: replace per-app filter, switch to
    /// split-tunnel routing, propagate the profile's leak-protection toggle.
    /// "Clear profile" semantics: drop the active-profile name, switch to
    /// full-tunnel routing, leave per-app config + leak protection alone so
    /// the user's manual picks survive the round trip.</para>
    /// </summary>
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
