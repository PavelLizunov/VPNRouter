using System.Net;

namespace VPNRouter.Core.Services.FreeConfigs;

public interface IFreeConfigCountryLookup
{
    string? LookupCountry(IPAddress address);
}
