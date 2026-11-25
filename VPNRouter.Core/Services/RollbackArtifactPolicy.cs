#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Services;

public enum RollbackArtifactProvenance
{
    None = 0,
    SignedRecoveryManifest,
}

public sealed record RollbackArtifactVerdict(
    bool Eligible,
    string ReasonCode,
    string DetailCode,
    RollbackArtifactProvenance Provenance,
    UpdateSourceInfo? Artifact)
{
    public static RollbackArtifactVerdict Refuse(string reason, string detail) =>
        new(false, reason, detail, RollbackArtifactProvenance.None, null);
}

/// <summary>
/// Client-side rollback artifact policy. The automatic path accepts only the
/// embedded publisher-signed recovery manifest, because the published desktop
/// binaries have no Authenticode publisher identity. The SHA-256 sidecar and
/// official release URL are checked again by the existing updater during staging
/// and publication.
/// </summary>
public static class RollbackArtifactPolicy
{
    // The first release that understood the current settings schema. An older
    // client must not be asked to migrate or rewrite a newer config.
    public const string MinimumSettingsSchemaVersion = "2.46.0";

    public static bool IsStandardBuild
    {
        get
        {
#if VPNROUTER_BUSINESS
            return false;
#else
            return true;
#endif
        }
    }

    public static bool IsSupportedHost(
        bool? isWindows = null,
        Architecture? architecture = null,
        bool? isStandardBuild = null)
    {
        isWindows ??= OperatingSystem.IsWindows();
        architecture ??= RuntimeInformation.ProcessArchitecture;
        isStandardBuild ??= IsStandardBuild;
        return isWindows.Value &&
               architecture.Value == Architecture.X64 &&
               isStandardBuild.Value;
    }

    public static RollbackArtifactVerdict CreateSignedRecoveryArtifact(
        string currentVersion,
        bool includePrereleases,
        UpdateSourceInfo? listedRelease,
        IReadOnlyCollection<string>? revokedVersions = null,
        bool? isWindows = null,
        Architecture? architecture = null,
        bool? isStandardBuild = null)
    {
        if (!IsSupportedHost(isWindows, architecture, isStandardBuild))
            return RollbackArtifactVerdict.Refuse("unsupported-host", "not-windows-standard-x64");

        var verification = SignedRecoveryManifestTrust.Verification;
        if (!verification.IsValid || verification.Manifest is not { } manifest)
            return RollbackArtifactVerdict.Refuse("signed-manifest-invalid", verification.ReasonCode);

        if (!string.Equals(manifest.Platform, "windows", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Arch, "x64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Edition, "standard", StringComparison.OrdinalIgnoreCase))
            return RollbackArtifactVerdict.Refuse("manifest-platform", "manifest-not-windows-standard-x64");

        if (!string.Equals(manifest.Channel, "stable", StringComparison.OrdinalIgnoreCase) &&
            !includePrereleases)
            return RollbackArtifactVerdict.Refuse("channel", "candidate-on-stable-channel");

        if (!UpdateChecker.TryParseSemVer(manifest.Version, out var target) ||
            !UpdateChecker.TryParseSemVer(currentVersion, out var current) ||
            target.CompareTo(current) >= 0)
            return RollbackArtifactVerdict.Refuse("target-not-older", "manifest-not-older-than-running-build");

        if (!UpdateChecker.TryParseSemVer(MinimumSettingsSchemaVersion, out var floor) ||
            target.CompareTo(floor) < 0)
            return RollbackArtifactVerdict.Refuse("security-floor", "target-below-settings-schema-floor");

        if (revokedVersions != null && revokedVersions.Contains(manifest.Version, StringComparer.OrdinalIgnoreCase))
            return RollbackArtifactVerdict.Refuse("revoked", "target-revoked");

        if (listedRelease == null ||
            !string.Equals(listedRelease.Version, manifest.Version, StringComparison.Ordinal) ||
            listedRelease.IsPrerelease ||
            !IsOfficialReleasePage(listedRelease.ReleaseUrl, manifest.Version))
            return RollbackArtifactVerdict.Refuse("not-in-history", "release-not-present-in-official-history");

        if (manifest.Size <= 0 ||
            manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
            return RollbackArtifactVerdict.Refuse("package-metadata", "invalid-size-or-digest");

        var artifact = new UpdateSourceInfo(
            Version: manifest.Version,
            ReleaseUrl: manifest.OfficialTagUrl,
            AssetName: manifest.Asset,
            DownloadUrl: manifest.OfficialDownloadUrl,
            AssetSize: manifest.Size,
            AssetSha256: manifest.Sha256,
            IsPrerelease: false,
            ReleaseNotes: listedRelease.ReleaseNotes,
            RequireExactSize: true);

        if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            !UpdateChecker.IsOfficialWindowsRelease(downloadUri, manifest.Version))
            return RollbackArtifactVerdict.Refuse("package-url", "download-url-not-official");

        return new RollbackArtifactVerdict(
            true,
            "eligible",
            "signed-recovery-manifest",
            RollbackArtifactProvenance.SignedRecoveryManifest,
            artifact);
    }

    public static bool StagedTreeMatchesManifest(
        string stagedDirectory,
        SignedRecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var app = CanonicalRecoveryAppPath(stagedDirectory);
        if (app == null)
            return false;

        try
        {
            var product = FileVersionInfo.GetVersionInfo(app).ProductVersion ?? string.Empty;
            return ProductCommitMatches(product, manifest.Commit);
        }
        catch
        {
            return false;
        }
    }

    internal static string? CanonicalRecoveryAppPath(string stagedDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagedDirectory))
            return null;
        // 2.50.5 update packages are published with the legacy _bootstrap
        // layout. The managed App.exe is the version oracle; the GUI launcher
        // has no PE VERSIONINFO.
        var path = Path.Combine(stagedDirectory, "_bootstrap", "VPNRouter.App.exe");
        return File.Exists(path) ? path : null;
    }

    internal static bool ProductCommitMatches(string productVersion, string commit)
    {
        if (string.IsNullOrWhiteSpace(productVersion) ||
            string.IsNullOrWhiteSpace(commit) ||
            commit.Length != 40 ||
            !commit.All(Uri.IsHexDigit))
            return false;

        var value = productVersion.Trim();
        var plus = value.IndexOf('+');
        if (plus <= 0 || plus != value.LastIndexOf('+'))
            return false;
        return value[(plus + 1)..].Equals(commit, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOfficialReleasePage(string releaseUrl, string version)
    {
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = $"/PavelLizunov/VPNRouter/releases/tag/v{version}";
        return uri.AbsolutePath.TrimEnd('/').Equals(expected, StringComparison.OrdinalIgnoreCase) &&
               uri.Query.Length == 0 &&
               uri.Fragment.Length == 0;
    }
}
