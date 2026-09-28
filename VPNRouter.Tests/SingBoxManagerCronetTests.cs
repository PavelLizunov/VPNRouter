using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SingBoxManagerCronetTests
{
    [Fact]
    public void TryColocateCronet_CopiesLibNextToSingBox_AndIsIdempotent()
    {
        var libName = OperatingSystem.IsWindows() ? "libcronet.dll"
                    : OperatingSystem.IsLinux()   ? "libcronet.so" : null;

        var root = Path.Combine(Path.GetTempPath(), "vpnr-cronet-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "app");
        var binDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(binDir);
        try
        {
            var singBox = Path.Combine(binDir, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            File.WriteAllText(singBox, "stub");
            if (libName != null) File.WriteAllText(Path.Combine(bundled, libName), "CRONET-BYTES");

            var ok = SingBoxManager.TryColocateCronet(singBox, bundled, null);

            if (libName == null)
            {
                Assert.False(ok);
                return;
            }
            Assert.True(ok);
            var dest = Path.Combine(binDir, libName);
            Assert.True(File.Exists(dest), "libcronet was not co-located next to sing-box");
            Assert.Equal("CRONET-BYTES", File.ReadAllText(dest));

            Assert.True(SingBoxManager.TryColocateCronet(singBox, bundled, null));
        }
        finally { try { Directory.Delete(root, true); } catch {  } }
    }

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
        finally { try { Directory.Delete(root, true); } catch {  } }
    }
}
