using System.Reflection;
using System.Runtime.InteropServices;

namespace VPNRouter.Core.Services;

internal sealed record AssistantSourceBuild(string Version, string Commit, string Platform, string Arch)
{
    internal string ReportIdentity => $"{Version} {Commit} {Platform} {Arch}";

    internal static AssistantSourceBuild? Installed()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var parts = info?.Split('+', 2);
        if (parts is not [_, var commit] || !ValidCommit(commit)) return null;

        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? "linux" : null;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return platform == null || arch == null ? null : new(AppVersion.Version, commit, platform, arch);
    }

    internal static bool ValidCommit(string commit) =>
        commit.Length == 40 && commit.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
