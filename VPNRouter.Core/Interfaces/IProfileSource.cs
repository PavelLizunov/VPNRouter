using VPNRouter.Core.Models;

namespace VPNRouter.Core.Interfaces;

public interface IProfileSource
{
    /// <summary>Priority order — lower = higher priority</summary>
    int Priority { get; }

    string SourceName { get; }

    Task<ProfileCollection?> LoadAsync(CancellationToken ct = default);

    bool IsAvailable();
}
