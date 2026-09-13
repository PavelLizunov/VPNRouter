using System.Net;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Interface for resolving an IP address to an ISO-2 country code.
/// Allows injecting fast offline readers (e.g. MaxMind MMDB) without coupling Core to specific database packages.
/// </summary>
public interface IFreeConfigCountryLookup
{
    /// <summary>
    /// Looks up the two-letter ISO country code for the given IP address, or null if unknown.
    /// </summary>
    string? LookupCountry(IPAddress address);
}
