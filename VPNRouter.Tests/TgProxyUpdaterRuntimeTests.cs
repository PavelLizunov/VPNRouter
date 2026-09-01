using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TgProxyUpdaterRuntimeTests
{
    [Fact]
    public void SupportedRuntime_PinsV110AndCertifi()
    {
        Assert.Equal("v1.10.0", TgProxyUpdater.SupportedProxyVersion);
        Assert.Equal(64, TgProxyUpdater.SupportedProxySourceSha256.Length);
        var source = File.ReadAllText(FindSource());
        Assert.Contains("releases/tags/{SupportedProxyVersion}", source);
        Assert.Contains("Version: \"2026.7.22\"", source);
        Assert.Contains("SupportedProxySourceSha256", source);
        Assert.Contains("import certifi; import proxy.tg_ws_proxy", source);
        Assert.DoesNotContain("releases/latest", source);
    }

    [Fact]
    public void ActivateSourceAt_SwapsSourceAndVersionTogether()
    {
        using var sandbox = new TempDir();
        var current = Path.Combine(sandbox.Path, "proxy");
        var staged = Path.Combine(sandbox.Path, "stage", "proxy");
        var backup = Path.Combine(sandbox.Path, "backup");
        var version = Path.Combine(sandbox.Path, "version.txt");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(current, "old.py"), "old");
        File.WriteAllText(Path.Combine(staged, "new.py"), "new");

        TgProxyUpdater.ActivateSourceAt(current, version, staged, backup, "v1.10.0");

        Assert.True(File.Exists(Path.Combine(current, "new.py")));
        Assert.False(File.Exists(Path.Combine(current, "old.py")));
        Assert.Equal("v1.10.0", File.ReadAllText(version));
    }

    [Fact]
    public void ActivateSourceAt_VersionWriteFailure_RestoresOldSource()
    {
        using var sandbox = new TempDir();
        var current = Path.Combine(sandbox.Path, "proxy");
        var staged = Path.Combine(sandbox.Path, "stage", "proxy");
        var backup = Path.Combine(sandbox.Path, "backup");
        var impossibleVersion = Path.Combine(sandbox.Path, "missing", "version.txt");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(current, "old.py"), "old");
        File.WriteAllText(Path.Combine(staged, "new.py"), "new");

        Assert.Throws<DirectoryNotFoundException>(() =>
            TgProxyUpdater.ActivateSourceAt(current, impossibleVersion, staged, backup, "v1.10.0"));

        Assert.True(File.Exists(Path.Combine(current, "old.py")));
        Assert.False(File.Exists(Path.Combine(current, "new.py")));
    }

    [Fact]
    public void ActivateSourceAt_FirstMoveFailure_DoesNotDeleteCurrentSource()
    {
        using var sandbox = new TempDir();
        var current = Path.Combine(sandbox.Path, "proxy");
        var staged = Path.Combine(sandbox.Path, "stage", "proxy");
        var backup = Path.Combine(sandbox.Path, "backup");
        var version = Path.Combine(sandbox.Path, "version.txt");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(current, "old.py"), "old");
        File.WriteAllText(Path.Combine(staged, "new.py"), "new");

        Assert.ThrowsAny<IOException>(() =>
            TgProxyUpdater.ActivateSourceAt(current, version, staged, backup, "v1.10.0"));

        Assert.True(File.Exists(Path.Combine(current, "old.py")));
        Assert.True(File.Exists(Path.Combine(staged, "new.py")));
    }

    [Fact]
    public void ActivateDependencyDirectoryAt_FirstMoveFailure_PreservesWorkingLib()
    {
        using var sandbox = new TempDir();
        var current = Path.Combine(sandbox.Path, "Lib");
        var staged = Path.Combine(sandbox.Path, "stage", "Lib");
        var backup = Path.Combine(sandbox.Path, "backup");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(current, "old.pyd"), "old");
        File.WriteAllText(Path.Combine(staged, "new.pyd"), "new");

        Assert.ThrowsAny<IOException>(() =>
            TgProxyUpdater.ActivateDependencyDirectoryAt(current, staged, backup));

        Assert.True(File.Exists(Path.Combine(current, "old.pyd")));
        Assert.True(File.Exists(Path.Combine(staged, "new.pyd")));
    }

    private static string FindSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "TgProxyUpdater.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("TgProxyUpdater.cs");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tgproxy-runtime-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
