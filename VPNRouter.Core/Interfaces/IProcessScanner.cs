using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Interfaces;

public interface IProcessScanner
{
    ScanResult ScanForProfile(Profile profile);
}
