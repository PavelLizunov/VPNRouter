using System.Net;
using MaxMind.GeoIP2;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tools.PoolAggregator;

internal sealed class MmdbCountryLookup : IFreeConfigCountryLookup, IDisposable
{
    private readonly DatabaseReader _reader;

    public MmdbCountryLookup(string databasePath)
    {
        _reader = new DatabaseReader(databasePath);
    }

    public string? LookupCountry(IPAddress address)
    {
        try
        {
            if (_reader.TryCountry(address, out var response))
            {
                return response?.Country?.IsoCode;
            }
        }
        catch
        {
            // Unresolvable or private IP
        }
        return null;
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
