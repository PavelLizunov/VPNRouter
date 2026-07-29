namespace VPNRouter.Tests;

public sealed class CliVersionSourceTests
{
    [Fact]
    public void Program_UsesCoreAppVersionAsApplicationVersion()
    {
        var source = File.ReadAllText(FindCliProgram());

        Assert.Matches(
            @"SetApplicationVersion\s*\(\s*VPNRouter\.Core\.AppVersion\.Version\s*\)",
            source);
        Assert.DoesNotMatch(@"SetApplicationVersion\s*\(\s*\x22", source);
    }

    private static string FindCliProgram()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir != null;
             dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "VPNRouter.CLI", "Program.cs");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("Could not locate VPNRouter.CLI/Program.cs.");
    }
}
