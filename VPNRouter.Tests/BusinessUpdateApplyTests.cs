using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessUpdateApplyTests
{
    [Fact]
    public async Task ApplyIdentityRejectsChangedMetadataFilesAndUnverifiedPaths()
    {
        using var fixture = new BusinessPackageFixture();
        var previous = AppPaths.DataDir;
        AppPaths.OverrideDataDir(Path.Combine(fixture.Root, "data"));
        try
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                foreach (var path in Directory.EnumerateFiles(fixture.App, "*", SearchOption.AllDirectories))
                {
                    using var entry = zip.CreateEntry("app/" + Path.GetRelativePath(fixture.App, path).Replace(Path.DirectorySeparatorChar, '/')).Open();
                    using var input = File.OpenRead(path);
                    input.CopyTo(entry);
                }
            }
            var payload = bytes.ToArray();
            const string url = "https://updates.example.invalid/business.zip";
            var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
            var checker = new UpdateChecker(new UpdateSettings(), "2.50.5", new FakeHttpClient().SetupStream(url, payload));
            var legacy = new UpdateInfo { LatestVersion = fixture.Version, DownloadUrl = url, SizeBytes = payload.Length, FullChecksumSha256 = sha };
            var info = new UpdateSourceInfo(fixture.Version, url, "business.zip", url, payload.Length, sha, false, "");
            var staged = await checker.DownloadAndStageAsync(legacy, true, TestContext.Current.CancellationToken);
            Assert.NotEmpty(checker.ValidateBusinessStageForApply(staged, info));
            Assert.Throws<InvalidOperationException>(() => checker.ValidateBusinessStageForApply(fixture.Root, info));
            Assert.Throws<InvalidOperationException>(() => checker.ValidateBusinessStageForApply(staged, info with { AssetSha256 = new string('f', 64) }));
            Assert.Throws<InvalidOperationException>(() => checker.ValidateBusinessStageForApply(staged, info with { Version = "2.50.7" }));
            File.AppendAllText(Path.Combine(staged, "app", "sing-box.exe"), "changed after verified download");
            Assert.Throws<InvalidOperationException>(() => checker.ValidateBusinessStageForApply(staged, info));
        }
        finally { AppPaths.OverrideDataDir(previous); }
    }

    [Fact]
    public void WindowsHelperCopiesQuotedPathsAndPreservesProvisioningBeforeBusinessRelaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HelperFixture();
        File.WriteAllText(Path.Combine(fixture.Source, "payload.dll"), "new bits");
        Directory.CreateDirectory(Path.Combine(fixture.Source, "nested"));
        File.WriteAllText(Path.Combine(fixture.Source, "nested", "other.dll"), "second file");
        File.WriteAllText(Path.Combine(fixture.Destination, "payload.dll"), "old bits");
        var provisioning = Path.Combine(fixture.Destination, "business-provisioning.json");
        File.WriteAllText(provisioning, "retained private fixture");
        var script = fixture.PrepareScript(int.MaxValue, 0, 100);
        Assert.Equal(0, RunHelper(script));
        Assert.Equal("new bits", File.ReadAllText(Path.Combine(fixture.Destination, "payload.dll")));
        Assert.Equal("second file", File.ReadAllText(Path.Combine(fixture.Destination, "nested", "other.dll")));
        Assert.Equal("retained private fixture", File.ReadAllText(provisioning));
        Assert.Equal("VPNRouter.App.exe", File.ReadAllText(fixture.LaunchMarker));
    }

    [Fact]
    public void WindowsHelperTimesOutWithoutCopyOrRelaunchWhileOriginalProcessLives()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HelperFixture();
        File.WriteAllText(Path.Combine(fixture.Source, "payload.dll"), "new bits");
        File.WriteAllText(Path.Combine(fixture.Destination, "payload.dll"), "old bits");
        using var current = Process.GetCurrentProcess();
        var script = fixture.PrepareScript(current.Id, current.StartTime.ToUniversalTime().Ticks, 100);
        Assert.Equal(1, RunHelper(script));
        Assert.Equal("old bits", File.ReadAllText(Path.Combine(fixture.Destination, "payload.dll")));
        Assert.False(File.Exists(fixture.LaunchMarker));
        Assert.Contains("APP_EXIT_TIMEOUT", File.ReadAllText(fixture.Log));
    }

    [Fact]
    public void WindowsHelperRefusesSourceChangedAfterPreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HelperFixture();
        var source = Path.Combine(fixture.Source, "payload.dll");
        File.WriteAllText(source, "verified bits");
        File.WriteAllText(Path.Combine(fixture.Destination, "payload.dll"), "old bits");
        var script = fixture.PrepareScript(int.MaxValue, 0, 100);
        File.WriteAllText(source, "tampered bits");
        Assert.Equal(1, RunHelper(script));
        Assert.Equal("old bits", File.ReadAllText(Path.Combine(fixture.Destination, "payload.dll")));
        Assert.False(File.Exists(fixture.LaunchMarker));
        Assert.Contains("SOURCE_CHANGED", File.ReadAllText(fixture.Log));
    }

    [Fact]
    public void WindowsHelperCopyFailureDoesNotRelaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HelperFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Source, "nested"));
        File.WriteAllText(Path.Combine(fixture.Source, "nested", "payload.dll"), "new bits");
        File.WriteAllText(Path.Combine(fixture.Destination, "nested"), "blocking regular file");
        Assert.Equal(1, RunHelper(fixture.PrepareScript(int.MaxValue, 0, 100)));
        Assert.Equal("blocking regular file", File.ReadAllText(Path.Combine(fixture.Destination, "nested")));
        Assert.False(File.Exists(fixture.LaunchMarker));
    }

    [Fact]
    public void ElevatedHelperUsesEncodedArgumentsAndNormalUacVerb()
    {
        const string script = "throw 'quoted $(text)'; # fixture";
        var info = UpdateChecker.CreateBusinessHelperStartInfo(script, true);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(info.ArgumentList[^1])));
        Assert.DoesNotContain(script, info.Arguments);
    }

    private static int RunHelper(string script)
    {
        var info = UpdateChecker.CreateBusinessHelperStartInfo(script, false);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new TimeoutException("Isolated Business update helper did not exit.");
        }
        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    private sealed class HelperFixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "business-apply-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source' ; throw 'INJECTED");
        internal string Destination => Path.Combine(Root, "destination' ; throw 'INJECTED");
        internal string Log => Path.Combine(Root, "helper.log");
        internal string LaunchMarker => Path.Combine(Root, "launch.txt");
        internal HelperFixture()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Destination);
        }
        internal string PrepareScript(int parent, long started, int timeout)
        {
            var manifest = UpdateChecker.CreateBusinessFileManifest(Source);
            var path = Path.Combine(Root, "files.json");
            File.WriteAllBytes(path, manifest);
            var script = UpdateChecker.BuildBusinessApplyScript(Source, Destination, path,
                Convert.ToHexString(SHA256.HashData(manifest)), Log, parent, started, timeout, false);
            var marker = LaunchMarker.Replace("'", "''");
            return "function Start-Process { param($FilePath, $WorkingDirectory) [IO.File]::WriteAllText('" + marker +
                   "', [IO.Path]::GetFileName($FilePath)) }\n" + script;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
