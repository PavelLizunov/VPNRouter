namespace VPNRouter.Tests;

public sealed class BuildLinuxAppImageToolPinTests
{
    private const string PinnedUrl =
        "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage";

    private const string PinnedSha256 =
        "ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0";

    private const long PinnedSize = 15092216;

    [Fact]
    public void BuildAppImageStep_PinsImmutableToolAndVerifiesDigest()
    {
        var yml = File.ReadAllText(FindBuildLinuxWorkflow());

        Assert.DoesNotContain("AppImage/AppImageKit", yml);
        Assert.DoesNotContain("continuous", yml);

        Assert.Contains(PinnedUrl, yml);
        Assert.Contains($"APPIMAGETOOL_SHA256=\"{PinnedSha256}\"", yml);
        Assert.Contains($"APPIMAGETOOL_SIZE={PinnedSize}", yml);

        Assert.DoesNotContain("<pinned-sha256>", yml);

        var shaIndex = yml.IndexOf("sha256sum -c", StringComparison.Ordinal);
        var chmodIndex = yml.IndexOf("chmod +x appimagetool", StringComparison.Ordinal);
        Assert.True(shaIndex >= 0, "sha256sum -c gate missing.");
        Assert.True(chmodIndex >= 0, "chmod +x appimagetool missing.");
        Assert.True(shaIndex < chmodIndex, "Digest must be verified before chmod +x.");
    }

    private static string FindBuildLinuxWorkflow()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir != null;
             dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".github", "workflows", "build-linux.yml");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("Could not locate .github/workflows/build-linux.yml.");
    }
}
