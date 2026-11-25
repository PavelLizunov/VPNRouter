using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace VPNRouter.Tests;

[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class RuntimeConfigMemoryTests
{
    [Theory]
    [InlineData("VPNRouter.App/VPNRouter.App.csproj")]
    [InlineData("VPNRouter.Headless/VPNRouter.Headless.csproj")]
    [InlineData("VPNRouter.Service/VPNRouter.Service.csproj")]
    public void HostProjects_ConfigureWorkstationGcMemoryConservation(string relativeCsprojPath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativeCsprojPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Expected project file at {fullPath}");

        var doc = XDocument.Load(fullPath);

        var options = doc.Descendants()
            .Where(e => e.Name.LocalName == "RuntimeHostConfigurationOption")
            .ToDictionary(
                e => e.Attribute("Include")?.Value ?? string.Empty,
                e => e.Attribute("Value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.True(options.TryGetValue("System.GC.ConserveMemory", out var conserveMemory),
            $"{relativeCsprojPath} must declare RuntimeHostConfigurationOption System.GC.ConserveMemory");
        Assert.Equal("9", conserveMemory);

        Assert.True(options.TryGetValue("System.GC.RetainVM", out var retainVm),
            $"{relativeCsprojPath} must declare RuntimeHostConfigurationOption System.GC.RetainVM");
        Assert.Equal("false", retainVm, ignoreCase: true);

        var serverGcProps = doc.Descendants()
            .Where(e => e.Name.LocalName == "ServerGarbageCollection")
            .Select(e => e.Value.Trim())
            .ToList();
        Assert.All(serverGcProps, value =>
            Assert.False(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                $"{relativeCsprojPath} must keep Workstation GC default (ServerGarbageCollection must not be true)"));

        if (options.TryGetValue("System.GC.Server", out var gcServerOption))
        {
            Assert.False(string.Equals(gcServerOption, "true", StringComparison.OrdinalIgnoreCase),
                $"{relativeCsprojPath} must not enable System.GC.Server");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
