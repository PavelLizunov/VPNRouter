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
            Assert.Equal(Path.Combine(directory, "app"), BusinessUpdatePackage.ValidateStaged(directory, AppVersion.Version));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
