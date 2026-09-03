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
        // FIND-02: pre-start cleanup emits "Stopped" status asynchronously.
        // If the UI is already in the process of connecting (IsConnecting == true),
        // "Stopped" must NOT flip IsConnecting back to false, preventing duplicate connect clicks.
        var store = new InMemorySettingsStore();
        using var vm = new MainWindowViewModel(store);

        vm.IsConnecting = true;

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        onEngineStatus!.Invoke(vm, new object[] { "Stopped" });

        // IsConnecting must remain true because the connection attempt is still in flight!
        Assert.True(vm.IsConnecting);
    }

    [AvaloniaFact]
    public void SaveSettings_WhenDefaultProfilesMissing_PreservesCustomGroupApps()
    {
        // F-02: if default.json failed to deserialize or default groups are missing,
        // SaveSettings() must not wipe existing CustomGroupApps on disk.
        var store = new InMemorySettingsStore();
        var initialSettings = new AppSettings();
        initialSettings.CustomGroupApps["Browsers"] = new List<string> { "special_browser.exe" };
        store.Save(initialSettings, AppPaths.ConfigYamlPath);

        using var vm = new MainWindowViewModel(store);

        // Simulate state where default groups were not loaded into AppGroups
        vm.AppGroups.Clear();
        vm.AppGroups.Add(new AppGroupViewModel("Custom Apps", "", true) { IsCustomGroup = true });

        // Invoke SaveSettings
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

    [Fact]
    public void SimpleMode_SmpToggleConnectAsync_GuardsIsConnectingAcrossProbe()
    {
        // FIND-01: verify source contract that SmpToggleConnectAsync sets IsConnecting = true
        // before running the 4s candidate health probe and wraps the bring-up in try/catch.
        var source = ReadAppFile("ViewModels", "MainWindowViewModel.SimpleMode.cs");

        var methodIdx = source.IndexOf("private async Task SmpToggleConnectAsync()", StringComparison.Ordinal);
        var nextMethodIdx = source.IndexOf("private bool TryApplyVless", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "SmpToggleConnectAsync method must exist");
        Assert.True(nextMethodIdx > methodIdx, "Next method boundary must exist");

        var body = source[methodIdx..nextMethodIdx];

        var isConnectingCheck = body.IndexOf("if (IsConnecting) return;", StringComparison.Ordinal);
        var isConnectingSet = body.IndexOf("IsConnecting = true;", StringComparison.Ordinal);
        var probeAll = body.IndexOf(".ProbeAllAsync(", StringComparison.Ordinal);
        var resetBeforeToggle = body.IndexOf("IsConnecting = false;\n            await ToggleConnectionAsync();", StringComparison.Ordinal);
        if (resetBeforeToggle < 0)
            resetBeforeToggle = body.IndexOf("IsConnecting = false;\r\n            await ToggleConnectionAsync();", StringComparison.Ordinal);

        Assert.True(isConnectingCheck >= 0, "Entry guard must check IsConnecting");
        Assert.True(isConnectingSet >= 0, "Must set IsConnecting = true");
        Assert.True(probeAll > isConnectingSet, "Must set IsConnecting = true BEFORE probing candidates");
        Assert.True(resetBeforeToggle > probeAll, "Must reset IsConnecting = false before handoff to ToggleConnectionAsync");
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
