#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Core.Services;

public sealed record SignedRecoveryManifest(
    int Schema,
    string Version,
    string Channel,
    string Platform,
    string Arch,
    string Edition,
    string Asset,
    long Size,
    string Sha256,
    string Commit)
{
    public string OfficialTagUrl =>
        $"https://github.com/PavelLizunov/VPNRouter/releases/tag/v{Version}";

    public string OfficialDownloadUrl =>
        $"https://github.com/PavelLizunov/VPNRouter/releases/download/v{Version}/{Asset}";
}

public sealed record SignedRecoveryManifestVerification(
    bool IsValid,
    string ReasonCode,
    SignedRecoveryManifest? Manifest)
{
    public static SignedRecoveryManifestVerification Invalid(string reason) => new(false, reason, null);
}

/// <summary>
/// The embedded, publisher-signed recovery manifest. The public key is the trust
/// root; the private key is not part of the repository. Verification happens on
/// the exact embedded bytes, including the manifest's trailing newline.
/// </summary>
public static class SignedRecoveryManifestTrust
{
    private const string ManifestResource = "VPNRouter.Recovery.windows-2.50.5.json";
    private const string SignatureResource = "VPNRouter.Recovery.windows-2.50.5.sig";
    private const string PublisherResource = "VPNRouter.Recovery.publisher.pem";

    private static readonly Lazy<SignedRecoveryManifestVerification> Verified = new(
        VerifyEmbedded,
        isThreadSafe: true);

    public static SignedRecoveryManifestVerification Verification => Verified.Value;

    public static SignedRecoveryManifest? Manifest => Verified.Value.Manifest;

    public static SignedRecoveryManifestVerification VerifyEmbedded()
    {
        try
        {
            var manifestBytes = ReadResource(ManifestResource);
            var signatureBytes = ReadResource(SignatureResource);
            var publisherPem = Encoding.ASCII.GetString(ReadResource(PublisherResource));

            using var key = ECDsa.Create();
            key.ImportFromPem(publisherPem);
            if (!key.VerifyData(
                    manifestBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                return SignedRecoveryManifestVerification.Invalid("signature");

            var manifest = Parse(manifestBytes);
            return manifest == null
                ? SignedRecoveryManifestVerification.Invalid("manifest")
                : new SignedRecoveryManifestVerification(true, "verified", manifest);
        }
        catch
        {
            return SignedRecoveryManifestVerification.Invalid("verification-error");
        }
    }

    internal static SignedRecoveryManifest? Parse(byte[] manifestBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            int schema = ReadInt(root, "schema");
            string version = ReadString(root, "version");
            string channel = ReadString(root, "channel");
            string platform = ReadString(root, "platform");
            string arch = ReadString(root, "arch");
            string edition = ReadString(root, "edition");
            string asset = ReadString(root, "asset");
            long size = ReadLong(root, "size");
            string sha256 = ReadString(root, "sha256");
            string commit = ReadString(root, "commit");

            if (schema != 1 ||
                !UpdateChecker.TryParseSemVer(version, out _) ||
                asset != $"VPNRouter-update-v{version}-win.zip" ||
                size <= 0 ||
                sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) ||
                commit.Length != 40 || !commit.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(channel) ||
                string.IsNullOrWhiteSpace(platform) ||
                string.IsNullOrWhiteSpace(arch) ||
                string.IsNullOrWhiteSpace(edition))
                return null;

            return new SignedRecoveryManifest(
                schema, version, channel, platform, arch, edition, asset, size, sha256, commit);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(SignedRecoveryManifestTrust).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing recovery manifest resource: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
