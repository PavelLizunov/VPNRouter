#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private async Task<bool> ApplyFreeConfigAsync(FreeConfigEntry entry)
    {
        if (ProductEdition.IsBusiness || _businessRules != null) return false;
        if (entry.Status != FreeConfigStatus.Verified || IsConnecting || _isReconnecting || IsServiceManagedVpn)
            return false;

        var previousPid = _engine.SingBoxPid;
        bool previousConnected = IsConnected;
        IsConnecting = true;
        try
        {
            if (!_settings.App.FreeConfigSecurityWarningAcked && !await ShowFreeConfigSecurityWarningAsync())
                return false;

            var candidate = SnapshotFreeConfigSettings();
            var index = FreeConfigSelection.SelectOrAdd(candidate.Vless.Servers, entry.ToVlessServerEntry());
            var target = candidate.Vless.Servers[index];
            candidate.Vless.ActiveServer = target.Name;
            candidate.App.ConfigMode = "generated";
            candidate.App.FreeConfigSecurityWarningAcked = true;

            using var cts = new CancellationTokenSource(Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget +
                Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget);
            var applied = await Task.Run(() => _engine.ConnectVerifiedFreeConfigAsync(candidate,
                () => _settingsStore.Save(candidate, AppPaths.ConfigYamlPath), _skipVpnConflictThisSession, cts.Token));
            if (!applied)
            {
                StatusText = Strings.ApplyFailed;
                return false;
            }

            _settings = candidate;
            _isLoadingUI = true;
            try
            {
                LoadServersIntoUI();
                IsSubscribeMode = false;
                IsVlessMode = true;
                SelectedServerModeIndex = 0;
            }
            finally { _isLoadingUI = false; }

            _displayExternalTunnel = false;
            IsConnected = true;
            _lastSuccessfulConnectAt = DateTime.UtcNow;
            ConnectButtonText = Strings.StopVPN;
            StartSubRefreshTimer();
            RestoreConnectedStatus();
            ConflictingVpnWarningText = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("ApplyFreeConfig failed ({Type})", ex.GetType().Name);
            StatusText = Strings.ApplyFailed;
            return false;
        }
        finally
        {
            IsConnecting = false;
            var currentPid = _engine.SingBoxPid;
            IsConnected = currentPid.HasValue &&
                ((previousConnected && currentPid == previousPid) || _engine.DescribeReadiness(currentPid.Value) == null);
            ConnectButtonText = IsConnected ? Strings.StopVPN : Strings.StartVPN;
        }
    }

    private AppSettings SnapshotFreeConfigSettings()
    {
        var current = _settings;
        try
        {
            _settings = SettingsLoader.Copy(current);
            ReadSettingsFromControls();
            if (!string.IsNullOrEmpty(CustomRulesErrorText))
                throw new InvalidOperationException("Invalid custom routing rules");
            // The controls return their backing server objects. Detach those before choosing the candidate.
            return SettingsLoader.Copy(_settings);
        }
        finally { _settings = current; }
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
