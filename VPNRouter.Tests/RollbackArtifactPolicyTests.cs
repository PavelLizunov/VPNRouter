#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using Xunit;

namespace VPNRouter.Tests;

public sealed class RollbackArtifactPolicyTests
{
    private const string CurrentVersion = "2.51.1-r1";
    private const string TargetVersion = "2.50.5";
    private const string Commit = "097393a79641848e84cb76aa324b8bc873da3ed6";

    private static UpdateSourceInfo Listed(string version = TargetVersion, bool prerelease = false) => new(
        Version: version,
        ReleaseUrl: $"https://github.com/PavelLizunov/VPNRouter/releases/tag/v{version}",
        AssetName: $"VPNRouter-v{version}-win.zip",
        DownloadUrl: $"https://github.com/PavelLizunov/VPNRouter/releases/download/v{version}/VPNRouter-v{version}-win.zip",
        AssetSize: 72_127_549,
        AssetSha256: new string('a', 64),
        IsPrerelease: prerelease,
        ReleaseNotes: string.Empty);

    private static RollbackArtifactVerdict Evaluate(
        UpdateSourceInfo? listed,
        string current = CurrentVersion,
        bool includePrereleases = false,
        bool isWindows = true,
        Architecture architecture = Architecture.X64,
        bool isStandardBuild = true,
        string[]? revoked = null) =>
        RollbackArtifactPolicy.CreateSignedRecoveryArtifact(
            current,
            includePrereleases,
            listed,
            revoked,
            isWindows,
            architecture,
            isStandardBuild);

    [Fact]
    public void EmbeddedRecoveryManifest_VerifiesWithPublisherKey()
    {
        var verification = SignedRecoveryManifestTrust.Verification;

        Assert.True(verification.IsValid, verification.ReasonCode);
        Assert.NotNull(verification.Manifest);
        Assert.Equal(TargetVersion, verification.Manifest!.Version);
        Assert.Equal("stable", verification.Manifest.Channel);
        Assert.Equal("windows", verification.Manifest.Platform);
        Assert.Equal("x64", verification.Manifest.Arch);
        Assert.Equal("standard", verification.Manifest.Edition);
        Assert.Equal($"VPNRouter-update-v{TargetVersion}-win.zip", verification.Manifest.Asset);
        Assert.Equal(72_127_549, verification.Manifest.Size);
        Assert.Equal("d9053ea06a8abc0ef7bee09b52f83d035b662e3e838818a254f7077aae6068c2",
            verification.Manifest.Sha256);
        Assert.Equal("097393a79641848e84cb76aa324b8bc873da3ed6", verification.Manifest.Commit);
    }

    [Fact]
    public void SignedRecoveryArtifact_AcceptedOnlyFromOfficialHistory()
    {
        var verdict = Evaluate(Listed());

        Assert.True(verdict.Eligible, verdict.DetailCode);
        Assert.Equal(RollbackArtifactProvenance.SignedRecoveryManifest, verdict.Provenance);
        Assert.NotNull(verdict.Artifact);
        Assert.Equal(TargetVersion, verdict.Artifact!.Version);
        Assert.Equal($"VPNRouter-update-v{TargetVersion}-win.zip", verdict.Artifact.AssetName);
        Assert.Equal(72_127_549, verdict.Artifact.AssetSize);
        Assert.Equal("d9053ea06a8abc0ef7bee09b52f83d035b662e3e838818a254f7077aae6068c2",
            verdict.Artifact.AssetSha256);
        Assert.True(verdict.Artifact.RequireExactSize);
        Assert.Equal("https://github.com/PavelLizunov/VPNRouter/releases/download/v2.50.5/" +
                     "VPNRouter-update-v2.50.5-win.zip", verdict.Artifact.DownloadUrl);
    }

    [Fact]
    public void MissingReleaseInOfficialHistory_IsRefused()
    {
        var verdict = Evaluate(listed: null);

        Assert.False(verdict.Eligible);
        Assert.Equal("not-in-history", verdict.ReasonCode);
    }

    [Fact]
    public void VersionMismatchOrPrerelease_IsRefused()
    {
        Assert.False(Evaluate(Listed("2.50.4")).Eligible);
        Assert.False(Evaluate(Listed(prerelease: true)).Eligible);
    }

    [Fact]
    public void CurrentOrOlderBuild_CannotRollForwardOrToItself()
    {
        Assert.False(Evaluate(Listed(), current: TargetVersion).Eligible);
        Assert.False(Evaluate(Listed(), current: "2.50.4").Eligible);
    }

    [Fact]
    public void HostArchitectureAndEdition_AreGated()
    {
        Assert.Equal("unsupported-host", Evaluate(Listed(), isWindows: false).ReasonCode);
        Assert.Equal("unsupported-host", Evaluate(Listed(), architecture: Architecture.Arm64).ReasonCode);
        Assert.Equal("unsupported-host", Evaluate(Listed(), isStandardBuild: false).ReasonCode);
    }

    [Fact]
    public void RevokedVersion_IsRefused()
    {
        Assert.Equal("revoked", Evaluate(Listed(), revoked: new[] { TargetVersion }).ReasonCode);
    }

    [Fact]
    public void OfficialReleasePage_RequiresExactTagPath()
    {
        Assert.True(RollbackArtifactPolicy.IsOfficialReleasePage(
            "https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.50.5", TargetVersion));
        Assert.False(RollbackArtifactPolicy.IsOfficialReleasePage(
            "https://example.com/PavelLizunov/VPNRouter/releases/tag/v2.50.5", TargetVersion));
        Assert.False(RollbackArtifactPolicy.IsOfficialReleasePage(
            "https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.50.4", TargetVersion));
    }

    [Theory]
    [InlineData("1.0.0+097393a79641848e84cb76aa324b8bc873da3ed6", true)]
    [InlineData("2.50.5+097393a79641848e84cb76aa324b8bc873da3ed6", true)]
    [InlineData("2.50.5+0000000000000000000000000000000000000000", false)]
    [InlineData("1.0.0+0000000000000000000000000000000000000000", false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0+097393a7", false)]
    [InlineData("1.0.0+097393a79641848e84cb76aa324b8bc873da3ed6+extra", false)]
    public void ProductCommit_IsTheVersionOracle(string productVersion, bool matches)
    {
        Assert.Equal(matches, RollbackArtifactPolicy.ProductCommitMatches(productVersion, Commit));
    }

    [Fact]
    public void StagedTree_RequiresTheCanonicalBootstrapAppPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpnrouter-stage-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "VPNRouter.App.exe"), "wrong path");
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.WriteAllText(Path.Combine(root, "app", "VPNRouter.App.exe"), "wrong path");
            File.WriteAllText(Path.Combine(root, "VPNRouter.GUI.exe"), "legacy launcher");
            Assert.Null(RollbackArtifactPolicy.CanonicalRecoveryAppPath(root));

            var bootstrap = Directory.CreateDirectory(Path.Combine(root, "_bootstrap"));
            var canonical = Path.Combine(bootstrap.FullName, "VPNRouter.App.exe");
            File.WriteAllText(canonical, "fixture");
            Assert.Equal(canonical, RollbackArtifactPolicy.CanonicalRecoveryAppPath(root));
            Assert.False(RollbackArtifactPolicy.StagedTreeMatchesManifest(
                root, SignedRecoveryManifestTrust.Manifest!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("{\"schema\":1}", false)]
    [InlineData("{\"schema\":1,\"version\":\"2.50.5\",\"channel\":\"stable\",\"platform\":\"windows\"," +
                "\"arch\":\"x64\",\"edition\":\"standard\",\"asset\":\"VPNRouter-update-v2.50.5-win.zip\"," +
                "\"size\":1,\"sha256\":\"zzzz\",\"commit\":\"zzzz\"}", false)]
    public void ManifestParser_RejectsMalformedMetadata(string json, bool expected)
    {
        var parsed = SignedRecoveryManifestTrust.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(expected, parsed != null);
    }
}
