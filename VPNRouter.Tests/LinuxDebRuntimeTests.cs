using VPNRouter.Core.Platform.Linux;

namespace VPNRouter.Tests;

public sealed class LinuxDebRuntimeTests
{
    [Fact]
    public void ProtectedRuntime_IsSelectedOnlyFromExactPackageRoot()
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.Equal("/opt/vpnrouter", LinuxDesktopPackage.InstallRoot);

        var root = Path.Combine(Path.GetTempPath(), $"vpnrouter-deb-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var expected = Path.Combine(root, "sing-box");

            var selected = LinuxDesktopPackage.TryGetProtectedRuntimePath(root, root, (_, _) => true);
            Assert.NotNull(selected);
            Assert.Equal(expected, selected!);

            var selectedWithSlash = LinuxDesktopPackage.TryGetProtectedRuntimePath(
                root + Path.DirectorySeparatorChar, root, (_, _) => true);
            Assert.NotNull(selectedWithSlash);
            Assert.Equal(expected, selectedWithSlash!);

            Assert.Null(LinuxDesktopPackage.TryGetProtectedRuntimePath(
                Path.Combine(root, "elsewhere"), root, (_, _) => true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProtectedRuntime_RejectsAnUntrustedParentDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        var visited = new List<string>();
        var trusted = LinuxDesktopPackage.IsTrustedPackageDirectory("/opt/vpnrouter", (path, directory) =>
        {
            Assert.True(directory);
            visited.Add(path);
            return path != "/opt";
        });

        Assert.False(trusted);
        Assert.Equal(new[] { "/opt/vpnrouter", "/opt" }, visited);
    }

    [Fact]
    public void DebianPackageDeclaresCapabilitiesForTheProtectedRuntime()
    {
        var workflow = ReadRepositoryFile(".github/workflows/build-linux.yml");
        var root = LinuxDesktopPackage.InstallRoot;
        Assert.Contains($"mkdir -p \"$PKG{root}\"", workflow);
        Assert.Contains($"exec {root}/VPNRouter.App", workflow);
        Assert.Matches(@"(?m)^ *Depends:[^\r\n]*\blibcap2-bin\b", workflow);
        Assert.Contains("$PKG/DEBIAN/postinst", workflow);
        Assert.Contains("$PKG/usr/libexec/vpnrouter-update-helper", workflow);
        Assert.Contains("dpkg-deb --raw-extract", workflow);

        var postinstall = ReadRepositoryFile("packaging/linux/postinst");
        Assert.Contains($"SINGBOX={root}/{LinuxDesktopPackage.SingBoxFileName}", postinstall);
        var helper = ReadRepositoryFile("packaging/linux/vpnrouter-update-helper");
        foreach (var script in new[] { postinstall, helper })
        {
            Assert.Contains($"CAPABILITIES={LinuxDesktopPackage.RuntimeCapabilities}", script);
            Assert.Contains("setcap \"$CAPABILITIES\"", script);
            Assert.Contains("setcap -q -v \"$CAPABILITIES\"", script);
        }

        var updater = ReadRepositoryFile("VPNRouter.Core/Services/UpdateChecker.Apply.cs");
        Assert.Contains("umask 022; exec /bin/cp -rfT --remove-destination -- \\\"$@\\\"\", \"vpnrouter-copy\",", updater);
        Assert.Contains("new[] { \"/bin/chmod\", \"0755\"", updater);
        Assert.Contains("cp -rfT --remove-destination -- \"$stage/payload\" \"$DST\"", helper);
        Assert.Contains("VerifyPrivilegedUpdateTree(sourceDir, installed: false)", updater);
        Assert.Contains("VerifyPrivilegedUpdateTree(installDir, installed: true)", updater);
        Assert.Contains("needsRoot && LinuxDesktopPackage.TryGetProtectedRuntimePath(installDir)", updater);
        Assert.Contains("RunWithCapture(pkexec!, [setcap, caps, runtime]", updater);
        Assert.Contains("RunWithCapture(setcap, [\"-q\", \"-v\", caps, runtime]", updater);
    }

    [Fact]
    public void ProtectedRuntime_RejectsWritableOrSymlinkedPackagePaths()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"vpnrouter-deb-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var runtime = Path.Combine(root, "sing-box");
        var link = Path.Combine(root, "sing-box-link");
        try
        {
            File.WriteAllText(runtime, "fake sing-box");
            File.SetUnixFileMode(runtime, (UnixFileMode)0x1B6); // 0666
            File.SetUnixFileMode(root, (UnixFileMode)0x1FF);   // 0777

            Assert.False(LinuxDesktopPackage.IsTrustedPackagePath(root, directory: true));
            Assert.False(LinuxDesktopPackage.IsTrustedPackagePath(runtime, directory: false));

            File.CreateSymbolicLink(link, runtime);
            Assert.False(LinuxDesktopPackage.IsTrustedPackagePath(link, directory: false));
            var directoryLink = Path.Combine(root, "directory-link");
            Directory.CreateSymbolicLink(directoryLink, "/");
            Assert.False(LinuxDesktopPackage.IsTrustedPackageDirectory(directoryLink + "/"));
            Assert.Null(LinuxDesktopPackage.TryGetProtectedRuntimePath(
                root, root, LinuxDesktopPackage.IsTrustedPackagePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {relativePath}");
    }
}
