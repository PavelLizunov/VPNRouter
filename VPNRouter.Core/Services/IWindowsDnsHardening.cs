#nullable enable

using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public interface IWindowsDnsHardening
{
    void Apply(AppSettings? settings, ILogger? logger);

    void Restore(ILogger? logger);

    void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger);

    void ReconcileLockdownForHealth(bool tunnelServing, AppSettings? settings, ILogger? logger);
}

public sealed class WindowsDnsHardeningImpl : IWindowsDnsHardening
{
    public static WindowsDnsHardeningImpl Default { get; } = new();

    private WindowsDnsHardeningImpl() { }

    public void Apply(AppSettings? settings, ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.Apply(settings, logger);
#endif
    }

    public void Restore(ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.Restore(logger);
#endif
    }

    public void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.EnableLockdownIfConfigured(settings, logger);
#endif
    }

    public void ReconcileLockdownForHealth(bool tunnelServing, AppSettings? settings, ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.ReconcileLockdownForHealth(tunnelServing, settings, logger);
#endif
    }
}
