using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class MainWindowViewModelConcurrencyAndDataLossTests
{
    [AvaloniaFact]
    public void OnEngineStatus_WhenConnecting_StoppedDoesNotResetIsConnecting()
    {
        var store = new InMemorySettingsStore();
        using var vm = new MainWindowViewModel(store);

        vm.IsConnecting = true;

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        onEngineStatus!.Invoke(vm, new object[] { "Stopped" });

        Assert.True(vm.IsConnecting);
    }

    [AvaloniaFact]
    public void SaveSettings_WhenDefaultProfilesMissing_PreservesCustomGroupApps()
    {
        var store = new InMemorySettingsStore();
        var initialSettings = new AppSettings();
        initialSettings.CustomGroupApps["Browsers"] = new List<string> { "special_browser.exe" };
        store.Save(initialSettings, AppPaths.ConfigYamlPath);

        using var vm = new MainWindowViewModel(store);

        vm.AppGroups.Clear();
        vm.AppGroups.Add(new AppGroupViewModel("Custom Apps", "", true) { IsCustomGroup = true });

        var saveSettings = typeof(MainWindowViewModel).GetMethod(
            "SaveSettings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(saveSettings);

        saveSettings!.Invoke(vm, null);

        var saved = store.Load(AppPaths.ConfigYamlPath);
        Assert.True(saved.CustomGroupApps.ContainsKey("Browsers"),
            "SaveSettings must not wipe CustomGroupApps when default groups are not loaded");
        Assert.Contains("special_browser.exe", saved.CustomGroupApps["Browsers"]);
    }

    private static string ReadAppFile(params string[] pathSegments)
    {
        var root = FindRepoRoot();
        var fullPath = Path.Combine(new[] { root, "VPNRouter.App" }.Concat(pathSegments).ToArray());
        return File.ReadAllText(fullPath);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not find repository root containing VPNRouter.sln");
    }
}
