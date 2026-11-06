using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessRollbackBackupTests
{
    [Fact]
    public async Task Downgrade_ValidatesAndBacksUpBeforeLaunching_WithoutChangingTheProfile()
    {
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();
        var launched = 0;
        fixture.Checker.ApplyBusinessUpdate(fixture.Staged, fixture.Info, startInfo =>
        {
            launched++;
            var backup = Assert.Single(fixture.Backups);
            Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(backup));
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                Assert.Equal(AppPaths.PrivateUnixFileMode, File.GetUnixFileMode(backup));
            Assert.False(File.Exists(fixture.InstallReceipt));
            fixture.AssertProfileUnchanged();
            Assert.Equal("powershell.exe", startInfo.FileName);
        });

        Assert.Equal(1, launched);
        Assert.Single(fixture.Backups);
        fixture.AssertProfileUnchanged();
    }

    [Theory]
    [InlineData("2.50.6")]
    [InlineData("2.50.7")]
    public async Task ForwardOrSameVersion_DoesNotBackUpOrDeleteTheReceipt(string currentVersion)
    {
        using var fixture = new Fixture(currentVersion);
        await fixture.StageAsync();
        var launched = 0;
        fixture.Checker.ApplyBusinessUpdate(fixture.Staged, fixture.Info, _ =>
        {
            launched++;
            Assert.Empty(fixture.Backups);
            Assert.Equal(fixture.ReceiptBytes, File.ReadAllBytes(fixture.InstallReceipt));
        });

        Assert.Equal(1, launched);
        Assert.Empty(fixture.Backups);
        fixture.AssertProfileUnchanged();
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("content")]
    [InlineData("unverified-directory")]
    public async Task UnverifiedOrChangedStage_CannotBackUpOrLaunch(string change)
    {
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();
        var info = fixture.Info;
        var staged = fixture.Staged;
        if (change == "metadata")
            info = info with { AssetSha256 = new string('f', 64) };
        else if (change == "content")
            File.AppendAllText(Path.Combine(staged, "app", "sing-box.exe"), "changed after staging");
        else
            staged = fixture.Package.Root;
        var launched = false;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Checker.ApplyBusinessUpdate(staged, info, _ => launched = true));

        Assert.False(launched);
        Assert.Empty(fixture.Backups);
        Assert.Equal(fixture.ReceiptBytes, File.ReadAllBytes(fixture.InstallReceipt));
        Assert.Empty(Directory.GetFiles(fixture.Staged, "business-update-files-*.json"));
        fixture.AssertProfileUnchanged();
    }

    [Fact]
    public async Task BackupReadFailure_PreventsHelperLaunchAndLeavesTheReceipt()
    {
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();
        var launched = false;
        using (var lockedConfig = new FileStream(AppPaths.ConfigYamlPath, FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => fixture.Checker.ApplyBusinessUpdate(
                fixture.Staged, fixture.Info, _ => launched = true));
        }

        Assert.False(launched);
        Assert.Empty(fixture.Backups);
        Assert.Equal(fixture.ReceiptBytes, File.ReadAllBytes(fixture.InstallReceipt));
        Assert.Empty(Directory.GetFiles(fixture.Staged, "business-update-files-*.json"));
        fixture.AssertProfileUnchanged();
    }

    [Fact]
    public async Task HelperLaunchFailure_RetainsThePrivateBackupAndRemovesItsLaunchManifest()
    {
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();
        var launches = 0;

        Assert.Throws<IOException>(() => fixture.Checker.ApplyBusinessUpdate(
            fixture.Staged, fixture.Info, _ =>
            {
                launches++;
                Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(Assert.Single(fixture.Backups)));
                throw new IOException("Synthetic helper launch failure.");
            }));

        Assert.Equal(1, launches);
        Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(Assert.Single(fixture.Backups)));
        Assert.Empty(Directory.GetFiles(fixture.Staged, "business-update-files-*.json"));
        fixture.AssertProfileUnchanged();
    }

    [Fact]
    public async Task MissingConfiguration_UsesTheExistingNoBackupOutcome()
    {
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();
        File.Delete(AppPaths.ConfigYamlPath);
        var statuses = new List<string>();
        fixture.Checker.StatusChanged += statuses.Add;
        var launches = 0;

        fixture.Checker.ApplyBusinessUpdate(fixture.Staged, fixture.Info, _ => launches++);

        Assert.Equal(1, launches);
        Assert.Empty(fixture.Backups);
        Assert.Contains(Strings.DowngradeNoConfigBackup, statuses);
        Assert.False(File.Exists(fixture.InstallReceipt));
        fixture.AssertProfileUnchanged(exceptConfiguration: true);
    }

    [Fact]
    public async Task ReceiptCleanupFailure_PreventsLaunchAfterCreatingTheBackup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();
        var attributes = File.GetAttributes(fixture.InstallReceipt);
        File.SetAttributes(fixture.InstallReceipt, attributes | FileAttributes.ReadOnly);
        var launched = false;
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Checker.ApplyBusinessUpdate(
                fixture.Staged, fixture.Info, _ => launched = true));
            Assert.False(launched);
            Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(Assert.Single(fixture.Backups)));
            Assert.Equal(fixture.ReceiptBytes, File.ReadAllBytes(fixture.InstallReceipt));
            Assert.Empty(Directory.GetFiles(fixture.Staged, "business-update-files-*.json"));
            fixture.AssertProfileUnchanged();
        }
        finally { File.SetAttributes(fixture.InstallReceipt, attributes); }
    }

    [Fact]
    public async Task UnsupportedProductionHost_RefusesBeforeBackupWithoutAnInjectedLauncher()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture("2.50.8");
        await fixture.StageAsync();

        Assert.Throws<PlatformNotSupportedException>(() =>
            fixture.Checker.ApplyBusinessUpdate(fixture.Staged, fixture.Info));

        Assert.Empty(fixture.Backups);
        Assert.Equal(fixture.ReceiptBytes, File.ReadAllBytes(fixture.InstallReceipt));
        fixture.AssertProfileUnchanged();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _previousData = AppPaths.DataDir;
        private readonly Dictionary<string, byte[]> _retained = new(StringComparer.Ordinal);
        internal BusinessPackageFixture Package { get; } = new("2.50.7");
        internal byte[] ConfigBytes { get; } = Encoding.UTF8.GetBytes(
            "app:\n  language: ru\n  routing_mode: full\n  business_subscription_source: synthetic-source\n" +
            "  business_subscription_cache: synthetic-cache\nvless:\n  active_server: fixture-choice\n" +
            "  account: synthetic-account\n");
        internal byte[] ReceiptBytes { get; } = Encoding.UTF8.GetBytes("fixture-time\n2.50.8\n");
        internal string DataDirectory => Path.Combine(Package.Root, "data");
        internal string InstallReceipt => Path.Combine(DataDirectory, ".update-installed-version");
        internal string[] Backups => Directory.GetFiles(DataDirectory,
            $"config.yaml.before-downgrade-to-{Package.Version}-*");
        internal UpdateChecker Checker { get; }
        internal UpdateSourceInfo Info { get; }
        internal string Staged { get; private set; } = string.Empty;

        internal Fixture(string currentVersion)
        {
            try
            {
                AppPaths.OverrideDataDir(DataDirectory);
                AppPaths.EnsureDirectories();
                Retain(AppPaths.ConfigYamlPath, ConfigBytes);
                Retain(Path.Combine(DataDirectory, "provisioning", "business.json"),
                    Encoding.UTF8.GetBytes("{\"account\":\"synthetic-account\",\"profile\":\"retained\"}"));
                Retain(Path.Combine(AppPaths.CacheDir, "subscription.json"), Encoding.UTF8.GetBytes("synthetic cached subscription"));
                Retain(Path.Combine(DataDirectory, "reports", "report.zip"), [1, 2, 3, 4]);
                Retain(Path.Combine(Package.Root, "standard", "config.yaml"), Encoding.UTF8.GetBytes("unrelated fixture"));
                File.WriteAllBytes(InstallReceipt, ReceiptBytes);

                using var bytes = new MemoryStream();
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var path in Directory.EnumerateFiles(Package.App, "*", SearchOption.AllDirectories))
                    {
                        using var entry = zip.CreateEntry("app/" + Path.GetRelativePath(Package.App, path)
                            .Replace(Path.DirectorySeparatorChar, '/')).Open();
                        using var input = File.OpenRead(path);
                        input.CopyTo(entry);
                    }
                }
                var payload = bytes.ToArray();
                var url = $"https://updates.example.invalid/VPNRouterBusiness-update-v{Package.Version}-win-x64.zip";
                var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
                Checker = new UpdateChecker(new UpdateSettings(), currentVersion, new FakeHttpClient().SetupStream(url, payload));
                Info = new(Package.Version, string.Empty, Path.GetFileName(new Uri(url).AbsolutePath),
                    url, payload.Length, sha, false, string.Empty);
            }
            catch
            {
                AppPaths.OverrideDataDir(_previousData);
                Package.Dispose();
                throw;
            }
        }

        internal async Task StageAsync()
        {
            var legacy = new UpdateInfo
            {
                LatestVersion = Info.Version, DownloadUrl = Info.DownloadUrl,
                SizeBytes = Info.AssetSize, FullChecksumSha256 = Info.AssetSha256
            };
            Staged = await Checker.DownloadAndStageAsync(legacy, true, TestContext.Current.CancellationToken);
        }

        internal void AssertProfileUnchanged(bool exceptConfiguration = false)
        {
            foreach (var (path, bytes) in _retained)
                if (!exceptConfiguration || path != AppPaths.ConfigYamlPath)
                    Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        private void Retain(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            _retained.Add(path, bytes);
        }

        public void Dispose()
        {
            AppPaths.OverrideDataDir(_previousData);
            Package.Dispose();
        }
    }
}
