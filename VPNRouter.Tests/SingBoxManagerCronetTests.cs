using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SingBoxManagerCronetTests
{
    [Fact]
    public void TryColocateCronet_NoBundledLib_ReturnsFalseWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "vpnr-cronet-nb-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "app");
        var binDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(binDir);
        try
        {
            var singBox = Path.Combine(binDir, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            File.WriteAllText(singBox, "stub");
            Assert.False(SingBoxManager.TryColocateCronet(singBox, bundled, null));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
