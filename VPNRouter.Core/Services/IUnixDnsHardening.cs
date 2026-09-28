#nullable enable
using Serilog;

namespace VPNRouter.Core.Services;

public interface IUnixDnsHardening
{
    void Apply(string dnsTarget, ILogger? logger);

    void Restore(ILogger? logger);

    void RestoreStrandedIfAny(ILogger? logger);
}

public sealed class NullUnixDnsHardening : IUnixDnsHardening
{
    public static NullUnixDnsHardening Default { get; } = new();

    public void Apply(string dnsTarget, ILogger? logger) { }

    public void Restore(ILogger? logger) { }

    public void RestoreStrandedIfAny(ILogger? logger) { }
}
