#nullable enable

namespace VPNRouter.Core.Services;

public enum StrictDnsAction
{
    None,
    FailOpen,
    ReArm,
}

public static class StrictDnsFailoverPolicy
{
    public static StrictDnsAction Decide(bool strictDnsSoleDriver, bool proxyHealthy, bool currentlyFailedOver)
    {
        bool shouldSuppress = strictDnsSoleDriver && !proxyHealthy;

        if (shouldSuppress && !currentlyFailedOver) return StrictDnsAction.FailOpen;
        if (!shouldSuppress && currentlyFailedOver) return StrictDnsAction.ReArm;
        return StrictDnsAction.None;
    }
}
