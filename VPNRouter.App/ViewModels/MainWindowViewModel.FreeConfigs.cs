#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private async Task<bool> ApplyFreeConfigAsync(FreeConfigEntry entry)
    {
        try
        {
            if (entry.Status != FreeConfigStatus.Verified)
            {
                _logger.Warning("[VM] ApplyFreeConfig rejected (status={Status})", entry.Status);
                return false;
            }

            if (!_settings.App.FreeConfigSecurityWarningAcked)
            {
                var proceed = await ShowFreeConfigSecurityWarningAsync();
                if (!proceed) return false;
                _settings.App.FreeConfigSecurityWarningAcked = true;
                SaveSettings();
            }

            var newEntry = entry.ToVlessServerEntry();

            var prevSelectedServer = SelectedServer;
            var prevIsConnected = IsConnected || _engine.IsRunning;
            var prevIsSubscribeMode = IsSubscribeMode;
            var prevIsVlessMode = IsVlessMode;
            var prevSelectedModeIndex = SelectedServerModeIndex;

            var entries = Servers.Select(s => s.ToEntry()).ToList();
            var index = FreeConfigSelection.SelectOrAdd(entries, newEntry);
            bool added = index == Servers.Count;
            var target = added ? new ServerViewModel(entries[index]) : Servers[index];
            if (added) Servers.Add(target);

            IsConnecting = true;
            SelectedServer = target;
            IsSubscribeMode = false;
            IsVlessMode = true;
            SelectedServerModeIndex = 0;

            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            if (IsConnected || _engine.IsRunning)
            {
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnected = false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
            var startTask = Task.Run(
                () => _engine.StartAsync(_settings, cts.Token, _skipVpnConflictThisSession),
                cts.Token);

            var outcome = await Internals.TwoPhaseStartCoordinator.RunAsync(
                startTask: startTask,
                subscribeStarted: handler =>
                {
                    void Wrapper(int pid) => handler(pid);
                    _engine.SingBoxStarted += Wrapper;
                    return () => _engine.SingBoxStarted -= Wrapper;
                },
                subscribeConnected: handler =>
                {
                    void Wrapper(int pid) => handler(pid);
                    _engine.Connected += Wrapper;
                    return () => _engine.Connected -= Wrapper;
                },
                cancellationToken: cts.Token);

            if (outcome == Internals.TwoPhaseStartOutcome.PhaseATimeout || outcome == Internals.TwoPhaseStartOutcome.PhaseBTimeout)
            {
                var isPhaseA = outcome == Internals.TwoPhaseStartOutcome.PhaseATimeout;
                _logger.Warning("[VM] ApplyFreeConfig: Phase {Phase} timed out", isPhaseA ? "A" : "B");
                try { await Task.Run(() => _engine.Stop()); } catch { }
                StatusText = isPhaseA ? Strings.StartTimeoutPhaseA : Strings.StartTimeoutPhaseB;

                if (added)
                {
                    Servers.Remove(target);
                }

                if (prevIsConnected && prevSelectedServer != null)
                {
                    SelectedServer = prevSelectedServer;
                    IsSubscribeMode = prevIsSubscribeMode;
                    IsVlessMode = prevIsVlessMode;
                    SelectedServerModeIndex = prevSelectedModeIndex;
                    SaveSettings();
                    _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

                    try
                    {
                        using var rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await Task.Run(() => _engine.StartAsync(_settings, rollbackCts.Token, _skipVpnConflictThisSession));
                        IsConnected = true;
                        ConnectButtonText = Strings.StopVPN;
                        StartSubRefreshTimer();
                        RefreshActiveIndicator();
                        RestoreConnectedStatus();
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.Warning("[VM] ApplyFreeConfig: rollback start failed ({Type})", rollbackEx.GetType().Name);
                        IsConnected = false;
                        ConnectButtonText = Strings.StartVPN;
                        RefreshActiveIndicator();
                    }
                }
                else
                {
                    IsConnected = false;
                    ConnectButtonText = Strings.StartVPN;
                    RefreshActiveIndicator();
                }

                IsConnecting = false;
                return false;
            }

            if (outcome != Internals.TwoPhaseStartOutcome.Connected)
            {
                try { await startTask; }
                catch (Exception ex)
                {
                    _logger.Warning("[VM] ApplyFreeConfig: start task did not reach Connected ({Type})", ex.GetType().Name);
                }

                if (outcome == Internals.TwoPhaseStartOutcome.Cancelled)
                {
                    try { await Task.Run(() => _engine.Stop()); } catch { }
                }

                IsConnecting = false;
                IsConnected = false;
                ConnectButtonText = Strings.StartVPN;
                RefreshActiveIndicator();
                return false;
            }

            try { await startTask; } catch { }
            IsConnected = true;
            IsConnecting = false;
            _lastSuccessfulConnectAt = DateTime.UtcNow;
            ConnectButtonText = Strings.StopVPN;
            StartSubRefreshTimer();
            RefreshActiveIndicator();
            RestoreConnectedStatus();
            ConflictingVpnWarningText = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("ApplyFreeConfig failed ({Type})", ex.GetType().Name);
            IsConnecting = false;
            return false;
        }
    }

    private async Task<bool> ShowFreeConfigSecurityWarningAsync()
    {
        var owner = GetMainWindow();
        if (owner == null) return true;

        var tcs = new TaskCompletionSource<bool>();

        IBrush Tok(string key, IBrush fallback)
            => owner.TryFindResource(key, owner.ActualThemeVariant, out var v)
                && v is IBrush b
                ? b
                : fallback;

        var proceedBtn = new Button
        {
            Content = Strings.FcSecWarnProceed,
            Padding = new Thickness(12, 6),
            FontWeight = FontWeight.SemiBold,
            Background = Tok("SuccessSolidBrush", Avalonia.Media.Brushes.SeaGreen),
            Foreground = Tok("AccentOnSolidBrush", Avalonia.Media.Brushes.White),
            CornerRadius = new CornerRadius(4),
        };
        var cancelBtn = new Button
        {
            Content = Strings.FcSecWarnCancel,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(4),
        };

        var dialog = new Window
        {
            Title = Strings.FcSecWarnTitle,
            Width = 520,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            [Controls.PictogramText.IconsProperty] = "warning",
                            [Controls.PictogramText.TextProperty] = "\u26a0 " + Strings.FcSecWarnHeader,
                            FontSize = 15,
                            FontWeight = FontWeight.Bold,
                            Foreground = Tok("WarningFgBrush", Avalonia.Media.Brushes.DarkOrange),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = Strings.FcSecWarnBody,
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Tok("WarningBgBrush", Avalonia.Media.Brushes.LightYellow),
                            BorderBrush = Tok("WarningBorderBrush", Avalonia.Media.Brushes.Goldenrod),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                [Controls.PictogramText.IconsProperty] = "error",
                                [Controls.PictogramText.TextProperty] = Strings.FcSecWarnDontUseList,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Tok("WarningFgBrush", Avalonia.Media.Brushes.SaddleBrown),
                            },
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Tok("SuccessBgBrush", Avalonia.Media.Brushes.Honeydew),
                            BorderBrush = Tok("SuccessBorderBrush", Avalonia.Media.Brushes.SeaGreen),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                [Controls.PictogramText.IconsProperty] = "check",
                                [Controls.PictogramText.TextProperty] = Strings.FcSecWarnGoodFor,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Tok("SuccessFgBrush", Avalonia.Media.Brushes.DarkGreen),
                            },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Margin = new Thickness(0, 10, 0, 0),
                            Children = { cancelBtn, proceedBtn },
                        },
                    },
                },
            },
        };

        proceedBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
