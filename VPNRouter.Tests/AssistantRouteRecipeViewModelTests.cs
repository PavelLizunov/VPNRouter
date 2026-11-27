using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class AssistantRouteRecipeViewModelTests
{
    [AvaloniaFact]
    public async Task Games_recipe_saves_mode_and_exact_list_and_undo_restores_them()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = true,
            ReadAssistantGameProcesses = () => ["GameB.exe", "gameA.exe", "GameA.exe", "OldGame.exe"],
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            string? preview = null;
            string result = await vm.RunAssistantGoalAsync("пустить все игры вне впн", window, panel,
                (description, _) =>
                {
                    preview = description;
                    return Task.FromResult(true);
                }, default);

            Assert.Equal(Strings.AssistantGamesSaved, result);
            Assert.NotNull(preview);
            Assert.Contains("gameA.exe, GameB.exe", preview);
            Assert.Contains("Все, кроме выбранных", preview);

            var saved = SettingsLoader.Load(AppPaths.ConfigYamlPath);
            Assert.Equal("split", saved.App.RoutingMode);
            Assert.Equal("exclude", saved.App.RoutingAppsMode);
            Assert.Equal(new[] { "OldGame.exe", "gameA.exe", "GameB.exe" }, saved.App.RoutingAppsExclude);
            Assert.Equal(new[] { "firefox.exe" }, saved.App.RoutingAppsInclude);
            Assert.False(saved.App.BypassRussianTraffic);
            Assert.True(vm.HasAssistantUndo);
            Assert.Contains(vm.BypassAppGroups.SelectMany(group => group.Apps),
                app => app.ProcessName == "gameA.exe" && app.IsChecked);

            Assert.Equal(Strings.AssistantUndone, await vm.UndoAssistantGoalAsync(default));
            var restored = SettingsLoader.Load(AppPaths.ConfigYamlPath);
            Assert.Equal("include", restored.App.RoutingAppsMode);
            Assert.Equal(new[] { "OldGame.exe" }, restored.App.RoutingAppsExclude);
            Assert.Equal(new[] { "firefox.exe" }, restored.App.RoutingAppsInclude);
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Games_recipe_decline_changes_nothing()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        string original = File.ReadAllText(AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = true,
            ReadAssistantGameProcesses = () => ["GameA.exe"],
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            Assert.Equal(Strings.AssistantNoChanges, await vm.RunAssistantGoalAsync(
                "Route all games directly", window, panel, (_, _) => Task.FromResult(false), default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Games_recipe_cancellation_changes_nothing()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        string original = File.ReadAllText(AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = true,
            ReadAssistantGameProcesses = () => ["GameA.exe"],
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            using var abort = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RunAssistantGoalAsync(
                "пустить все игры вне впн", window, panel, (_, _) =>
                {
                    abort.Cancel();
                    return Task.FromResult(true);
                }, abort.Token));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Games_recipe_reports_empty_detection_and_unsupported_platform_without_a_confirmation()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        string original = File.ReadAllText(AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = true,
            ReadAssistantGameProcesses = () => [],
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            Assert.Equal(Strings.AssistantGamesNotFound, await vm.RunAssistantGoalAsync(
                "send all games outside the VPN", window, panel, UnexpectedConfirmation, default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }

        using var unsupported = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = false,
            ReadAssistantGameProcesses = () =>
                throw new InvalidOperationException("Unsupported platforms must not scan."),
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var unsupportedPanel = new AssistantSetupPanel { DataContext = unsupported };
        var unsupportedWindow = new Window { Content = unsupportedPanel };
        unsupportedWindow.Show();
        try
        {
            Assert.Equal(Strings.AssistantGamesPlatformUnsupported, await unsupported.RunAssistantGoalAsync(
                "send all games outside the VPN", unsupportedWindow, unsupportedPanel, UnexpectedConfirmation, default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(unsupported.HasAssistantUndo);
        }
        finally { unsupportedWindow.Close(); }
    }

    [AvaloniaFact]
    public async Task Relevant_conflicts_and_unsupported_config_mode_block_before_confirmation()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        settings.App.CustomRules = [new CustomRule { Action = "proxy", Type = "domain", Value = "example.com" }];
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        string original = File.ReadAllText(AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = true,
            ReadAssistantGameProcesses = () => ["GameA.exe"],
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            Assert.Equal(Strings.AssistantRuleConflict, await vm.RunAssistantGoalAsync(
                "пустить все игры вне впн", window, panel, UnexpectedConfirmation, default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));

            var customConfig = NewSettings();
            customConfig.App.ConfigMode = "custom";
            SettingsLoader.Save(customConfig, AppPaths.ConfigYamlPath);
            original = File.ReadAllText(AppPaths.ConfigYamlPath);
            Assert.Equal(Strings.AssistantRecipeConfigMode, await vm.RunAssistantGoalAsync(
                "сделать ру сайты вне впн", window, panel, UnexpectedConfirmation, default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ambiguous_text_keeps_the_existing_hint_path_and_changes_nothing()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        string original = File.ReadAllText(AppPaths.ConfigYamlPath);
        int calls = 0;
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            StepAssistantAsync = (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new AssistantStep(AssistantStepStatus.Clarify));
            },
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            Assert.Equal(Strings.AssistantSingleCommand, await vm.RunAssistantGoalAsync(
                "Route all games directly and connect the VPN", window, panel, UnexpectedConfirmation, default));
            Assert.Equal(1, calls);
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Russian_sites_recipe_changes_only_the_bypass_and_undo_restores_it()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        settings.App.RoutingMode = "full";
        settings.App.RoutingAppsInclude = ["firefox.exe"];
        settings.App.RoutingAppsExclude = ["OldGame.exe"];
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantRuRuleSetAvailable = () => true,
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            string? preview = null;
            string result = await vm.RunAssistantGoalAsync("сделать ру сайты вне впн", window, panel,
                (description, _) =>
                {
                    preview = description;
                    return Task.FromResult(true);
                }, default);

            Assert.Equal(Strings.AssistantRuSaved, result);
            Assert.NotNull(preview);
            Assert.Contains(".ru, .su, .рф", preview);
            Assert.Contains("Остальной трафик", preview);

            var saved = SettingsLoader.Load(AppPaths.ConfigYamlPath);
            Assert.True(saved.App.BypassRussianTraffic);
            Assert.Equal("full", saved.App.RoutingMode);
            Assert.Equal(new[] { "OldGame.exe" }, saved.App.RoutingAppsExclude);
            Assert.Equal(new[] { "firefox.exe" }, saved.App.RoutingAppsInclude);
            Assert.True(vm.HasAssistantUndo);

            Assert.Equal(Strings.AssistantUndone, await vm.UndoAssistantGoalAsync(default));
            Assert.False(SettingsLoader.Load(AppPaths.ConfigYamlPath).App.BypassRussianTraffic);
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Russian_sites_recipe_reports_an_already_enabled_bypass_without_changes()
    {
        using var scope = new Scope();
        var settings = NewSettings();
        settings.App.BypassRussianTraffic = true;
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        string original = File.ReadAllText(AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantRuRuleSetAvailable = () => true,
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var panel = new AssistantSetupPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            Assert.Equal(Strings.AssistantRuAlreadyOn, await vm.RunAssistantGoalAsync(
                "enable the russian traffic bypass", window, panel, UnexpectedConfirmation, default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(vm.HasAssistantUndo);
        }
        finally { window.Close(); }

        using var pendingVm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantRuRuleSetAvailable = () => false,
            StepAssistantAsync = (_, _, _, _, _) =>
                throw new InvalidOperationException("Local recipes must not call the provider."),
        };
        var pendingPanel = new AssistantSetupPanel { DataContext = pendingVm };
        var pendingWindow = new Window { Content = pendingPanel };
        pendingWindow.Show();
        try
        {
            Assert.Equal(Strings.AssistantRuRuleSetsPending, await pendingVm.RunAssistantGoalAsync(
                "сделать ру сайты вне впн", pendingWindow, pendingPanel, UnexpectedConfirmation, default));
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.False(pendingVm.HasAssistantUndo);
        }
        finally { pendingWindow.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Хочу, чтобы игры шли мимо VPN", "games")]
    [InlineData("Сделать ру сайты вне впн", "russia")]
    public async Task Home_accepts_free_text_and_previews_the_actual_change(string goal, string scenario)
    {
        using var scope = new Scope();
        var settings = NewSettings();
        settings.App.UiMode = "simple";
        settings.App.RoutingAppsExclude = [];
        settings.App.Subscriptions[0].Servers = [new() { Name = "Stockholm", Server = "test.invalid", Port = 443, Security = "none", Uuid = Guid.Empty.ToString() }];
        SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
        {
            IsAssistantGameDetectionSupported = true,
            ReadAssistantGameProcesses = () => ["game.exe"],
            IsAssistantRuRuleSetAvailable = () => false,
            StepAssistantAsync = (_, _, _, _, _) => throw new InvalidOperationException("Unexpected provider call."),
        };
        var page = new SimplePage { DataContext = vm };
        var window = new Window { Content = page, Width = 380, Height = 660, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        try
        {
            var panel = page.FindControl<AssistantSetupPanel>("SetupAssistant")!;
            var input = panel.FindControl<TextBox>("AssistantGoal")!;
            var confirm = panel.FindControl<Button>("ConfirmAssistant")!;
            var status = panel.FindControl<TextBlock>("AssistantStatus")!;
            var undo = panel.FindControl<Button>("UndoAssistant")!;
            var scroll = page.FindControl<ScrollViewer>("SimpleContentScroll")!;
            var original = File.ReadAllText(AppPaths.ConfigYamlPath);
            input.Focus();
            input.Text = goal;
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitFor(() => confirm.IsEffectivelyVisible, status);
            Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            Assert.Contains(scenario == "games" ? "game.exe" : ".ru, .su, .рф", status.Text);
            Assert.Equal("Применить", confirm.Content);
            window.UpdateLayout();
            var y = panel.TranslatePoint(default, scroll)!.Value.Y;
            scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y + y - 12));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var bottom = confirm.TranslatePoint(new Point(0, confirm.Bounds.Height), scroll)!.Value.Y;
            Assert.InRange(bottom, confirm.Bounds.Height, scroll.Bounds.Height);
            if (OperatingSystem.IsWindows())
            {
                using var image = window.CaptureRenderedFrame();
                Assert.NotNull(image);
                image.Save(Path.Combine(ScreenshotHelper.ScreenshotsDir, $"simple-home-assistant-{scenario}-preview-ru-380.png"),
                    PngBitmapEncoderOptions.Default);
            }
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => undo.IsEffectivelyVisible, status);
            var saved = SettingsLoader.Load(AppPaths.ConfigYamlPath);
            Assert.Equal(scenario == "games" ? "exclude" : "include", saved.App.RoutingAppsMode);
            Assert.Equal(scenario == "russia", saved.App.BypassRussianTraffic);
        }
        finally { window.Close(); }
    }

    private static async Task WaitFor(Func<bool> predicate, TextBlock status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate() && !timeout.IsCancellationRequested)
            await Task.Delay(15);
        Assert.True(predicate(), status.Text);
    }

    private static Task<bool> UnexpectedConfirmation(string text, CancellationToken ct) =>
        throw new InvalidOperationException("This request cannot authorize a change.");

    private static AppSettings NewSettings()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Language = "ru";
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsIncludeInitialized = true;
        settings.App.RoutingAppsInclude = ["firefox.exe"];
        settings.App.RoutingAppsExclude = ["OldGame.exe"];
        settings.App.BypassRussianTraffic = false;
        settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-recipes", Enabled = true }];
        return settings;
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _previousDirectory;
        private readonly string _previousLanguage;
        private readonly string _root;

        internal Scope()
        {
            _previousDirectory = AppPaths.DataDir;
            _previousLanguage = Strings.Lang;
            _root = Path.Combine(Path.GetTempPath(), $"vpnrouter-recipes-{Guid.NewGuid():N}");
            AppPaths.OverrideDataDir(_root);
        }

        public void Dispose()
        {
            AppPaths.OverrideDataDir(_previousDirectory);
            Strings.Lang = _previousLanguage;
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A diagnostics handle can keep the temporary directory briefly busy.
            }
        }
    }
}
