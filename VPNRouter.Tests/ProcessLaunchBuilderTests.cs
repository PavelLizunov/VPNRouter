using System.Diagnostics;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ProcessLaunchBuilderTests
{
    [Theory]
    [InlineData("logs")]
    [InlineData("logs with spaces")]
    [InlineData("logs O'Brien & ^ % ! Unicode-Ж")]
    public void OpenDirectory_UsesPlatformOpenerAndExactDirectoryNotParent(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), name);
        var info = FileManagerHelper.BuildOpenDirectoryStartInfo(directory);
        Assert.Equal(OperatingSystem.IsWindows() ? "explorer.exe" :
            OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Empty(info.Arguments);
        Assert.Equal(new[] { directory }, info.ArgumentList);
    }

    [Theory]
    [InlineData(@"C:\Users\Test User\temp hosts.txt", @"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\O'Brien & ^ % !\hosts-Ж.txt", @"C:\hosts & ^ % O'Brien\hosts")]
    public void HostsHelpers_KeepLegalPathCharactersInSingleArgument(string tempPath, string hostsPath)
    {
        var (editor, reveal) = ZapretActions.BuildHostsEditStartInfos(tempPath, hostsPath);
        Assert.Equal("notepad.exe", editor.FileName);
        Assert.Equal("explorer.exe", reveal.FileName);
        Assert.False(editor.UseShellExecute);
        Assert.False(reveal.UseShellExecute);
        Assert.Empty(editor.Arguments);
        Assert.Empty(reveal.Arguments);
        Assert.Equal(new[] { tempPath }, editor.ArgumentList);
        Assert.Equal(new[] { $"/select,{hostsPath}" }, reveal.ArgumentList);
    }

    [Theory]
    [InlineData("bad\rpath", "hosts", "tempPath")]
    [InlineData("bad\npath", "hosts", "tempPath")]
    [InlineData("bad\"path", "hosts", "tempPath")]
    [InlineData("temp", "bad\rpath", "hostsPath")]
    [InlineData("temp", "bad\npath", "hostsPath")]
    [InlineData("temp", "bad\"path", "hostsPath")]
    [InlineData(null, "hosts", "tempPath")]
    [InlineData("temp", null, "hostsPath")]
    public void HostsHelpers_PreserveExistingValidation(string? tempPath, string? hostsPath, string parameter)
    {
        var error = Assert.ThrowsAny<ArgumentException>(() =>
            ZapretActions.BuildHostsEditStartInfos(tempPath!, hostsPath!));
        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(@"C:\Users\Test User\vpnr-self-repair.ps1")]
    [InlineData(@"C:\O'Brien & ^ % !\repair-Ж.ps1")]
    public void SelfRepair_UsesDirectHiddenFileLaunchWithOrderedArgumentTokens(string path)
    {
        var info = SelfRepair.BuildStartInfo(path);
        Assert.Equal("powershell.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Empty(info.Arguments);
        Assert.Equal(new[] { "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", path },
            info.ArgumentList);
    }

    [Fact]
    public void ProductionCallers_UseTheTestedBuilders()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var root = directory!.FullName;
        var settings = File.ReadAllText(Path.Combine(root, "VPNRouter.App", "ViewModels", "MainWindowViewModel.Settings.cs"));
        var openLogs = settings[settings.IndexOf("private void OpenLogs()", StringComparison.Ordinal)..];
        openLogs = openLogs[..openLogs.IndexOf("[ObservableProperty]", StringComparison.Ordinal)];
        Assert.Contains("Process.Start(FileManagerHelper.BuildOpenDirectoryStartInfo(logsDir))", openLogs);
        Assert.DoesNotContain("BuildRevealStartInfo", openLogs);
        var repair = File.ReadAllText(Path.Combine(root, "VPNRouter.App", "Services", "SelfRepair.cs"));
        Assert.Contains("var psi = BuildStartInfo(bootstrapPath);", repair);
        var hosts = File.ReadAllText(Path.Combine(root, "VPNRouter.Core", "Services", "ZapretActions.cs"));
        Assert.Contains("var (editor, reveal) = BuildHostsEditStartInfos(tempPath, hostsPath);", hosts);
    }
}
