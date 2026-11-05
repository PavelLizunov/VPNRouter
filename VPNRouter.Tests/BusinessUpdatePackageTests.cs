using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Collection(VPNRouter.Tests.Fakes.SafeModeStateCollection.Name)]
public sealed class BusinessUpdatePackageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingBusinessMetadataAndExternalReceiptAreAcceptedWithoutLoadingAssemblies(bool readonlyVersion)
    {
        using var fixture = new BusinessPackageFixture(readonlyVersion: readonlyVersion);
        Assert.Equal(fixture.App, BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
    }

    [Fact]
    public void ForgedBusinessReceiptCannotTurnActualStandardCoreIntoBusiness()
    {
        if (ProductEdition.IsBusiness) return;
        using var fixture = new BusinessPackageFixture(AppVersion.Version);
        File.Copy(typeof(ProductEdition).Assembly.Location, Path.Combine(fixture.App, "VPNRouter.Core.dll"), true);
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
    }

    [Fact]
    public void StandardAppCannotBeMixedWithBusinessCore()
    {
        using var fixture = new BusinessPackageFixture(AppVersion.Version);
        File.WriteAllBytes(Path.Combine(fixture.App, "VPNRouter.App.dll"), BusinessPackageFixture.ManagedImage(false, business: false));
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
        if (!ProductEdition.IsBusiness)
        {
            File.Copy(typeof(VPNRouter.App.Views.BusinessWindow).Assembly.Location,
                Path.Combine(fixture.App, "VPNRouter.App.dll"), true);
            Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
        }
    }

    [Theory]
    [InlineData(false, "2.50.6", false)]
    [InlineData(true, "2.50.7", false)]
    [InlineData(true, "2.50.6", true)]
    public void WrongEditionVersionOrEmbeddedCredentialsAreRejected(bool business, string coreVersion, bool credentials)
    {
        using var fixture = new BusinessPackageFixture();
        File.WriteAllBytes(Path.Combine(fixture.App, "VPNRouter.Core.dll"), BusinessPackageFixture.ManagedImage(true, business, coreVersion));
        File.WriteAllBytes(Path.Combine(fixture.App, "VPNRouter.App.dll"), BusinessPackageFixture.ManagedImage(false, credentials: credentials));
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
    }

    [Theory]
    [InlineData("VPNRouter.App.exe")]
    [InlineData("coreclr.dll")]
    [InlineData("geo/geoip-ru.srs")]
    [InlineData("geo/geosite-ru.srs")]
    public void MissingRuntimeOrGeographyRefusesThePackage(string relative)
    {
        using var fixture = new BusinessPackageFixture();
        File.Delete(Path.Combine(fixture.App, relative));
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
    }

    [Theory]
    [InlineData("edition", "Standard")]
    [InlineData("architecture", "win-arm64")]
    [InlineData("version", "2.50.7")]
    [InlineData("provisioning", "embedded")]
    public void ReceiptMustMatchEditionArchitectureVersionAndExternalProvisioning(string property, string value)
    {
        using var fixture = new BusinessPackageFixture();
        var receipt = new Dictionary<string, object>
        {
            ["schema"] = 1, ["edition"] = "Business", ["architecture"] = "win-x64",
            ["version"] = fixture.Version, ["provisioning"] = "external"
        };
        receipt[property] = value;
        File.WriteAllText(Path.Combine(fixture.App, BusinessUpdatePackage.ReceiptName), JsonSerializer.Serialize(receipt));
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateStaged(fixture.Root, fixture.Version));
    }

    [Theory]
    [InlineData("app/../outside.dll")]
    [InlineData("elsewhere/file.dll")]
    [InlineData("app\\outside.dll")]
    [InlineData("app/config.yaml")]
    [InlineData("app/business-provisioning.json")]
    [InlineData("app/VPNRouter.GUI.exe")]
    [InlineData("app/CON.dll")]
    [InlineData("app/file.dll:stream")]
    public void ArchiveCannotEscapeOrReplaceProvisioningAndOtherEditionEntryPoints(string path)
    {
        using var fixture = new BusinessPackageFixture();
        var zipPath = Path.Combine(fixture.Root, "bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry(path).Open())) writer.Write("fixture");
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateArchive(zipPath));
    }

    [Fact]
    public void CaseAliasesAndSymlinksAreRejectedBeforeExtraction()
    {
        using var fixture = new BusinessPackageFixture();
        var zipPath = Path.Combine(fixture.Root, "aliases.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("app/file.dll");
            zip.CreateEntry("app/FILE.dll");
        }
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateArchive(zipPath));
        File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var link = zip.CreateEntry("app/linked.dll");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
        }
        Assert.Throws<InvalidOperationException>(() => BusinessUpdatePackage.ValidateArchive(zipPath));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task UnknownLengthResponseMustStillMatchExactManifestSize(long declaredSize)
    {
        using var fixture = new BusinessPackageFixture();
        var previous = AppPaths.DataDir;
        AppPaths.OverrideDataDir(Path.Combine(fixture.Root, "data"));
        try
        {
            byte[] bytes = [1, 2, 3];
            var checker = new UpdateChecker(new UpdateSettings(), "2.50.5", new UnboundedFixtureHttp(bytes));
            var info = new UpdateInfo
            {
                LatestVersion = fixture.Version, DownloadUrl = "https://updates.example.invalid/shared.zip",
                SizeBytes = declaredSize, FullChecksumSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => checker.DownloadAndStageAsync(info, true, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(AppPaths.DataDir, "update-staging")));
        }
        finally { AppPaths.OverrideDataDir(previous); }
    }

    private sealed class UnboundedFixtureHttp(byte[] bytes) : IHttpClient
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default) =>
            Task.FromResult<IHttpStreamingResponse>(new FixtureResponse(bytes));
        private sealed class FixtureResponse(byte[] body) : IHttpStreamingResponse
        {
            public int StatusCode => 200;
            public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
            public long? ContentLength => null;
            public Stream Body { get; } = new MemoryStream(body);
            public ValueTask DisposeAsync() => Body.DisposeAsync();
        }
    }
}

internal sealed class BusinessPackageFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "business-package-" + Guid.NewGuid().ToString("N"));
    internal string App => Path.Combine(Root, "app");
    internal string Version { get; }
    internal BusinessPackageFixture(string version = "2.50.6", bool readonlyVersion = false)
    {
        Version = version;
        string[] files = ["VPNRouter.App.exe", "VPNRouter.App.dll", "VPNRouter.Core.dll", "VPNRouter.App.deps.json",
            "VPNRouter.App.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll",
            "sing-box.exe", "geo/geoip-ru.srs", "geo/geosite-ru.srs"];
        foreach (var name in files)
        {
            var path = Path.Combine(App, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "harmless fixture; never executed");
        }
        File.WriteAllText(Path.Combine(App, BusinessUpdatePackage.ReceiptName), JsonSerializer.Serialize(new
        { schema = 1, edition = "Business", architecture = "win-x64", version, provisioning = "external" }));
        File.WriteAllBytes(Path.Combine(App, "VPNRouter.Core.dll"), ManagedImage(true, true, version, readonlyVersion: readonlyVersion));
        File.WriteAllBytes(Path.Combine(App, "VPNRouter.App.dll"), ManagedImage(false));
    }

    internal static byte[] ManagedImage(bool core, bool business = true, string version = "2.50.6",
        bool credentials = false, bool readonlyVersion = false)
    {
        var metadata = new MetadataBuilder();
        var name = core ? "VPNRouter.Core" : "VPNRouter.App";
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var assembly = metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, (AssemblyHashAlgorithm)0);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var il = new BlobBuilder();
        if (core)
        {
            metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                metadata.GetOrAddString("VPNRouter.Core"), metadata.GetOrAddString("ProductEdition"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var edition = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                metadata.GetOrAddString("IsBusiness"), metadata.GetOrAddBlob(new byte[] { 0x06, 0x02 }));
            metadata.AddConstant(edition, business);
            metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                metadata.GetOrAddString("VPNRouter.Core"), metadata.GetOrAddString("AppVersion"), default,
                MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(1));
            var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static |
                (readonlyVersion ? FieldAttributes.InitOnly : FieldAttributes.Literal | FieldAttributes.HasDefault),
                metadata.GetOrAddString("Version"), metadata.GetOrAddBlob(new byte[] { 0x06, 0x0E }));
            if (readonlyVersion)
            {
                var code = new BlobBuilder();
                var instructions = new InstructionEncoder(code);
                instructions.LoadString(metadata.GetOrAddUserString(version));
                instructions.OpCode(ILOpCode.Stsfld);
                instructions.Token(field);
                instructions.OpCode(ILOpCode.Ret);
                var offset = new MethodBodyStreamEncoder(il).AddMethodBody(instructions, maxStack: 1);
                metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL | MethodImplAttributes.Managed, metadata.GetOrAddString(".cctor"),
                    metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x01 }), offset, MetadataTokens.ParameterHandle(1));
            }
            else metadata.AddConstant(field, version);
        }
        if (!core)
        {
            var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0),
                default, default, (AssemblyFlags)0, default);
            var marker = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Reflection"),
                metadata.GetOrAddString("AssemblyMetadataAttribute"));
            var constructor = metadata.AddMemberReference(marker, metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(new byte[] { 0x20, 0x02, 0x01, 0x0E, 0x0E }));
            var value = new BlobBuilder();
            value.WriteUInt16(1);
            value.WriteSerializedString("VPNRouterEdition");
            value.WriteSerializedString(business ? "Business" : "Standard");
            value.WriteUInt16(0);
            metadata.AddCustomAttribute(assembly, constructor, metadata.GetOrAddBlob(value));
        }
        var resources = new BlobBuilder();
        if (credentials)
        {
            resources.WriteInt32(0);
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString("VPNRouter.Business.Connection"), default, 0);
        }
        var builder = new ManagedPEBuilder(new PEHeaderBuilder(Machine.Amd64, imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), il, managedResources: resources);
        var image = new BlobBuilder();
        builder.Serialize(image);
        return image.ToArray();
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
