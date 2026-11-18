using System.IO.Compression;
using System.Security.Cryptography;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class WindowsStandardUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractVerifiedWindowsArchive_PublishesMatchingReleaseLayout(bool bootstrap)
    {
        var work = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(work.FullName, "release.zip");
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                using (var gui = new StreamWriter(zip.CreateEntry(bootstrap ? "VPNRouter.GUI.exe" : "app/VPNRouter.GUI.exe").Open()))
                    gui.Write("launcher");
                using var app = new StreamWriter(zip.CreateEntry((bootstrap ? "_bootstrap/" : "app/") + "VPNRouter.App.exe").Open());
                app.Write("release");
            }
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            var output = Path.Combine(work.FullName, "publish");
            Assert.Throws<InvalidDataException>(() => UpdateChecker.ExtractVerifiedWindowsArchive(file, new string('0', 64), output));
            Assert.False(Directory.Exists(output));
            var payload = UpdateChecker.ExtractVerifiedWindowsArchive(file, digest, output);
            Assert.Equal("release", File.ReadAllText(Path.Combine(payload, "VPNRouter.App.exe")));
            Assert.Equal("launcher", File.ReadAllText(Path.Combine(payload, "VPNRouter.GUI.exe")));
        }
        finally { work.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("https://github.com/PavelLizunov/VPNRouter/releases/download/v1.2.3/VPNRouter-v1.2.3-win.zip", true)]
    [InlineData("https://github.com/PavelLizunov/VPNRouter/releases/download/v1.2.3/VPNRouter-update-v1.2.3-win.zip", true)]
    [InlineData("https://example.test/releases/download/v1.2.3/VPNRouter-v1.2.3-win.zip", false)]
    [InlineData("https://github.com/PavelLizunov/VPNRouter/releases/download/v1.2.2/VPNRouter-v1.2.3-win.zip", false)]
    [InlineData("https://github.com/PavelLizunov/VPNRouter/releases/download/v1.2.3/VPNRouter-install-v1.2.3.zip", true)]
    [InlineData("https://github.com/PavelLizunov/VPNRouter/releases/download/v1.2.3/VPNRouter-install-v1.2.2.zip", false)]
    public void OnlyRequestedOfficialReleaseIsEligible(string url, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsOfficialWindowsRelease(new Uri(url), "1.2.3"));
        Assert.Equal(expected, UpdateChecker.IsOfficialWindowsRelease(new Uri(url + ".sha256"), "1.2.3", checksum: true));
        Assert.False(UpdateChecker.IsOfficialWindowsRelease(new Uri(url), "1.2.3", checksum: true));
    }
}
