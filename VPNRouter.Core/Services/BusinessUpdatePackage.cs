using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Core.Services;

internal static class BusinessUpdatePackage
{
    internal const long MaxDownloadBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxEntries = 20_000;
    internal const string ReceiptName = "vpnrouter-business-update.json";
    internal static readonly string[] RequiredFiles =
    [
        "VPNRouter.App.exe", "VPNRouter.App.dll", "VPNRouter.Core.dll",
        "VPNRouter.App.deps.json", "VPNRouter.App.runtimeconfig.json",
        "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll",
        "sing-box.exe", "geo/geoip-ru.srs", "geo/geosite-ru.srs", ReceiptName
    ];

    internal static void ValidateDownload(string url, long size, string? sha, bool lite)
    {
        if (lite || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || size <= 0 || size > MaxDownloadBytes ||
            sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit))
            throw InvalidPackage();
    }

    internal static void ValidateArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is 0 or > MaxEntries) throw InvalidPackage();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!IsPayloadPath(name))
                throw InvalidPackage(name.Contains('\\') ? "archive_separator" : "archive_path");
            if (!names.Add(name.TrimEnd('/'))) throw InvalidPackage("archive_alias");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw InvalidPackage("archive_link");
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expanded)
                throw InvalidPackage();
            expanded += entry.Length;
        }
    }

    internal static string ValidateStaged(string directory, string expectedVersion)
    {
        if (!UpdateChecker.TryParseSemVer(expectedVersion, out _)) throw InvalidPackage();
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) || IsReparse(root)) throw InvalidPackage();
        var pending = new Stack<string>();
        pending.Push(root);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long expanded = 0;
        while (pending.TryPop(out var parent))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if (++count > MaxEntries || IsReparse(entry)) throw InvalidPackage();
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var isDirectory = Directory.Exists(entry);
                if (!IsPayloadPath(relative + (isDirectory ? "/" : "")) || !names.Add(relative))
                    throw InvalidPackage();
                if (isDirectory) pending.Push(entry);
                else
                {
                    var length = new FileInfo(entry).Length;
                    if (length > MaxExpandedBytes - expanded) throw InvalidPackage();
                    expanded += length;
                }
            }
        }
        var app = Path.Combine(root, "app");
        foreach (var required in RequiredFiles)
        {
            var file = Path.Combine(app, required.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw InvalidPackage();
        }
        ValidateReceipt(Path.Combine(app, ReceiptName), expectedVersion);
        ValidateBuildIdentity(app, expectedVersion);
        return app;
    }

    internal static void ValidateBuildIdentity(string appDirectory, string expectedVersion)
    {
        ValidateCore(Path.Combine(appDirectory, "VPNRouter.Core.dll"), expectedVersion);
        ValidateApp(Path.Combine(appDirectory, "VPNRouter.App.dll"));
    }

    private static bool IsPayloadPath(string path)
    {
        if (path.Contains('\\') || !(path == "app/" || path.StartsWith("app/", StringComparison.Ordinal)))
            return false;
        var segments = path.TrimEnd('/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Trim() != segment ||
                segment.EndsWith('.') || segment.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
                return false;
            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                return false;
        }
        var file = segments[^1];
        return !file.StartsWith("VPNRouter.GUI.", StringComparison.OrdinalIgnoreCase) &&
               !file.StartsWith("VPNRouter.CLI.", StringComparison.OrdinalIgnoreCase) &&
               !file.StartsWith("VPNRouter.Service.", StringComparison.OrdinalIgnoreCase) &&
               !file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) &&
               !file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) &&
               !file.EndsWith(".key", StringComparison.OrdinalIgnoreCase) &&
               !file.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) &&
               (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                (segments.Length == 3 && segments[1] == "profiles" &&
                 !file.Equals("config.json", StringComparison.OrdinalIgnoreCase)) ||
                file.Equals(ReceiptName, StringComparison.Ordinal));
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void ValidateReceipt(string path, string version)
    {
        if (new FileInfo(path).Length > 8192) throw InvalidPackage();
        using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw InvalidPackage();
        var properties = json.RootElement.EnumerateObject().ToArray();
        var expected = new HashSet<string>(["schema", "edition", "architecture", "version", "provisioning"], StringComparer.Ordinal);
        if (properties.Length != expected.Count || properties.Any(p => !expected.Remove(p.Name)) ||
            !json.RootElement.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 ||
            json.RootElement.GetProperty("edition").GetString() != "Business" ||
            json.RootElement.GetProperty("architecture").GetString() != "win-x64" ||
            json.RootElement.GetProperty("version").GetString() != version ||
            json.RootElement.GetProperty("provisioning").GetString() != "external")
            throw InvalidPackage();
    }

    private static void ValidateCore(string path, string version)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw InvalidPackage();
        var reader = pe.GetMetadataReader();
        if (reader.GetString(reader.GetAssemblyDefinition().Name) != "VPNRouter.Core") throw InvalidPackage();
        var edition = FindType(reader, "ProductEdition");
        var business = FindField(reader, edition, "IsBusiness");
        var businessField = reader.GetFieldDefinition(business);
        var constant = businessField.GetDefaultValue();
        if (constant.IsNil || (businessField.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) !=
            (FieldAttributes.Static | FieldAttributes.Literal) ||
            !reader.GetBlobBytes(businessField.Signature).AsSpan().SequenceEqual(new byte[] { 0x06, 0x02 }))
            throw InvalidPackage();
        var value = reader.GetConstant(constant);
        if (value.TypeCode != ConstantTypeCode.Boolean || !reader.GetBlobBytes(value.Value).AsSpan().SequenceEqual(new byte[] { 1 }))
            throw InvalidPackage();
        var appVersion = FindType(reader, "AppVersion");
        var field = FindField(reader, appVersion, "Version");
        var fieldDefinition = reader.GetFieldDefinition(field);
        if ((fieldDefinition.Attributes & FieldAttributes.Static) == 0 ||
            !reader.GetBlobBytes(fieldDefinition.Signature).AsSpan().SequenceEqual(new byte[] { 0x06, 0x0E })) throw InvalidPackage();
        var literal = fieldDefinition.GetDefaultValue();
        if (!literal.IsNil)
        {
            var item = reader.GetConstant(literal);
            if ((fieldDefinition.Attributes & FieldAttributes.Literal) == 0 || item.TypeCode != ConstantTypeCode.String ||
                Encoding.Unicode.GetString(reader.GetBlobBytes(item.Value)) != version) throw InvalidPackage();
            return;
        }
        if ((fieldDefinition.Attributes & FieldAttributes.InitOnly) == 0) throw InvalidPackage();
        var constructors = reader.GetTypeDefinition(appVersion).GetMethods()
            .Where(m => reader.GetString(reader.GetMethodDefinition(m).Name) == ".cctor").ToArray();
        if (constructors.Length != 1) throw InvalidPackage();
        var method = reader.GetMethodDefinition(constructors[0]);
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        var offset = 0;
        while (offset < il.Length && il[offset] == 0) offset++;
        if (offset + 5 > il.Length || il[offset++] != 0x72) throw InvalidPackage();
        var token = BitConverter.ToInt32(il, offset); offset += 4;
        if ((token & unchecked((int)0xFF000000)) != 0x70000000 ||
            reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF)) != version)
            throw InvalidPackage();
        while (offset < il.Length && il[offset] == 0) offset++;
        if (offset + 5 > il.Length || il[offset++] != 0x80 ||
            BitConverter.ToInt32(il, offset) != MetadataTokens.GetToken(field)) throw InvalidPackage();
        offset += 4;
        while (offset < il.Length && il[offset] == 0) offset++;
        if (offset + 1 != il.Length || il[offset] != 0x2A) throw InvalidPackage();
    }

    private static TypeDefinitionHandle FindType(MetadataReader reader, string name)
    {
        var matches = reader.TypeDefinitions.Where(h =>
        {
            var type = reader.GetTypeDefinition(h);
            return reader.GetString(type.Namespace) == "VPNRouter.Core" && reader.GetString(type.Name) == name;
        }).ToArray();
        return matches.Length == 1 ? matches[0] : throw InvalidPackage();
    }

    private static FieldDefinitionHandle FindField(MetadataReader reader, TypeDefinitionHandle type, string name)
    {
        var matches = reader.GetTypeDefinition(type).GetFields()
            .Where(h => reader.GetString(reader.GetFieldDefinition(h).Name) == name).ToArray();
        return matches.Length == 1 ? matches[0] : throw InvalidPackage();
    }

    private static void ValidateApp(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw InvalidPackage();
        var reader = pe.GetMetadataReader();
        if (reader.GetString(reader.GetAssemblyDefinition().Name) != "VPNRouter.App" ||
            reader.ManifestResources.Any(h => reader.GetString(reader.GetManifestResource(h).Name)
                .StartsWith("VPNRouter.Business.Connection", StringComparison.OrdinalIgnoreCase))) throw InvalidPackage();
        var markers = 0;
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" ||
                reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) throw InvalidPackage();
            var key = value.ReadSerializedString();
            var edition = value.ReadSerializedString();
            if (key != "VPNRouterEdition") continue;
            if (++markers != 1 || edition != "Business" || value.ReadUInt16() != 0 || value.RemainingBytes != 0)
                throw InvalidPackage();
        }
        if (markers != 1) throw InvalidPackage();
    }

    private static InvalidOperationException InvalidPackage(string? reason = null) =>
        new("The package is not a credential-free Windows Business update for the requested version." +
            (reason == null ? string.Empty : " Code=" + reason));
}
