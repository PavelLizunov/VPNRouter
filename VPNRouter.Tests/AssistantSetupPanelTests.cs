using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class AssistantSetupPanelTests
{
    [AvaloniaFact]
    public async Task Bare_finish_is_unconfirmed_and_semantic_navigation_checks_the_selected_page()
    {
        var dataDir = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"vpnrouter-assistant-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.Language = "en";
            settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-assistant", Enabled = true }];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            bool navigate = false;
            int calls = 0;
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                StepAssistantAsync = (_, _, _, observation, _) =>
                {
                    calls++;
                    Assert.DoesNotContain(observation.Controls, control => control.Name.Contains("Repair", StringComparison.Ordinal));
                    return Task.FromResult(navigate
                        ? new AssistantStep(AssistantStepStatus.Act, observation.Controls.Single(c => c.Name == "OpenApplications"))
                        : new AssistantStep(AssistantStepStatus.Finish));
                },
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var window = new Window { Content = panel };
            window.Show();
            try
            {
                Assert.Equal(VPNRouter.Core.Localization.Strings.AssistantUnconfirmed,
                    await vm.RunAssistantGoalAsync("Open Applications", window, panel, default));
                navigate = true;
                Assert.Equal(VPNRouter.Core.Localization.Strings.AssistantPageOpened,
                    await vm.RunAssistantGoalAsync("Open Applications", window, panel, default));
                Assert.False(vm.IsSimpleMode);
                Assert.True(vm.IsAppsTabSelected);
                Assert.Equal(2, calls);
            }
            finally { window.Close(); }
        }
        finally
        {
            AppPaths.OverrideDataDir(dataDir);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Repair_explains_disabled_auto_route_without_asking_the_model_or_changing_settings()
    {
        var dataDir = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"vpnrouter-repair-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.Tun.AutoRoute = false;
            settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-assistant", Enabled = true }];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            var original = File.ReadAllText(AppPaths.ConfigYamlPath);
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                StepAssistantAsync = (_, _, _, _, _) => throw new InvalidOperationException("Unexpected provider call."),
            };

            Assert.Equal(VPNRouter.Core.Localization.Strings.AssistantRepairAutoRoute,
                await vm.RepairAssistantVpnAsync(TestContext.Current.CancellationToken));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.IsConnected);
        }
        finally
        {
            AppPaths.OverrideDataDir(dataDir);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    [AvaloniaFact]
    public void LeavingSimpleModeCancelsThePendingAssistantRequest()
    {
        var dataDir = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"vpnrouter-assistant-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.Language = "en";
            settings.App.Subscriptions =
            [
                new() { Url = "https://ninitux.com/sub/synthetic-assistant", Enabled = true }
            ];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            var revision = File.ReadAllText(AppPaths.ConfigYamlPath);
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            CancellationToken request = default;
            var pending = new TaskCompletionSource<AssistantStep>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                StepAssistantAsync = (_, _, _, _, ct) =>
                {
                    request = ct;
                    return pending.Task.WaitAsync(ct);
                },
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var window = new Window { Content = panel };
            window.Show();
            panel.FindControl<Button>("OpenAssistant")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.FindControl<TextBox>("AssistantGoal")!.Text = "Route only Chrome through the VPN";
            panel.FindControl<Button>("RunAssistant")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(request.CanBeCanceled);
            Assert.False(request.IsCancellationRequested);

            vm.ToggleUiModeCommand.Execute(null);

            Assert.False(vm.IsSimpleMode);
            Assert.True(request.IsCancellationRequested);
            Assert.Equal(revision, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
            window.Close();
        }
        finally
        {
            AppPaths.OverrideDataDir(dataDir);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}
