using System.Diagnostics;
using System.IO.Compression;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessUpdatePackagingContractTests
{
    [Theory]
    [InlineData("Business", true, false, true, null)]
    [InlineData("Standard", true, false, false, "require VPNRouterEdition=Business")]
    [InlineData("Business", true, true, false, "must not embed a BusinessConnectionFile")]
    [InlineData("Business", false, false, false, "require a private BusinessConnectionFile")]
    [InlineData("Business", false, true, true, null)]
    [InlineData("Standard", false, false, true, null)]
    public async Task BuildGuard_SeparatesSharedUpdatesFromPrivateProvisioning(
        string edition, bool shared, bool connection, bool succeeds, string? expectedError)
    {
        var directory = Path.Combine(Path.GetTempPath(), "business-build-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var privateFile = Path.Combine(directory, "synthetic-connection.txt");
            if (connection) File.WriteAllText(privateFile, "synthetic-fixture-only");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "msbuild", Path.Combine(FindRoot(), "VPNRouter.App", "VPNRouter.App.csproj"),
                "-nologo", "-verbosity:quiet", "-target:ValidateBusinessConnection",
                "-property:VPNRouterEdition=" + edition,
                "-property:BusinessSharedUpdate=" + shared.ToString().ToLowerInvariant(),
                "-property:BusinessConnectionFile=" + (connection ? privateFile : string.Empty)
            }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("MSBuild guard evaluation did not finish.");
            }
            var output = await stdout + await stderr;
            Assert.Equal(succeeds, process.ExitCode == 0);
            if (expectedError != null) Assert.Contains(expectedError, output);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void BusinessPackaging_UsesOneOfficialWindowsCorePin()
    {
        var root = FindRoot();
        var pin = File.ReadAllText(Path.Combine(root, "packaging", "windows", "business-core.ps1"));
        Assert.Contains("$BusinessSingBoxVersion = '1.14.4-vpnctl.3'", pin);
        Assert.Contains("$BusinessSingBoxAsset = 'sing-box-1.14.4-vpnctl.3-windows-amd64.zip'", pin);
        Assert.Contains(
            "$BusinessSingBoxSha256 = '11f5194ea8bf7b1796c9cfa3a160adf8748b2fd26b22bbaa1b1ff4b42f715825'",
            pin);

        foreach (var script in new[] { "build-business.ps1", "build-business-update.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(root, "tools", script));
            Assert.Contains("business-core.ps1", text);
            Assert.Contains(
                "-SingBoxVersion $BusinessSingBoxVersion -SingBoxSha256 $BusinessSingBoxSha256", text);
        }

        var verifier = File.ReadAllText(Path.Combine(root, "tools", "verify-business-core.ps1"));
        Assert.Contains("business-core.ps1", verifier);
        Assert.Contains("sing-box.exe", BusinessUpdatePackage.RequiredFiles);

        // The Standard Windows pin stays a separate build.ps1 parameter.
        var standard = File.ReadAllText(Path.Combine(root, "build.ps1"));
        Assert.Contains("[string]$SingBoxVersion = \"", standard);
        Assert.DoesNotContain("BusinessSingBoxVersion", standard);
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build-ui-mcp.yml"));
        Assert.Contains("./tools/verify-business-core.ps1", workflow);
    }

    [Fact]
    public void SharedPublishedAssemblies_AreBusinessAndContainNoEmbeddedConnection()
    {
        var directory = Environment.GetEnvironmentVariable("VPNROUTER_BUSINESS_SHARED_PAYLOAD_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(directory), "Published shared Business payload is verified by Windows packaging CI.");
        BusinessUpdatePackage.ValidateBuildIdentity(directory!, AppVersion.Version);
    }

    [Fact]
    public void SharedUpdateArchive_HasExpectedIdentityAndNoProvisioning()
    {
        var zip = Environment.GetEnvironmentVariable("VPNROUTER_BUSINESS_SHARED_UPDATE_ZIP");
        Assert.SkipUnless(!string.IsNullOrEmpty(zip), "Complete shared update ZIP is verified by its packaging job.");
        var directory = Path.Combine(Path.GetTempPath(), "business-shared-zip-" + Guid.NewGuid().ToString("N"));
        try
        {
            BusinessUpdatePackage.ValidateArchive(zip!);
            ZipFile.ExtractToDirectory(zip!, directory);
            var app = Path.Combine(directory, "app");
            Assert.Equal(app, BusinessUpdatePackage.ValidateStaged(directory, AppVersion.Version));
            if (OperatingSystem.IsWindows())
                Assert.Equal("sing-box version 1.14.4-vpnctl.3", ReadCoreVersion(app));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static string ReadCoreVersion(string appDirectory)
    {
        var core = Path.Combine(appDirectory, "sing-box.exe");
        Assert.True(File.Exists(core) && new FileInfo(core).Length > 0, "Packaged core is missing.");
        var start = new ProcessStartInfo(core)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Packaged core did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(5000), "Packaged core version inspection timed out.");
        Assert.True(Task.WaitAll(new Task[] { stdout, stderr }, 5000),
            "Packaged core version output did not finish.");
        Assert.Equal(0, process.ExitCode);
        var line = stdout.GetAwaiter().GetResult().Split('\n')
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith("sing-box version ", StringComparison.Ordinal));
        return line ?? throw new InvalidOperationException("Packaged core did not report its version.");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
