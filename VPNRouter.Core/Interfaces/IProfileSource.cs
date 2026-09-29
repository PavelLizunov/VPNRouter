using VPNRouter.Core.Models;

namespace VPNRouter.Core.Interfaces;

public interface IProfileSource
{
    int Priority { get; }

    string SourceName { get; }

    Task<ProfileCollection?> LoadAsync(CancellationToken ct = default);

    bool IsAvailable();
}
