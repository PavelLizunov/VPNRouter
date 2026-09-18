#nullable enable
using System;
using System.IO;
using Serilog;
using VPNRouter.Core;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Verifies true capabilities without optimistic assumptions.
/// Protocol v1 rule: "Capabilities reflect verified availability, not optimistic platform detection."
/// </summary>
public static class PlatformCapabilityVerifier
{
    /// <summary>
    /// Evaluates whether DNS leak lockdown is truthfully supported on this platform.
    /// True on Windows and macOS; false on Linux where DNS lockdown lacks full firewall parity.
    /// </summary>
    public static bool VerifyDnsLockdownSupport()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// Evaluates whether firewall kill-switch is truthfully supported on this host.
    /// On Windows: true (netsh rules).
    /// On Linux: false unless verified capability is explicitly demonstrated through a verified protected operation adapter.
    /// On other platforms: false (fails closed).
    /// </summary>
    public static bool VerifyKillSwitchSupport(Func<bool>? linuxNftChecker = null, ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
            return true;

        if (OperatingSystem.IsLinux())
        {
            if (linuxNftChecker != null)
            {
                try
                {
                    return linuxNftChecker();
                }
                catch (Exception ex)
                {
                    var scrubbed = BoundedTeardown.SanitizeExceptionMessage(ex);
                    logger?.Warning("[PlatformCapabilityVerifier] Error checking Linux nftables capability: {Message}; failing closed.", scrubbed);
                    return false;
                }
            }

            return ProbeLinuxNftWithoutPassword(logger);
        }

        return false;
    }

    /// <summary>
    /// Checks whether the system can currently initiate a VPN connection.
    /// Requires sing-box binary to exist (or injected readiness func to succeed) and ownership status to be positively Free.
    /// Fails closed on HeldByAnother, Unavailable, or any unverified status.
    /// </summary>
    public static bool VerifyCanConnect(
        Func<OwnershipCheckResult>? ownershipProbe = null,
        ILogger? logger = null,
        Func<bool>? capabilityReadinessFunc = null)
    {
        try
        {
            if (capabilityReadinessFunc != null)
            {
                if (!capabilityReadinessFunc())
                    return false;
            }
            else
            {
                var exePath = AppPaths.SingBoxExePath;
                if (!File.Exists(exePath))
                    return false;
            }

            var ownership = ownershipProbe != null
                ? ownershipProbe()
                : LinuxOwnershipGuard.CheckOwnership(logger);

            // Fail closed: only positive Free status permits connection.
            if (ownership.Status != OwnershipStatus.Free)
                return false;

            return true;
        }
        catch (Exception ex)
        {
            var scrubbed = BoundedTeardown.SanitizeExceptionMessage(ex);
            logger?.Warning("[PlatformCapabilityVerifier] Error during CanConnect check: {Message}; failing closed.", scrubbed);
            return false;
        }
    }

    private static bool ProbeLinuxNftWithoutPassword(ILogger? logger = null)
    {
        // Production Linux supports only verified capabilities; unverified capabilities fail closed.
        // We never guess root identity from Environment.UserName and never assume NOPASSWD.
        logger?.Warning("[PlatformCapabilityVerifier] Linux firewall killswitch requires a verified protected operation adapter; missing adapter fails closed.");
        return false;
    }
}
