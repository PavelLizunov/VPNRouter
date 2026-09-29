using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private string _smpInput = string.Empty;

    [ObservableProperty] private string _smpErrorText = string.Empty;

    [ObservableProperty] private bool _smpFormExpanded = true;

    public bool SmpAutostartChecked
    {
        get => ServiceVm.IsInstalled
               && ServiceVm.IsRunning
               && AutostartVpn;
        set
        {
            if (_isLoadingUI) return;
            if (SmpAutostartChecked == value) return;

            if (value)
            {
                if (!ServiceVm.AutostartChecked)
                    ServiceVm.AutostartChecked = true;
                AutostartVpn = true;
            }
            else
            {
                AutostartVpn = false;
                var stillNeeded = _settings.App.AutostartZapret
                                  || _settings.App.AutostartTgProxy;
                if (!stillNeeded && ServiceVm.AutostartChecked)
                    ServiceVm.AutostartChecked = false;
            }
            SaveSettings();
            OnPropertyChanged(nameof(SmpAutostartChecked));
        }
    }

    public const string SimpleSplitProfile =
        "Discord_Privacy,Messengers,AI_Tools,Browsers,Work_Suite,Streaming,Gaming,Privacy_Shell";

    public string SmpConnectButtonText => IsConnected ? Strings.SmpStopVpn : Strings.SmpStartVpn;

    public IBrush SmpConnectButtonBrush
    {
        get
        {
            var key = IsConnected ? "DangerSolidBrush" : "AccentSolidBrush";
            var app = Avalonia.Application.Current;
            if (app != null &&
                app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) &&
                res is IBrush brush)
                return brush;
            return IsConnected
                ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
                : new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xE9));
        }
    }

    public string SmpHeroTitle => IsConnected ? Strings.SmpConnectedTitle : Strings.SmpDisconnectedTitle;

    public string SmpActiveServerLine
    {
        get
        {
            if (!IsConnected) return string.Empty;
            var (name, ip) = DeriveConnectedServerLabel();
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(ip)) return string.Empty;
            if (string.IsNullOrEmpty(name)) return $"{Strings.SmpActiveThrough} {ip}";
            if (string.IsNullOrEmpty(ip))   return $"{Strings.SmpActiveThrough} {name}";
            return $"{Strings.SmpActiveThrough} {name} · {ip}";
        }
    }

    private string? _lastConnectionAlert;

    private bool HasConnectionAlert => !string.IsNullOrEmpty(_lastConnectionAlert);

    private void RaiseSimpleAlertProps()
    {
        OnPropertyChanged(nameof(SimpleStatusIsOn));
        OnPropertyChanged(nameof(SimpleStatusIsWarn));
        OnPropertyChanged(nameof(SimpleStatusIsOff));
        OnPropertyChanged(nameof(SimpleStatusTitle));
        OnPropertyChanged(nameof(SimpleStatusDescription));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        if (value && HasConnectionAlert)
        {
            _lastConnectionAlert = null;
            RaiseSimpleAlertProps();
        }
    }

    public bool SimpleStatusIsOn   => IsConnected && !IsConnecting && !HasConnectionAlert;
    public bool SimpleStatusIsWarn => IsConnecting || HasConnectionAlert;
    public bool SimpleStatusIsOff  => !IsConnected && !IsConnecting && !HasConnectionAlert;

    public string SimpleStatusTitle => IsConnecting
        ? Strings.SmpStatusConnecting
        : (IsConnected && !HasConnectionAlert)
            ? Strings.SmpStatusProtected
            : Strings.SmpStatusNotConnected;

    public string SimpleStatusDescription
    {
        get
        {
            if (IsConnecting) return Strings.SmpStatusConnectingHint;
            if (HasConnectionAlert) return _lastConnectionAlert!;
            if (IsConnected)
            {
                var (name, ip) = DeriveConnectedServerLabel();
                var via = Strings.SmpStatusConnectedVia;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ip)) return $"{via} {name} · {ip}";
                if (!string.IsNullOrEmpty(name)) return $"{via} {name}";
                if (!string.IsNullOrEmpty(ip))   return $"{via} {ip}";
                return Strings.SmpStatusConnectedNoDetails;
            }
            return Strings.SmpStatusDisconnectedHint;
        }
    }

    public string SimpleConfigModeSummary
    {
        get
        {
            var configuredMode = _settings.App.ConfigMode ?? "generated";
            var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? Strings.SmpCfgSubscribe
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    ? Strings.SmpCfgManual
                    : Strings.SmpCfgCustom;
            var tunnelLabel = IsSplitTunnel ? Strings.SmpCfgSplit : Strings.SmpCfgFull;
            return $"{configLabel} · {tunnelLabel}";
        }
    }

    public string SimpleActiveOutboundLine
    {
        get
        {
            var ip = _engine?.ActiveServerAddress;
            if (string.IsNullOrEmpty(ip)) return string.Empty;

            var configuredMode = _settings.App.ConfigMode ?? "generated";
            string? name = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    ? (SelectedServer ?? Servers.FirstOrDefault())?.DisplayName
                    : null;

            return string.IsNullOrEmpty(name)
                ? $"{Strings.SmpStatusConnectedVia} {ip}"
                : $"{Strings.SmpStatusConnectedVia} {name}@{ip}";
        }
    }

    public bool SimpleActiveOutboundIsSuspect
    {
        get
        {
            var ip = _engine?.ActiveServerAddress;
            if (string.IsNullOrEmpty(ip) || _settings == null)
                return false;

            return !IsServerKnown(ip!, _settings);
        }
    }

    public bool SimpleActiveOutboundNormalVisible
        => !string.IsNullOrEmpty(SimpleActiveOutboundLine) && !SimpleActiveOutboundIsSuspect;

    public bool SimpleActiveOutboundSuspectVisible
        => !string.IsNullOrEmpty(SimpleActiveOutboundLine) && SimpleActiveOutboundIsSuspect;

    private static bool IsServerKnown(string ip, AppSettings settings)
    {
        bool MatchSet(IEnumerable<VlessServerEntry>? list)
        {
            if (list == null) return false;
            foreach (var s in list)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server)
                    && string.Equals(s.Server.Trim(), ip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        var legacy = settings.Vless?.Server;
        if (!string.IsNullOrWhiteSpace(legacy)
            && string.Equals(legacy!.Trim(), ip, StringComparison.OrdinalIgnoreCase))
            return true;

        if (MatchSet(settings.Vless?.Servers)) return true;
        if (MatchSet(settings.App?.SubscriptionServers)) return true;

        var subs = settings.App?.Subscriptions;
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (MatchSet(sub?.Servers)) return true;
            }
        }

        return false;
    }

    public string SimpleCtaText => IsConnecting
        ? Strings.SmpCtaCancel
        : IsConnected
            ? Strings.SmpCtaDisconnect
            : Strings.SmpCtaConnect;

    public bool SimpleCtaIsConnected    => IsConnected && !IsConnecting;
    public bool SimpleCtaIsConnecting   => IsConnecting;
    public bool SimpleCtaIsDisconnected => !IsConnected && !IsConnecting;

    [RelayCommand]
    private void OpenConfigPicker()
    {
        SmpFormExpanded = !SmpFormExpanded;
    }

    [RelayCommand]
    private async Task SmpToggleConnectAsync()
    {
        SmpErrorText = string.Empty;

        if (IsConnected)
        {
            await ToggleConnectionAsync();
            return;
        }

        if (IsConnecting) return;
        IsConnecting = true;
        try
        {
            var kind = SimpleInputDetector.Classify(SmpInput);

            var hasExistingConfig =
                (_settings.Vless.Servers?.Count > 0) ||
                (_settings.App.Subscriptions?.Any(s => s.Enabled && s.Servers.Count > 0) == true);

            if (kind == SmpInputKind.Invalid)
            {
                if (hasExistingConfig)
                {
                }
                else
                {
                    SmpErrorText = IsRussian
                        ? "Вставь ссылку (vless:// / hysteria2:// / tuic:// / ss:// / naive://) или URL подписки (http:// / https://)."
                        : "Paste a server link (vless:// / hysteria2:// / tuic:// / ss:// / naive://) or a subscription URL (http:// / https://).";
                    IsConnecting = false;
                    return;
                }
            }
            else if (kind == SmpInputKind.ServerUri)
            {
                if (!TryApplyVless(SmpInput.Trim()))
                {
                    IsConnecting = false;
                    return;
                }
            }
            else if (kind == SmpInputKind.SubscriptionUrl)
            {
                if (!TryApplySubscriptionUrl(SmpInput.Trim()))
                {
                    IsConnecting = false;
                    return;
                }
            }

            _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";

            if (IsSplitTunnel)
                _settings.ActiveProfile = SimpleSplitProfile;

            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            if (kind == SmpInputKind.SubscriptionUrl)
            {
                try
                {
                    await RefreshAllSubscriptionsAsync();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[Simple] Subscription refresh failed");
                    SmpErrorText = IsRussian
                        ? $"Не удалось получить подписку: {CrashReporter.ScrubSecrets(ex.Message)}"
                        : $"Couldn't fetch the subscription: {CrashReporter.ScrubSecrets(ex.Message)}";
                    IsConnecting = false;
                    return;
                }
            }

            if ((_settings.App.ConfigMode ?? "generated")
                .Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = (_settings.App.Subscriptions
                        ?? new System.Collections.Generic.List<SubscriptionEntry>())
                    .Where(s => s.Enabled)
                    .SelectMany(s => s.Servers ?? Enumerable.Empty<VlessServerEntry>())
                    .ToList();

                if (candidates.Count > 0)
                {
                    StatusText = IsRussian ? "Подбираем рабочий сервер…" : "Finding a working server…";
                    try
                    {
                        var results = await new ServerHealthProbe(_logger)
                            .ProbeAllAsync(candidates, TimeSpan.FromSeconds(4));
                        var chosen = ConnectionIntentScorer.PickServer(
                            results,
                            _settings.App.ConnectionIntent,
                            _settings.App.ActiveSubscriptionServer);

                        if (chosen == null)
                        {
                            SmpErrorText = IsRussian
                                ? "Все серверы недоступны — проверь подписку или интернет."
                                : "All servers are unreachable — check your subscription or internet.";
                            IsConnecting = false;
                            return;
                        }

                        if (!string.Equals(chosen.Name, _settings.App.ActiveSubscriptionServer, StringComparison.Ordinal))
                        {
                            _logger.Information(
                                "[SmartConnect] active server unreachable/unset — switching to live '{Name}'", chosen.Name);
                            _settings.App.ActiveSubscriptionServer = chosen.Name ?? _settings.App.ActiveSubscriptionServer;

                            var winnerVm = SubscriptionServers.FirstOrDefault(s => s.Name == chosen.Name);
                            if (winnerVm is not null)
                                SelectedSubscriptionServer = winnerVm;

                            SaveSettings();
                        }

                        if (ConnectionIntent.Normalize(_settings.App.ConnectionIntent) != ConnectionIntent.General)
                        {
                            StatusText = $"{ConnectionIntentStatusText} -> {chosen.Name}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[SmartConnect] probe failed — connecting without pre-flight");
                    }
                }
            }

            IsConnecting = false;
            await ToggleConnectionAsync();
        }
        catch
        {
            IsConnecting = false;
            throw;
        }
    }

    private bool TryApplyVless(string uri)
    {
        try
        {
            if (uri.StartsWith("naive", StringComparison.OrdinalIgnoreCase) &&
                !ServerUriParser.NaiveRuntimeAvailable)
            {
                SmpErrorText = IsRussian
                    ? "NaiveProxy работает только на Windows и Linux (нужен libcronet)."
                    : "NaiveProxy works only on Windows and Linux (needs libcronet).";
                return false;
            }

            var entry = ServerUriParser.Parse(uri);

            _settings.Vless.Servers = new List<VlessServerEntry> { entry };
            _settings.Vless.ActiveServer = entry.Name ?? string.Empty;

            Servers.Clear();
            var vm = new ServerViewModel(entry);
            Servers.Add(vm);
            SelectedServer = vm;

            _settings.App.ConfigMode = "generated";
            _settings.App.ActiveSubscriptionServer = string.Empty;
            IsSubscribeMode = false;
            IsVlessMode = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Simple] Server URI parse failed");
            SmpErrorText = IsRussian
                ? "Некорректная ссылка. Поддерживаются vless:// / hysteria2:// / tuic:// / ss:// / naive://, должна заканчиваться '#имя'."
                : "Invalid server link. Supported: vless:// / hysteria2:// / tuic:// / ss:// / naive://, must end with '#name'.";
            return false;
        }
    }

    private bool TryApplySubscriptionUrl(string url)
    {
        var entry = new SubscriptionEntry
        {
            Name = "simple",
            Url = url,
            Enabled = true,
            Servers = new List<VlessServerEntry>(),
        };
        _settings.App.Subscriptions = new List<SubscriptionEntry> { entry };

        Subscriptions.Clear();
        Subscriptions.Add(new SubscriptionViewModel(entry));

        _settings.App.ConfigMode = "subscribe";
        IsSubscribeMode = true;
        IsVlessMode = false;
        return true;
    }

    partial void OnIsSplitTunnelChanged(bool value)
    {
        if (_isLoadingUI) return;

        _settings.App.RoutingMode = value ? "split" : "full";
        if (value)
            _settings.ActiveProfile = SimpleSplitProfile;
        SaveSettings();

        MarkRoutingSettingsChanged();

        if (IsSimpleMode && IsConnected && !IsConnecting)
        {
            _ = ApplyPendingChangesInternalAsync(forceRestart: true);
        }
    }

}
