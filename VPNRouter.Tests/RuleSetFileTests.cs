using System.Text;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class RuleSetFileTests
{
    [Fact]
    public void PublishedGeositeFile_IsValid()
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();

        Assert.Equal(155, bytes.Length);
        Assert.True(RuleSetFile.IsValidBinary(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10 * 1024)]
    public void IntactEnvelope_PassesTheCheck(int minLength)
    {
        var bytes = RuleSetSamples.IntactEnvelope(minLength);

        Assert.True(bytes.Length >= minLength);
        Assert.True(RuleSetFile.IsValidBinary(bytes));
    }

    [Fact]
    public async Task PackagedParserDistinguishesRuleContentsFromAnIntactEnvelope()
    {
        var configured = Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        var runtime = configured ?? Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
        bool linuxCi = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (linuxCi) Assert.False(string.IsNullOrWhiteSpace(configured), "Linux CI must supply VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipUnless(configured != null || File.Exists(runtime), "Requires the pinned or packaged sing-box parser");
        Assert.True(File.Exists(runtime), $"Missing sing-box parser: {runtime}");

        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("vpnr-ruleset-check-");
        try
        {
            var path = Path.Combine(dir.FullName, "candidate.tmp");
            foreach (var bytes in new[] { RuleSetSamples.KeywordRule(), RuleSetSamples.UpstreamGeositeTldRu(), RuleSetSamples.GeoIpRule() })
            {
                await File.WriteAllBytesAsync(path, bytes, ct);
                Assert.True(await RuleSetSamples.CheckWithRuntimeAsync(path, ct, runtime));
            }
            await File.WriteAllBytesAsync(path, RuleSetSamples.IntactEnvelope(), ct);
            Assert.True(RuleSetFile.IsValidBinary(path));
            Assert.False(await RuleSetSamples.CheckWithRuntimeAsync(path, ct, runtime));
            File.Delete(path);
            Assert.False(await RuleSetSamples.CheckWithRuntimeAsync(path, ct, runtime));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task RuleSetParserUsesTheSameDeploymentAsStartup()
    {
        var dir = Directory.CreateTempSubdirectory("vpnrouter-parser-layout-");
        var previous = VPNRouter.Core.AppPaths.DataDir;
        try
        {
            VPNRouter.Core.AppPaths.OverrideDataDir(dir.FullName);
            var settings = new VPNRouter.Core.Models.SingBoxSettings
            {
                ExecutablePath = Path.Combine(dir.FullName, "configured", "sing-box.exe")
            };
            var deployed = OperatingSystem.IsWindows() ? settings.ExecutablePath : VPNRouter.Core.AppPaths.SingBoxExePath;
            var bundle = Path.Combine(dir.FullName, "package", "sing-box");
            Directory.CreateDirectory(Path.GetDirectoryName(deployed)!);
            Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
            File.WriteAllBytes(bundle, [1]);
            Assert.Equal(bundle, VPNRouter.Core.AppPaths.SingBoxDeploymentSource(bundle, deployed));
            File.WriteAllBytes(deployed, [2, 3]);
            Assert.Equal(bundle, VPNRouter.Core.AppPaths.SingBoxDeploymentSource(bundle, deployed));
            File.Delete(bundle);
            Assert.Equal(deployed, VPNRouter.Core.AppPaths.SingBoxDeploymentSource(bundle, deployed));

            // Match the shipped bundle size when the test runner includes one, as a deployed install would.
            var packaged = new FileInfo(VPNRouter.Core.AppPaths.BundledSingBoxExePath);
            if (packaged.Exists)
            {
                using var installed = File.OpenWrite(deployed);
                installed.SetLength(packaged.Length);
            }
            var runner = new Fakes.FakeProcessRunner().OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.Zero, false));
            var sample = Path.Combine(dir.FullName, "candidate.srs");
            File.WriteAllBytes(sample, RuleSetSamples.KeywordRule());
            Assert.True(await RuleSetFile.CheckContentsAsync(sample, TestContext.Current.CancellationToken, runner, settings));
            Assert.Equal(deployed, Assert.Single(runner.RunCalls).ExecutablePath);

            var pinned = SingBoxRuntimePolicy.ForTestFile(Path.Combine(dir.FullName, "missing-runtime"), new string('0', 64));
            using var scope = SingBoxRuntimePolicy.EnterScope(pinned);
            Assert.False(await RuleSetFile.CheckContentsAsync(sample, TestContext.Current.CancellationToken, runner, settings));
            Assert.Single(runner.RunCalls);
        }
        finally
        {
            VPNRouter.Core.AppPaths.OverrideDataDir(previous);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ZeroFilledFileOfThePublishedLength_IsInvalid()
    {
        Assert.False(RuleSetFile.IsValidBinary(RuleSetSamples.ZeroFilled(155)));
    }

    [Fact]
    public void ValidHeaderWithZeroedBody_IsInvalid()
    {
        Assert.False(RuleSetFile.IsValidBinary(RuleSetSamples.ValidHeaderZeroedBody(155)));
    }

    [Fact]
    public void HtmlPage_IsInvalid()
    {
        Assert.False(RuleSetFile.IsValidBinary(
            Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Blocked</body></html>")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(151)]
    public void FileCutShort_IsInvalid(int length)
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu().AsSpan(0, length).ToArray();

        Assert.False(RuleSetFile.IsValidBinary(bytes));
    }

    [Fact]
    public void ZeroedTail_IsInvalid()
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();
        Array.Clear(bytes, bytes.Length - 40, 40);

        Assert.False(RuleSetFile.IsValidBinary(bytes));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)6)]
    [InlineData((byte)255)]
    public void VersionTheCoreDoesNotRead_IsInvalid(byte version)
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();
        bytes[3] = version;

        Assert.False(RuleSetFile.IsValidBinary(bytes));
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)5)]
    public void VersionTheCoreReads_IsValid(byte version)
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();
        bytes[3] = version;

        Assert.True(RuleSetFile.IsValidBinary(bytes));
    }

    [Fact]
    public void PathOverload_ReadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "vpnr-ruleset-file-" + Guid.NewGuid().ToString("N") + ".srs");
        try
        {
            File.WriteAllBytes(path, RuleSetSamples.UpstreamGeositeTldRu());
            Assert.True(RuleSetFile.IsValidBinary(path));

            File.WriteAllBytes(path, RuleSetSamples.ZeroFilled(155));
            Assert.False(RuleSetFile.IsValidBinary(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
