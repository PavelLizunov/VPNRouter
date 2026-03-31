using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Interfaces;

/// <summary>
/// Scans running processes to resolve profile rules into concrete process names.
/// Windows: WMI for child process detection. macOS: ps/sysctl.
/// </summary>
public interface IProcessScanner
{
    ScanResult ScanForProfile(Profile profile);
}
