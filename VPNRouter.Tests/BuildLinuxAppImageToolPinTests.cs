namespace VPNRouter.Tests;

/// <summary>
/// Supply-chain guard for the Linux release build: appimagetool must be fetched
/// from the immutable AppImage/appimagetool release asset behind a fail-closed
/// size + SHA256 gate, never the retired rolling AppImageKit ref. See
/// plans/phase1-audit-p08-appimagetool-pin-2026-07-29.md (SUP-1).
/// </summary>
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

        // Retired rolling ref (supply-chain RCE vector) must be gone.
        Assert.DoesNotContain("AppImage/AppImageKit", yml);
        Assert.DoesNotContain("continuous", yml);

        // Immutable successor-repo asset is pinned with exact digest + size.
        Assert.Contains(PinnedUrl, yml);
        Assert.Contains($"APPIMAGETOOL_SHA256=\"{PinnedSha256}\"", yml);
        Assert.Contains($"APPIMAGETOOL_SIZE={PinnedSize}", yml);

        // Digest must be real, not the brief's placeholder.
        Assert.DoesNotContain("<pinned-sha256>", yml);

        // Verification must precede chmod/exec: size + sha256sum -c gate the
        // binary before it is made executable.
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
