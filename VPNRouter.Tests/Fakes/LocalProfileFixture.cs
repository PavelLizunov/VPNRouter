using System.Text.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests.Fakes;

/// <summary>Explicit profile intent for lifecycle tests, independent of bundled platform catalogs.</summary>
internal sealed class LocalProfileFixture : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vpnrouter-profile-{Guid.NewGuid():N}.json");

    public LocalProfileFixture(AppSettings settings, string name, bool blockOnVpnFail)
    {
        var collection = new ProfileCollection
        {
            Profiles = new List<Profile>
            {
                new() { Name = name, BlockOnVpnFail = blockOnVpnFail }
            }
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(collection, AppJsonContext.Default.ProfileCollection));
        settings.ProfileSources = new List<ProfileSource> { new() { Type = "local", Path = _path } };
        settings.ActiveProfile = name;
    }

    public void Dispose() => File.Delete(_path);
}
