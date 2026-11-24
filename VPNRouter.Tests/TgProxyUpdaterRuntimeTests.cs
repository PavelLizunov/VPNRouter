using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TgProxyUpdaterRuntimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActivateRuntimeAt_PublishesProbedFilesAndPreservesUnrelatedData(bool replacePython)
    {
        using var sandbox = new TempDir();
        var root = sandbox.Path;
        var stage = Path.Combine(root, ".stage");
        SeedFile(root, "python/old.pyd");
        SeedFile(root, "proxy/old.py");
        SeedFile(root, "local-settings.json");
        File.WriteAllText(Path.Combine(root, "version.txt"), "old");
        SeedFile(stage, "proxy/tg_ws_proxy.py");
        File.WriteAllText(Path.Combine(stage, "version.txt"), TgProxyUpdater.SupportedProxyVersion);
        if (replacePython) SeedFile(stage, "python/new.pyd");

        TgProxyUpdater.ActivateRuntimeAt(root, stage);

        Assert.True(File.Exists(Path.Combine(root, "proxy", "tg_ws_proxy.py")));
        Assert.False(File.Exists(Path.Combine(root, "proxy", "old.py")));
        Assert.Equal(replacePython, File.Exists(Path.Combine(root, "python", "new.pyd")));
        Assert.Equal(!replacePython, File.Exists(Path.Combine(root, "python", "old.pyd")));
        Assert.Equal(TgProxyUpdater.SupportedProxyVersion, File.ReadAllText(Path.Combine(root, "version.txt")));
        Assert.Equal("stub", File.ReadAllText(Path.Combine(root, "local-settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreInterruptedActivationAt_RestoresOldAndRemovesPartialFreshEntries(bool pythonAlreadyPublished)
    {
        using var sandbox = new TempDir();
        var root = sandbox.Path;
        var journal = Path.Combine(root, ".activating");
        SeedFile(journal, ".previous/python/old.pyd");
        SeedFile(pythonAlreadyPublished ? root : journal, "python/new.pyd");
        SeedFile(journal, ".previous/proxy.absent");
        SeedFile(root, "proxy/tg_ws_proxy.py");
        SeedFile(journal, "version.txt");
        File.WriteAllText(Path.Combine(root, "version.txt"), "original");

        TgProxyUpdater.RestoreInterruptedActivationAt(root);
        TgProxyUpdater.RestoreInterruptedActivationAt(root);

        Assert.True(File.Exists(Path.Combine(root, "python", "old.pyd")));
        Assert.False(File.Exists(Path.Combine(root, "python", "new.pyd")));
        Assert.False(Directory.Exists(Path.Combine(root, "proxy")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "version.txt")));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void ActivateRuntimeAt_ExistingJournalPreservesOldInstallationAndStage()
    {
        using var sandbox = new TempDir();
        var root = sandbox.Path;
        var stage = Path.Combine(root, ".stage");
        SeedFile(root, ".activating/.previous/other");
        SeedFile(root, "proxy/old.py");
        SeedFile(stage, "proxy/new.py");

        Assert.ThrowsAny<IOException>(() => TgProxyUpdater.ActivateRuntimeAt(root, stage));
        Assert.True(File.Exists(Path.Combine(root, "proxy", "old.py")));
        Assert.True(File.Exists(Path.Combine(stage, "proxy", "new.py")));
    }

    [Fact]
    public void IsPythonCurrentAt_FalseWhenLegacyMarkerMissing()
    {
        using var sandbox = new TempDir();
        var pythonDir = Path.Combine(sandbox.Path, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        File.WriteAllText(Path.Combine(pythonDir, "python314.dll"), "stub");

        Assert.False(TgProxyUpdater.IsPythonCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsPythonCurrentAt_FalseWhenMarkerVersionDiffers()
    {
        using var sandbox = new TempDir();
        var pythonDir = Path.Combine(sandbox.Path, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        File.WriteAllText(Path.Combine(pythonDir, "python314.dll"), "stub");
        File.WriteAllText(
            Path.Combine(pythonDir, TgProxyUpdater.PythonVersionMarkerFileName),
            "3.12.7");

        Assert.False(TgProxyUpdater.IsPythonCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsPythonCurrentAt_TrueForPinnedVersion()
    {
        using var sandbox = new TempDir();
        var pythonDir = Path.Combine(sandbox.Path, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        File.WriteAllText(Path.Combine(pythonDir, "python314.dll"), "stub");
        File.WriteAllText(
            Path.Combine(pythonDir, TgProxyUpdater.PythonVersionMarkerFileName),
            TgProxyUpdater.PythonVersion);

        Assert.True(TgProxyUpdater.IsPythonCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsDependenciesCurrentAt_FalseForMarkerOnlyLegacyInstall()
    {
        using var sandbox = new TempDir();
        var certifiDir = Path.Combine(sandbox.Path, "python", "Lib", "certifi");
        Directory.CreateDirectory(certifiDir);
        File.WriteAllText(Path.Combine(certifiDir, "__init__.py"), "stub");

        Assert.False(TgProxyUpdater.IsDependenciesCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsDependenciesCurrentAt_FalseForOldPackageVersions()
    {
        using var sandbox = new TempDir();
        SeedPinnedDependencies(sandbox.Path);
        var lib = Path.Combine(sandbox.Path, "python", "Lib");
        Directory.Delete(Path.Combine(lib, "certifi-2026.11.20.dist-info"), recursive: true);
        Directory.CreateDirectory(Path.Combine(lib, "certifi-2026.7.22.dist-info"));
        Directory.Delete(Path.Combine(lib, "cryptography-50.0.2.dist-info"), recursive: true);
        Directory.CreateDirectory(Path.Combine(lib, "cryptography-46.0.5.dist-info"));

        Assert.False(TgProxyUpdater.IsDependenciesCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsDependenciesCurrentAt_FalseForConflictingCffiAbi()
    {
        using var sandbox = new TempDir();
        SeedPinnedDependencies(sandbox.Path);
        var lib = Path.Combine(sandbox.Path, "python", "Lib");
        File.WriteAllText(Path.Combine(lib, "_cffi_backend.cp312-win_amd64.pyd"), "old");

        Assert.False(TgProxyUpdater.IsDependenciesCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsDependenciesCurrentAt_TrueForPinnedLayout()
    {
        using var sandbox = new TempDir();
        SeedPinnedDependencies(sandbox.Path);

        Assert.True(TgProxyUpdater.IsDependenciesCurrentAt(sandbox.Path));
    }

    private static void SeedPinnedDependencies(string baseDir)
    {
        var lib = Path.Combine(baseDir, "python", "Lib");
        SeedFile(lib, "certifi/__init__.py");
        SeedFile(lib, "certifi/cacert.pem");
        SeedFile(lib, "cryptography/__init__.py");
        SeedFile(lib, "cryptography/hazmat/bindings/_rust.pyd");
        SeedFile(lib, "cffi/__init__.py");
        SeedFile(lib, "_cffi_backend.cp314-win_amd64.pyd");
        SeedFile(lib, "pycparser/__init__.py");
        Directory.CreateDirectory(Path.Combine(lib, "certifi-2026.11.20.dist-info"));
        Directory.CreateDirectory(Path.Combine(lib, "cryptography-50.0.2.dist-info"));
        Directory.CreateDirectory(Path.Combine(lib, "cffi-2.1.1.dist-info"));
        Directory.CreateDirectory(Path.Combine(lib, "pycparser-3.0.dist-info"));
    }

    private static void SeedFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stub");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tgproxy-runtime-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
