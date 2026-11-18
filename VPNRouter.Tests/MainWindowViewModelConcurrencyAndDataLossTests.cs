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

    [AvaloniaFact]
    public void InvalidCustomEditsAndNativeImportPreserveSavedProtection()
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        settings.App.CustomRules = [new() { Action = "block", Type = "geosite", Value = "keep" }];
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        void AssertProtected()
        {
            var saved = Assert.Single(store.Load(AppPaths.ConfigYamlPath).App.CustomRules);
            Assert.Equal(("block", "geosite", "keep"), (saved.Action, saved.Type, saved.Value));
        }

        vm.EditedCustomRulesText = "direct domain new.example\nblock geosite bad.name";
        Assert.True(vm.RulesEditorHasErrors);
        vm.ApplyEditedRulesCommand.Execute(null);
        AssertProtected();
        vm.CustomRulesText = vm.EditedCustomRulesText;
        AssertProtected();

        const string native = """[{"outbound":"direct","port":80},{"action":"sniff","network":"tcp"}]""";
        Assert.False(vm.AppendImportedCustomRules(native));
        Assert.Single(vm.CustomRulesList);
        AssertProtected();

        vm.CustomRulesList[0].Value = "bad.name";
        Assert.False(string.IsNullOrEmpty(vm.CustomRulesErrorText));
        AssertProtected();
        vm.CustomRulesList[0].Value = "NEWS";
        var updated = Assert.Single(store.Load(AppPaths.ConfigYamlPath).App.CustomRules);
        Assert.Equal(("block", "geosite", "news"), (updated.Action, updated.Type, updated.Value));
    }

    [AvaloniaTheory]
    [InlineData("Subscription")]
    [InlineData("ManualVless")]
    [InlineData("Connect")]
    public async Task ConnectAndReconnect_PreserveOwnedProcessWhenClashIsUnavailable(string intent)
    {
        var store = new InMemorySettingsStore();
        using var vm = new MainWindowViewModel(store);
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var engine = (VPNRouter.Core.Services.VpnEngine)typeof(MainWindowViewModel).GetField("_engine", fields)!.GetValue(vm)!;
        var managerField = engine.GetType().GetField("_singBox", fields)!;
        var runner = new FakeProcessRunner();
        using var manager = new VPNRouter.Core.Services.SingBoxManager(new SingBoxSettings(), null,
            new FakeHttpClient().Setup("/configs", "", statusCode: 503), runner);
        using var handle = new FakeProcessHandle(142);
        var handleField = manager.GetType().GetField("_handle", fields)!;
        handleField.SetValue(manager, handle);
        managerField.SetValue(engine, manager);
        try
        {
            // Seed desktop status without starting the live connection statistics timer.
            typeof(MainWindowViewModel).GetField("_isConnected", fields)!.SetValue(vm, intent != "Connect");
            Assert.False(engine.IsRunning);
            Assert.True(engine.HasActiveTunnel);
            Assert.False(engine.TryPrepareColdReconnect());
            var managed = typeof(MainWindowViewModel).GetProperty("IsServiceManagedVpn", fields)!;
            Assert.Equal(false, managed.GetValue(vm));
            if (intent == "Connect")
            {
                await vm.ToggleConnectionCommand.ExecuteAsync(null);
            }
            else
            {
                var reconnect = typeof(MainWindowViewModel).GetMethod("ReconnectAsync", fields)!;
                var reason = Enum.Parse(reconnect.GetParameters()[1].ParameterType, intent);
                // No session token: ApplyAsync rejects the candidate without contacting a real service.
                await (Task)reconnect.Invoke(vm, new object[] { "sample", reason })!;
            }
            Assert.False(handle.HasExited);
            Assert.Equal(0, handle.DisposeCallCount);
            Assert.True(vm.IsConnected);
            Assert.Empty(runner.StartCalls);
        }
        finally
        {
            managerField.SetValue(engine, null);
            handleField.SetValue(manager, null);
        }
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
