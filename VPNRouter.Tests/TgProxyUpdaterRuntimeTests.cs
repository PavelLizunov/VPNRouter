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

    [Theory]
    [InlineData(null)]
    [InlineData("3.12.7")]
    [InlineData(TgProxyUpdater.PythonVersion)]
    public void IsPythonCurrentAt_RequiresExactVersionMarker(string? version)
    {
        using var sandbox = new TempDir();
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);
        var marker = Path.Combine(sandbox.Path, "python", TgProxyUpdater.PythonVersionMarkerFileName);
        if (version == null) File.Delete(marker);
        else File.WriteAllText(marker, version);

        Assert.Equal(version == TgProxyUpdater.PythonVersion, TgProxyUpdater.IsPythonCurrentAt(sandbox.Path));
    }

    [Theory]
    [InlineData("python/python314.zip")]
    [InlineData("python/python3.dll")]
    [InlineData("python/vcruntime140.dll")]
    [InlineData("python/vcruntime140_1.dll")]
    [InlineData("python/Lib/cryptography/hazmat/bindings/_rust.pyd")]
    [InlineData("python/Lib/_cffi_backend.cp314-win_amd64.pyd")]
    [InlineData("proxy/tg_ws_proxy.py")]
    public void IsInstalledAt_RejectsEmptyAndMissingRuntimeFile(string name)
    {
        using var sandbox = new TempDir();
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);
        var path = Path.Combine(sandbox.Path, name);
        File.WriteAllText(path, string.Empty);
        Assert.False(TgProxyUpdater.IsInstalledAt(sandbox.Path));
        File.Delete(path);
        Assert.False(TgProxyUpdater.IsInstalledAt(sandbox.Path));
    }

    [Fact]
    public void IsPythonCurrentAt_RejectsTruncatedStandardLibraryWithIntactVersionMarker()
    {
        using var sandbox = new TempDir();
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);
        using (var file = File.OpenWrite(Path.Combine(sandbox.Path, "python/python314.zip")))
            file.SetLength(file.Length - 1);
        Assert.False(TgProxyUpdater.IsPythonCurrentAt(sandbox.Path));
    }

    [Theory]
    [InlineData("Lib")]
    [InlineData("..")]
    public void IsPythonCurrentAt_RequiresLaunchModulePaths(string missing)
    {
        using var sandbox = new TempDir();
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);
        var path = Path.Combine(sandbox.Path, "python", "python314._pth");
        var remaining = File.ReadAllLines(path).Where(line => line != missing).ToArray();
        File.WriteAllLines(path, remaining);
        Assert.False(TgProxyUpdater.IsPythonCurrentAt(sandbox.Path));
    }

    [Fact]
    public void ActivateRuntimeAt_WindowsLockedInterpreterPreservesOldSourceAndVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new TempDir();
        var root = sandbox.Path;
        var stage = Path.Combine(root, ".stage");
        AutostartContractTests.SeedPinnedRuntime(root);
        File.WriteAllText(Path.Combine(root, "version.txt"), "previous");
        AutostartContractTests.SeedPinnedRuntime(stage);
        using var held = File.OpenRead(Path.Combine(root, "python/python.exe"));

        Assert.ThrowsAny<IOException>(() => TgProxyUpdater.ActivateRuntimeAt(root, stage));
        Assert.True(File.Exists(Path.Combine(root, "python/python.exe")));
        Assert.True(File.Exists(Path.Combine(root, "proxy/tg_ws_proxy.py")));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(root, "version.txt")));
    }

    [Theory]
    [InlineData(".stage-")]
    [InlineData(".restored-")]
    public void CleanInactiveStages_OnlyRemovesMarkedInactiveStages(string prefix)
    {
        using var sandbox = new TempDir();
        var owned = Path.Combine(sandbox.Path, prefix + Guid.NewGuid().ToString("N"));
        var unmarked = Path.Combine(sandbox.Path, prefix + Guid.NewGuid().ToString("N"));
        var foreign = Path.Combine(sandbox.Path, prefix + "operator");
        var journal = Path.Combine(sandbox.Path, ".activating");
        SeedFile(owned, ".vpnrouter-tgproxy-stage");
        SeedFile(owned, "nested/payload");
        SeedFile(unmarked, "local-settings");
        SeedFile(foreign, ".vpnrouter-tgproxy-stage");
        SeedFile(journal, ".vpnrouter-tgproxy-stage");

        TgProxyUpdater.CleanInactiveStages(sandbox.Path);

        Assert.False(Directory.Exists(owned));
        Assert.True(File.Exists(Path.Combine(unmarked, "local-settings")));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void RestoreInterruptedActivationAt_RestoresPreviousSourceAndVersionAfterBothWerePublished()
    {
        using var sandbox = new TempDir();
        var root = sandbox.Path;
        var journal = Path.Combine(root, ".activating");
        SeedFile(root, "proxy/new.py");
        SeedFile(journal, ".previous/proxy/old.py");
        SeedFile(journal, ".previous/version.txt");
        File.WriteAllText(Path.Combine(root, "version.txt"), "new");
        SeedFile(journal, ".previous/python/old.pyd");
        SeedFile(journal, "python/new.pyd");

        TgProxyUpdater.RestoreInterruptedActivationAt(root);

        Assert.Equal("stub", File.ReadAllText(Path.Combine(root, "version.txt")));
        Assert.True(File.Exists(Path.Combine(root, "python/old.pyd")));
        Assert.True(File.Exists(Path.Combine(root, "proxy/old.py")));
        Assert.False(File.Exists(Path.Combine(root, "proxy/new.py")));
        Assert.False(Directory.Exists(journal));
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
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);
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
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);
        var lib = Path.Combine(sandbox.Path, "python", "Lib");
        File.WriteAllText(Path.Combine(lib, "_cffi_backend.cp312-win_amd64.pyd"), "old");

        Assert.False(TgProxyUpdater.IsDependenciesCurrentAt(sandbox.Path));
    }

    [Fact]
    public void IsDependenciesCurrentAt_TrueForPinnedLayout()
    {
        using var sandbox = new TempDir();
        AutostartContractTests.SeedPinnedRuntime(sandbox.Path);

        Assert.True(TgProxyUpdater.IsDependenciesCurrentAt(sandbox.Path));
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
