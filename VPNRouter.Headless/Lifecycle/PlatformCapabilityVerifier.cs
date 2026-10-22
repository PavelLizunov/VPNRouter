#nullable enable
using System;
using System.IO;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;

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
        return GetUnavailableReasonCode(ownershipProbe, logger, capabilityReadinessFunc) == null;
    }

    internal static string? RuntimeFailureCode(SingBoxRuntimeFailure failure) => failure switch
    {
        SingBoxRuntimeFailure.None => null,
        SingBoxRuntimeFailure.Missing => "runtime-missing",
        SingBoxRuntimeFailure.MissingCapabilities => "runtime-capabilities",
        SingBoxRuntimeFailure.Untrusted or SingBoxRuntimeFailure.Changed => "runtime-untrusted",
        SingBoxRuntimeFailure.PrerequisiteUnavailable => "sandbox",
        _ => "other"
    };

    internal static string? GetUnavailableReasonCode(
        Func<OwnershipCheckResult>? ownershipProbe = null,
        ILogger? logger = null,
        Func<bool>? capabilityReadinessFunc = null)
    {
        try
        {
            if (capabilityReadinessFunc != null)
            {
                if (!capabilityReadinessFunc()) return "other";
            }
            else
            {
                var policy = SingBoxRuntimePolicy.Current;
                if (policy != null)
                {
                    if (!policy.TryAuthorize(SingBoxRuntimeOperation.Probe, out var failure))
                        return RuntimeFailureCode(failure);
                }
                else if (!File.Exists(AppPaths.SingBoxExePath)) return "runtime-missing";
            }

            try
            {
                var ownership = ownershipProbe != null ? ownershipProbe() : LinuxOwnershipGuard.CheckOwnership(logger);
                return ownership.Status == OwnershipStatus.Free ? null : "ownership";
            }
            catch
            {
                return "ownership";
            }
        }
        catch (SingBoxRuntimePolicyException ex)
        {
            return RuntimeFailureCode(ex.Failure);
        }
        catch (Exception ex)
        {
            var scrubbed = BoundedTeardown.SanitizeExceptionMessage(ex);
            logger?.Warning("[PlatformCapabilityVerifier] Error during CanConnect check: {Message}; failing closed.", scrubbed);
            return "other";
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
