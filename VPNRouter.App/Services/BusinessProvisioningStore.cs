using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VPNRouter.Core;

namespace VPNRouter.App.Services;

internal static class BusinessProvisioningStore
{
    internal const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string PathFor(string dataDirectory) =>
        Path.Combine(dataDirectory, "provisioning", "business.json");

    internal static string Resolve(string? embeddedBundle, string dataDirectory)
    {
        try
        {
            var path = PathFor(dataDirectory);
            if (embeddedBundle != null)
            {
                ValidateBundle(embeddedBundle);
                Persist(path, embeddedBundle);
                return embeddedBundle;
            }

            RefuseLinks(path);
            string saved;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                saved = ReadBounded(stream);
            ValidateBundle(saved);
            BusinessPrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
            RefuseLinks(path);
            ProtectFile(path);
            return saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or JsonException or ArgumentException or
            InvalidOperationException or FormatException or KeyNotFoundException or NotSupportedException)
        {
            throw InvalidProvisioning();
        }
    }

    internal static string ReadBounded(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaximumBytes) throw InvalidProvisioning();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaximumBytes - buffer.Length + 1))) > 0)
        {
            if (buffer.Length + read > MaximumBytes) throw InvalidProvisioning();
            buffer.Write(chunk, 0, read);
        }
        try { return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw InvalidProvisioning(); }
    }

    internal static void ValidateBundle(string bundle)
    {
        try
        {
            if (bundle.Length > MaximumBytes || StrictUtf8.GetByteCount(bundle) > MaximumBytes)
                throw InvalidProvisioning();
            if (bundle.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(bundle, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                RequireUnique(root);
                if (root.TryGetProperty("business", out var services))
                {
                    RequireUnique(services, optionalEndpoints: true);
                    if (services.TryGetProperty("support", out var support)) RequireUnique(support);
                }
            }
            BusinessClientConfiguration.ValidateResponse(bundle);
            _ = BusinessClientConfiguration.Parse(bundle);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or
            FormatException or KeyNotFoundException)
        {
            throw InvalidProvisioning();
        }
    }

    private static void RequireUnique(JsonElement value, bool optionalEndpoints = false)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidProvisioning();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) && !(optionalEndpoints && property.Name is "nodes_url" or "updates_url"))
                throw InvalidProvisioning();
    }

    private static void Persist(string path, string bundle)
    {
        RefuseLinks(path);
        var directory = Path.GetDirectoryName(path)!;
        BusinessPrivateFiles.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = AppPaths.CreatePrivateFile(temporary, FileMode.CreateNew))
            {
                var bytes = StrictUtf8.GetBytes(bundle);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ProtectFile(temporary);
            RefuseLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void RefuseLinks(string path)
    {
        BusinessPrivateFiles.RefuseLinks(path);
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            if ((Path.GetDirectoryName(item) == null ? (FileSystemInfo)new DirectoryInfo(item) : new FileInfo(item)).LinkTarget != null)
                throw InvalidProvisioning();
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in new[] { identity.User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static InvalidDataException InvalidProvisioning() => new("Business provisioning is unavailable or invalid.");
}
