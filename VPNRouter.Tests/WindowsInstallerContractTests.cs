#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Text contract of the Windows installer (packaging/windows/vpnrouter.iss, tools/build-installer.ps1,
/// packaging/windows/installer/stop-vpnrouter.ps1). The installer itself is compiled with Inno Setup on a Windows worker;
/// these checks keep the promises of the owner-approved proposal from drifting: machine-wide install, every system-level
/// task off by default, the zip layout and the in-app updater left working, cleanup on uninstall, data kept by default.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class WindowsInstallerContractTests
{
    private static string Root { get; } = FindRepoRoot();

    private static string Iss => File.ReadAllText(Path.Combine(Root, "packaging", "windows", "vpnrouter.iss"));

    private static string StopScript => File.ReadAllText(Path.Combine(Root, "packaging", "windows", "installer", "stop-vpnrouter.ps1"));

    private static string BuildScript => File.ReadAllText(Path.Combine(Root, "tools", "build-installer.ps1"));

    private static string Section(string name)
    {
        var text = Iss;
        var start = text.IndexOf($"[{name}]", StringComparison.Ordinal);
        Assert.True(start >= 0, $"[{name}] section is missing");
        var next = text.IndexOf("\n[", start + 1, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    [Fact]
    public void Setup_IsMachineWideWithAFixedAppId()
    {
        var setup = Section("Setup");
        Assert.Contains("AppId={{BAAAEA6C-F1D8-4D34-B00D-91EC746D8527}", setup);
        Assert.Contains("PrivilegesRequired=admin", setup);
        Assert.Contains(@"DefaultDirName={autopf}\VPNRouter", setup);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", setup);
        Assert.Contains("OutputBaseFilename=VPNRouter-Setup-v{#AppVersion}", setup);
    }

    [Fact]
    public void Tasks_EverythingThatChangesTheSystemIsOffByDefault()
    {
        var tasks = Section("Tasks").Split('\n').Where(l => l.TrimStart().StartsWith("Name:", StringComparison.Ordinal)).ToList();
        foreach (var name in new[] { "desktopicon", "autostart", "service", "defender" })
        {
            var line = Assert.Single(tasks, l => l.Contains($"Name: \"{name}\"", StringComparison.Ordinal));
            Assert.Contains("Flags: unchecked", line);
        }
    }

    [Fact]
    public void Payload_KeepsTheZipLayoutAndTheShortcutTargetTheAppSelfHealsTo()
    {
        Assert.Contains("Source: \"{#PayloadDir}\\*\"; DestDir: \"{app}\"; Flags: recursesubdirs createallsubdirs ignoreversion", Section("Files"));
        var icons = Section("Icons");
        Assert.Contains("Name: \"{commonprograms}\\VPNRouter\"; Filename: \"{app}\\app\\VPNRouter.GUI.exe\"", icons);
        Assert.Contains("WorkingDir: \"{app}\\app\"", icons);
        // A staging or scratch install must not overwrite the shared Start Menu shortcut of a real install.
        Assert.Matches(new Regex(@"\{commonprograms\}\\VPNRouter[^\r\n]*Check: SystemChangesAllowed"), icons);
    }

    [Fact]
    public void Uninstall_RemovesTheUpdaterArtifactsButNeverTheData()
    {
        var delete = Section("UninstallDelete");
        foreach (var entry in new[] { @"{app}\app", @"{app}\app.bak", @"{app}\app.bak.tmp", @"{app}\app.bak.id", @"{app}\.update-backup.lock" })
            Assert.Contains($"Name: \"{entry}\"", delete);
        Assert.DoesNotContain("{commonappdata}", delete);

        var code = Section("Code");
        Assert.Contains("SwitchPresent('DELETEDATA')", code);
        Assert.Contains("MB_DEFBUTTON2", code);
    }

    [Fact]
    public void Uninstall_RunsTheCleanupCommandUnlessSkipped()
    {
        var code = Section("Code");
        Assert.Contains("'cleanup '", code);
        Assert.Contains("SwitchPresent('NOCLEANUP')", code);
        Assert.Contains("CLEANUPARGS", code);
    }

    [Fact]
    public void SystemLevelSteps_AreSkippedWhenNoSystemChangesIsGiven()
    {
        var code = Section("Code");
        Assert.Contains("not SwitchPresent('NOSYSTEMCHANGES')", code);
        Assert.Matches(new Regex(@"ssPostInstall\)\s+and\s+SystemChangesAllowed.*?MigrateOldScriptInstall;.*?RestrictDataDirectoryAcl;", RegexOptions.Singleline), code);
        Assert.Contains("if SystemChangesAllowed then", code);
        Assert.Contains("-DataDir", code);
    }

    [Fact]
    public void StopScript_NeverStopsAnythingByNameAlone()
    {
        var script = StopScript;
        Assert.Contains("ExecutablePath", script);
        Assert.DoesNotMatch(new Regex(@"taskkill|Get-Process\s+-Name|Stop-Process\s+-Name", RegexOptions.IgnoreCase), script);
        Assert.Contains("unins", script);
        Assert.Contains("exit 10", script);
    }

    [Fact]
    public void Languages_EnglishAndRussianHaveTheSameCustomMessages()
    {
        var iss = Iss;
        Assert.Contains("Name: \"english\"", iss);
        Assert.Contains("Name: \"russian\"", iss);
        var english = Regex.Matches(iss, @"^english\.(\w+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).OrderBy(s => s).ToList();
        var russian = Regex.Matches(iss, @"^russian\.(\w+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).OrderBy(s => s).ToList();
        Assert.NotEmpty(english);
        Assert.Equal(english, russian);
    }

    [Fact]
    public void BuildScript_TakesTheSameVersionShapeAsInstallPs1AndUploadsNothing()
    {
        var script = BuildScript;
        Assert.Contains(@"[ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$')]", script);
        Assert.DoesNotMatch(new Regex(@"gh\s+release|Invoke-RestMethod|Invoke-WebRequest|\-Upload", RegexOptions.IgnoreCase), script);
        Assert.Contains("VPNRouter-Setup-v$Version.exe", script);
        Assert.Contains(".sha256", script);
    }

    [Fact]
    public void GitIgnore_ReIncludesTheBuildScript()
    {
        var ignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        Assert.Contains("!tools/build-installer.ps1", ignore);
    }

    [Fact]
    public void ReleaseTooling_DoesNotShipTheInstallerYet()
    {
        // Release wiring (two more release files) is a separate, owner-commanded step; until then the asset contract is unchanged.
        var buildPs1 = File.ReadAllText(Path.Combine(Root, "build.ps1"));
        Assert.DoesNotContain("VPNRouter-Setup", buildPs1);
        foreach (var workflow in Directory.EnumerateFiles(Path.Combine(Root, ".github", "workflows"), "*.y*ml"))
            Assert.DoesNotContain("VPNRouter-Setup", File.ReadAllText(workflow));
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
