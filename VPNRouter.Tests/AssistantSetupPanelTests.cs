using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class AssistantSetupPanelTests
{
    [Theory]
    [InlineData("Подключи VPN.", "SetVpnConnected", true, null)]
    [InlineData("Disconnect the VPN", "SetVpnConnected", false, null)]
    [InlineData("Disable strict DNS", "SetStrictDns", false, null)]
    [InlineData("Включи сброс DNS при запуске", "SetFlushDnsOnStart", true, null)]
    [InlineData("Route only Firefox through the VPN", "RouteOnlyApplication", null, "Firefox")]
    [InlineData("VPN только для Firefox", "RouteOnlyApplication", null, "Firefox")]
    [InlineData("Microsoft Edge напрямую", "RouteApplicationDirect", null, "Microsoft Edge")]
    [InlineData("Bypass VPN for Microsoft Edge", "RouteApplicationDirect", null, "Microsoft Edge")]
    [InlineData("Пусти браузер по умолчанию напрямую", "RouteApplicationDirect", null, "браузер по умолчанию")]
    [InlineData("Open network settings", "OpenNetworkSettings", null, null)]
    public void Single_command_binds_its_effect_and_operand(string text, string control, bool? value, string? application)
    {
        Assert.Equal(new AssistantIntent(control, value, application), AssistantIntent.Parse(text));
    }

    [Theory]
    [InlineData("Why would I disconnect VPN?")]
    [InlineData("Переведи «Включи VPN»")]
    [InlineData("Не отключай VPN")]
    [InlineData("Enable strict DNS and disconnect VPN")]
    [InlineData("Route Edge directly if Steam closes")]
    public void Informational_or_compound_text_has_no_executable_intent(string goal) =>
        Assert.Null(AssistantIntent.Parse(goal));

    [AvaloniaFact]
    public async Task Assistant_rejects_unbound_results_and_verifies_semantic_navigation()
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
            bool routeConflict = false;
            int calls = 0;
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                ResolveAssistantApplication = app => app == "synthetic-target.exe" ? app : null,
                StepAssistantAsync = (_, _, _, observation, _) =>
                {
                    calls++;
                    Assert.DoesNotContain(observation.Controls, control => control.Name.Contains("Repair", StringComparison.Ordinal));
                    if (routeConflict)
                    {
                        Assert.All(observation.Controls, control => Assert.StartsWith("Route", control.Name));
                        return Task.FromResult(new AssistantStep(AssistantStepStatus.Act,
                            observation.Controls.Single(c => c.Name.StartsWith("RouteOnlyApplication", StringComparison.Ordinal)),
                            Path.GetFileName(Environment.ProcessPath!)));
                    }
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
                    await vm.RunAssistantGoalAsync("Open Applications", window, panel, UnexpectedConfirmation, default));
                var revision = File.ReadAllText(AppPaths.ConfigYamlPath);
                routeConflict = true;
                Assert.Equal(Strings.AssistantProposalMismatch,
                    await vm.RunAssistantGoalAsync("Route only synthetic-target.exe through VPN", window, panel, UnexpectedConfirmation, default));
                Assert.Equal(revision, File.ReadAllText(AppPaths.ConfigYamlPath));
                Assert.False(vm.IsConnected);
                Assert.False(vm.HasAssistantUndo);
                routeConflict = false;
                navigate = true;
                Assert.Equal(Strings.AssistantPageOpened,
                    await vm.RunAssistantGoalAsync("Open Applications", window, panel, (description, _) =>
                    {
                        Assert.Equal(Strings.AssistantChangeDescription("OpenApplications", null, null, false), description);
                        return Task.FromResult(true);
                    }, default));
                Assert.False(vm.IsSimpleMode);
                Assert.True(vm.IsAppsTabSelected);
                Assert.Equal(3, calls);
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
    public void Connection_diagnosis_is_available_only_for_the_subscription_used_by_the_attempt()
    {
        var previous = AppPaths.DataDir;
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            AppPaths.OverrideDataDir(directory.FullName);
            const string current = "https://ninitux.com/sub/synthetic-current";
            const string other = "https://ninitux.com/sub/synthetic-other";
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.Subscriptions = [new() { Url = current, Enabled = true }];
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger);
            var panel = new AssistantSetupPanel { DataContext = vm };
            var window = new Window { Content = panel };
            window.Show();
            try
            {
                var button = panel.FindControl<Button>("DiagnoseConnection")!;
                vm.SetSourceConnectionObservation("tun-owned", other);
                Assert.False(button.IsVisible);
                vm.SetSourceConnectionObservation("phase-b-timeout", current);
                Assert.True(button.IsVisible);
                vm.SetSourceConnectionObservation(null);
                Assert.False(button.IsVisible);
                vm.SetSourceConnectionObservation("cancelled", current);
                Assert.False(button.IsVisible);
                Assert.False(vm.IsConnected);
            }
            finally { window.Close(); }
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            directory.Delete(true);
        }
    }

    private static Task<bool> UnexpectedConfirmation(string text, CancellationToken ct) =>
        throw new InvalidOperationException("This request cannot authorize a change.");

    [AvaloniaFact]
    public async Task Unrequested_or_stale_effects_leave_the_settings_and_tunnel_untouched()
    {
        string previousDir = AppPaths.DataDir;
        string temporary = Path.Combine(Path.GetTempPath(), $"assistant-consent-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.FlushDnsOnStart = false;
            settings.App.RoutingAppsMode = "exclude";
            settings.App.RoutingAppsExclude = ["already.exe"];
            settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-consent", Enabled = true }];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            int calls = 0;
            bool replay = true;
            string browser = "msedge.exe";
            Func<AssistantControl, AssistantStep> propose = c => new(AssistantStepStatus.Act, c, Toggle: true);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                ResolveAssistantApplication = app => app == "already.exe" ? app : app == "Edge" || app == "браузер" ? browser : null,
                StepAssistantAsync = (_, _, _, observation, _) =>
                {
                    calls++;
                    if (replay)
                    {
                        var c = observation.Controls.First();
                        return Task.FromResult(new AssistantStep(AssistantStepStatus.Act, c,
                            Toggle: c.Kind == "toggle" ? c.Active != true : null));
                    }
                    return Task.FromResult(propose(Assert.Single(observation.Controls)));
                },
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var window = new Window { Content = panel };
            window.Show();
            try
            {
                string original = File.ReadAllText(AppPaths.ConfigYamlPath);
                int saves = store.SaveCount;
                using var stream = typeof(AssistantSetupPanelTests).Assembly.GetManifestResourceStream("AssistantAbstention.json")!;
                var questions = System.Text.Json.JsonSerializer.Deserialize<string[]>(stream)!;
                Assert.Equal(98, questions.Length);
                Assert.Equal("C136D81D2F423177F57005356675B07253E371133ECBE0451B49C356E3B20E24",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", questions)))));
                foreach (string question in questions)
                {
                    Assert.Null(AssistantIntent.Parse(question));
                    var message = await vm.RunAssistantGoalAsync(question, window, panel, UnexpectedConfirmation, default);
                    Assert.False(string.IsNullOrWhiteSpace(message), question);
                }
                string hint = await vm.RunAssistantGoalAsync("How do I connect my VPN?", window, panel, UnexpectedConfirmation, default);
                string commandHint = Strings.AssistantCommandExample("SetVpnConnected", true, null)!;
                Assert.Equal(Strings.AssistantSuggestedCommand(commandHint), hint);
                Assert.InRange(calls, 1, 99);
                Assert.Equal(saves, store.SaveCount);
                Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
                Assert.False(vm.IsConnected);
                Assert.False(vm.HasAssistantUndo);
                replay = false;
                propose = c => new(AssistantStepStatus.Act, c, Toggle: false);
                Assert.Equal(Strings.AssistantProposalMismatch,
                    await vm.RunAssistantGoalAsync("Connect VPN", window, panel, UnexpectedConfirmation, default));
                var beforeNoOp = calls;
                propose = _ => throw new InvalidOperationException("Already applied changes must be handled locally");
                Assert.Equal(Strings.AssistantAlreadySet, await vm.RunAssistantGoalAsync("Route already.exe directly",
                    window, panel, UnexpectedConfirmation, default));
                Assert.Equal(Strings.AssistantNoChanges, await vm.RunAssistantGoalAsync("Disable DNS flushing on startup",
                    window, panel, UnexpectedConfirmation, default));
                Assert.Equal(beforeNoOp, calls);
                propose = c => new(AssistantStepStatus.Act, c with { Name = "RouteOnlyApplicationForResolvedTarget" });
                Assert.Equal(Strings.AssistantProposalMismatch,
                    await vm.RunAssistantGoalAsync("Пусти Edge напрямую", window, panel, UnexpectedConfirmation, default));
                propose = c => new(AssistantStepStatus.Act, c);
                Assert.Equal(Strings.AssistantSettingsChanged,
                    await vm.RunAssistantGoalAsync("Пусти браузер напрямую", window, panel, (description, _) =>
                    {
                        Assert.Contains("msedge.exe", description);
                        browser = "firefox.exe";
                        return Task.FromResult(true);
                    }, default));
                propose = c => new(AssistantStepStatus.Act, c, Toggle: true);
                const string command = "Enable DNS flushing on startup";
                Assert.Equal(Strings.AssistantNoChanges, await vm.RunAssistantGoalAsync(command, window, panel,
                    (description, _) =>
                    {
                        Assert.Equal(Strings.AssistantChangeDescription("SetFlushDnsOnStart", true, null, false), description);
                        return Task.FromResult(false);
                    }, default));
                using var abort = new CancellationTokenSource();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RunAssistantGoalAsync(command, window, panel,
                    (_, _) =>
                    {
                        abort.Cancel();
                        return Task.FromResult(true);
                    }, abort.Token));
                Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));

                Assert.Equal(Strings.AssistantSettingsChanged, await vm.RunAssistantGoalAsync(command, window, panel,
                    (_, _) =>
                    {
                        File.AppendAllText(AppPaths.ConfigYamlPath, "\n# newer settings\n");
                        return Task.FromResult(true);
                    }, default));
                Assert.EndsWith("# newer settings\n", File.ReadAllText(AppPaths.ConfigYamlPath));
                Assert.Equal(saves, store.SaveCount);
                Assert.False(vm.HasAssistantUndo);
                Assert.False(vm.IsConnected);
            }
            finally { window.Close(); }
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDir);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData("confirm")]
    [InlineData("enter")]
    [InlineData("decline")]
    [InlineData("escape")]
    [InlineData("outside")]
    [InlineData("subscription")]
    public async Task Only_explicit_confirmation_applies_the_displayed_preference(string response)
    {
        var previousDir = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"assistant-ui-consent-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.FlushDnsOnStart = false;
            settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-consent", Enabled = true }];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            int requests = 0;
            using var vm = new MainWindowViewModel(RealSettingsStore.Instance, logger)
            {
                StepAssistantAsync = (_, _, _, obs, _) =>
                {
                    requests++;
                    return Task.FromResult(new AssistantStep(AssistantStepStatus.Act, Assert.Single(obs.Controls), Toggle: true));
                },
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var outside = new Button { Content = "Outside" };
            var window = new Window { Width = 520, Height = 600, Content = new StackPanel { Children = { panel, outside } } };
            window.Show();
            try
            {
                var run = panel.FindControl<Button>("RunAssistant")!;
                var confirm = panel.FindControl<Button>("ConfirmAssistant")!;
                var status = panel.FindControl<TextBlock>("AssistantStatus")!;
                var input = panel.FindControl<TextBox>("AssistantGoal")!;
                Assert.True(input.IsEffectivelyVisible);
                Assert.False(run.IsEnabled);
                input.Text = "Enable DNS flushing on startup";
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                run.PropertyChanged += (_, args) =>
                {
                    if (args.Property == Avalonia.Input.InputElement.IsEnabledProperty && run.IsEnabled)
                        completed.TrySetResult(true);
                };
                Assert.False(confirm.IsVisible);
                if (response == "enter")
                {
                    input.Focus();
                    input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                }
                else run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(confirm.IsVisible);
                Assert.Equal(Strings.AssistantChangeDescription("SetFlushDnsOnStart", true, null, false), status.Text);
                Assert.False(SettingsLoader.Load(AppPaths.ConfigYamlPath).App.FlushDnsOnStart);
                switch (response)
                {
                    case "enter":
                    case "confirm":
                        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        break;
                    case "decline":
                        var decline = panel.FindControl<Button>("CancelAssistant")!;
                        Assert.Equal(Strings.AssistantDecline, decline.Content);
                        decline.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        break;
                    case "escape":
                        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                        break;
                    case "outside":
                        window.UpdateLayout();
                        var center = outside.TranslatePoint(new Point(outside.Bounds.Width / 2, outside.Bounds.Height / 2), window)!.Value;
                        window.MouseDown(center, MouseButton.Left);
                        window.MouseUp(center, MouseButton.Left);
                        break;
                    case "subscription":
                        vm.Subscriptions.Single().Enabled = false;
                        break;
                }
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.False(confirm.IsVisible);
                var expected = response switch
                {
                    "enter" or "confirm" => Strings.AssistantPreferenceSaved,
                    "subscription" => Strings.AssistantCancelled,
                    _ => Strings.AssistantNoChanges,
                };
                Assert.Equal(expected, status.Text);
                Assert.Equal(response is "confirm" or "enter", SettingsLoader.Load(AppPaths.ConfigYamlPath).App.FlushDnsOnStart);
                Assert.Equal(1, requests);
                Assert.False(vm.IsConnected);
            }
            finally { window.Close(); }
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDir);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Exclusive_route_discloses_profile_and_regional_bypass_changes_before_writing()
    {
        var previousDir = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"assistant-route-preview-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.RoutingMode = "full";
            settings.App.BypassRussianTraffic = true;
            settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-consent", Enabled = true }];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                ResolveAssistantApplication = _ => "msedge.exe",
                StepAssistantAsync = (_, _, _, obs, _) =>
                    Task.FromResult(new AssistantStep(AssistantStepStatus.Act, Assert.Single(obs.Controls))),
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var window = new Window { Content = panel };
            window.Show();
            try
            {
                var original = File.ReadAllText(AppPaths.ConfigYamlPath);
                Assert.Equal(Strings.AssistantNoChanges, await vm.RunAssistantGoalAsync("VPN only for Edge", window, panel,
                    (description, _) =>
                    {
                        Assert.Contains("msedge.exe", description);
                        Assert.Contains(Strings.AssistantSplitProfileChange, description);
                        Assert.Contains(Strings.AssistantRegionalBypassChange, description);
                        return Task.FromResult(false);
                    }, default));
                Assert.Equal(original, File.ReadAllText(AppPaths.ConfigYamlPath));
            }
            finally { window.Close(); }
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDir);
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
            panel.FindControl<TextBox>("AssistantGoal")!.Text = "Connect VPN";
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

    [AvaloniaFact]
    public async Task InFlightRequestAndConfirmation_IgnoreScrollTabAndInsideClicks()
    {
        var previousDir = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"assistant-focus-guard-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.Language = "en";
            settings.App.FlushDnsOnStart = false;
            settings.App.Subscriptions = [new() { Url = "https://ninitux.com/sub/synthetic-guard", Enabled = true }];
            SettingsLoader.Save(settings, AppPaths.ConfigYamlPath);
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            var release = new TaskCompletionSource<AssistantStep>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken requestToken = default;
            AssistantControl? capturedControl = null;
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            using var vm = new MainWindowViewModel(store, logger)
            {
                StepAssistantAsync = (_, _, _, obs, ct) =>
                {
                    requestToken = ct;
                    capturedControl = Assert.Single(obs.Controls);
                    return release.Task.WaitAsync(ct);
                },
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var outside = new Button { Content = "Outside" };
            var window = new Window { Width = 520, Height = 640, Content = new StackPanel { Children = { panel, outside } } };
            window.Show();
            try
            {
                var run = panel.FindControl<Button>("RunAssistant")!;
                var confirm = panel.FindControl<Button>("ConfirmAssistant")!;
                var progress = panel.FindControl<ProgressBar>("AssistantProgress")!;
                var status = panel.FindControl<TextBlock>("AssistantStatus")!;
                panel.FindControl<TextBox>("AssistantGoal")!.Text = "Enable DNS flushing on startup";
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                run.PropertyChanged += (_, args) =>
                {
                    if (args.Property == InputElement.IsEnabledProperty && run.IsEnabled)
                        completed.TrySetResult(true);
                };

                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(progress.IsVisible);
                Assert.False(requestToken.IsCancellationRequested);

                // During in-flight request, clicking outside or pressing Tab must not cancel.
                window.UpdateLayout();
                var outsidePoint = outside.TranslatePoint(new Point(outside.Bounds.Width / 2, outside.Bounds.Height / 2), window)!.Value;
                window.MouseDown(outsidePoint, MouseButton.Left);
                window.MouseUp(outsidePoint, MouseButton.Left);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
                Assert.False(requestToken.IsCancellationRequested);

                // Resolve the step into the confirmation phase.
                Assert.NotNull(capturedControl);
                release.SetResult(new AssistantStep(AssistantStepStatus.Act, capturedControl, Toggle: true));
                await Task.Delay(50, TestContext.Current.CancellationToken);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(confirm.IsVisible);
                Assert.False(progress.IsVisible);

                // Pressing Tab or clicking inside the status card must keep the confirmation open.
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
                var statusPoint = status.TranslatePoint(new Point(4, 4), window)!.Value;
                window.MouseDown(statusPoint, MouseButton.Left);
                window.MouseUp(statusPoint, MouseButton.Left);
                Assert.True(confirm.IsVisible);

                // Escape declines cleanly.
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.False(confirm.IsVisible);
                Assert.Equal(Strings.AssistantNoChanges, status.Text);
            }
            finally { window.Close(); }
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDir);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}
