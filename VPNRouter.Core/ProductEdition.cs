namespace VPNRouter.Core;

public static class ProductEdition
{
#if VPNROUTER_BUSINESS
    public const bool IsBusiness = true;
    public const string Name = "VPNRouter Business";
    public const string DataDirectoryName = "VPNRouterBusiness";
#else
    public const bool IsBusiness = false;
    public const string Name = "VPNRouter";
    public const string DataDirectoryName = "VPNRouter";
#endif
}
