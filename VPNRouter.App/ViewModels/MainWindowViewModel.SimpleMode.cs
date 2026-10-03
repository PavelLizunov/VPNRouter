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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(L_SmpConfigRowToggle))]
    private bool _smpFormExpanded;

    public string L_SmpConfigRowToggle => SmpFormExpanded ? Strings.SmpConfigRowHide : Strings.SmpConfigRowChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(L_FcSettingsToggle))]
    private bool _fcSettingsExpanded;

    public string L_FcSettingsToggle => FcSettingsExpanded ? Strings.SectionHide : Strings.SectionShow;
    public string L_FcSettingsLabel => UiIcons.StripSymbols(Strings.FcAdvancedSettings);
    public string L_SrvManualEmptyHint => Strings.SrvManualEmptyHint;
    public string L_SrvEmptyTitle => Strings.SrvEmptyTitle;
    public string L_SrvEmptyBody => Strings.SrvEmptyBody;
    public string L_SubsEmptyTitle => Strings.SubsEmptyTitle;
    public string L_SubsEmptyBody => Strings.SubsEmptyBody;
    public string L_AddSubscriptionTitle => VPNRouter.Core.Localization.Strings.AdvSubscribeAddSubscription;

    [RelayCommand]
    private void ToggleFcSettings() => FcSettingsExpanded = !FcSettingsExpanded;

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

    // Why the connected server is not the one that was selected (set by the pre-flight, cleared on the next Connect).
    private string? _smpChoiceNote;

    private bool HasConnectionAlert => !string.IsNullOrEmpty(_lastConnectionAlert);

    private void RaiseSimpleAlertProps()
    {
        RaiseSimpleHomeProps();
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

    public bool SimpleStatusIsOn   => IsConnected && !IsConnecting && !IsApplying && !HasConnectionAlert;
    public bool SimpleStatusIsWarn => IsConnecting || IsApplying || HasConnectionAlert;
    public bool SimpleStatusIsOff  => !IsConnected && !IsConnecting && !IsApplying && !HasConnectionAlert;

    public string SimpleStatusTitle => IsConnecting
        ? Strings.SmpStatusConnecting
        : IsApplying
            ? Strings.SmpStatusApplying
        : (IsConnected && !HasConnectionAlert)
            ? Strings.SmpStatusProtected
            : Strings.SmpStatusNotConnected;

    public string SimpleStatusDescription
    {
        get
        {
            if (IsConnecting) return Strings.SmpStatusConnectingHint;
            if (IsApplying) return Strings.SmpStatusApplyingHint;
            if (HasConnectionAlert) return _lastConnectionAlert!;
            if (IsConnected)
            {
                var (name, ip) = DeriveConnectedServerLabel();
                var via = Strings.SmpStatusConnectedVia;
                var note = string.IsNullOrEmpty(_smpChoiceNote) ? string.Empty : $"\n{_smpChoiceNote}";
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ip)) return $"{via} {name} · {ip}{note}";
                if (!string.IsNullOrEmpty(name)) return $"{via} {name}{note}";
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
        : IsApplying
            ? Strings.SmpCtaWait
        : IsConnected
            ? Strings.SmpCtaDisconnect
            : Strings.SmpCtaConnect;

    public bool SimpleCtaIsConnected    => IsConnected && !IsConnecting && !IsApplying;
    public bool SimpleCtaIsConnecting   => IsConnecting || IsApplying;
    public bool SimpleCtaIsDisconnected => !IsConnected && !IsConnecting && !IsApplying;

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

        _smpChoiceNote = null;

        if (IsConnecting || IsApplying || _isReconnecting) return;
        if (_engine.IsRunning)
        {
            AdoptRunningEngine();
            return;
        }
        IsConnecting = true;
        try
        {
            var kind = SimpleInputDetector.Classify(SmpInput);
            var subscriptionAction = SubscriptionInputAction.Replace;

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
                subscriptionAction = SimpleConnectPolicy.DecideSubscriptionInput(SmpInput, _settings.App.Subscriptions);
                if (subscriptionAction == SubscriptionInputAction.Replace)
                {
                    if (!TryApplySubscriptionUrl(SmpInput.Trim()))
                    {
                        IsConnecting = false;
                        return;
                    }
                }
                else
                {
                    _logger.Information("[Simple] Subscription URL unchanged ({Action}) - keeping the saved servers", subscriptionAction);
                    _settings.App.ConfigMode = "subscribe";
                }
            }

            _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";

            if (IsSplitTunnel)
                _settings.ActiveProfile = SimpleSplitProfile;

            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            if (kind == SmpInputKind.SubscriptionUrl && subscriptionAction != SubscriptionInputAction.KeepCached)
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
                        var generalIntent = ConnectionIntent.Normalize(_settings.App.ConnectionIntent) == ConnectionIntent.General;
                        var probe = new ServerHealthProbe(_logger);
                        List<ServerLiveness> results;
                        VlessServerEntry? keptSelection = null;
                        if (SimpleConnectPolicy.ShouldProbeSelectedFirst(_settings.App.ActiveSubscriptionServer, candidates, generalIntent))
                        {
                            var selected = candidates.First(c => string.Equals(c.Name, _settings.App.ActiveSubscriptionServer, StringComparison.Ordinal));
                            results = await probe.ProbeAllAsync(new[] { selected }, TimeSpan.FromSeconds(2));
                            if (SimpleConnectPolicy.KeepSelectedAfterProbe(results))
                            {
                                _logger.Information("[SmartConnect] '{Name}' cannot be probed (protocol or address family) — keeping the selected server", selected.Name);
                                keptSelection = selected;
                            }
                            else if (results.Count == 0 || !results[0].Alive)
                                results = await probe.ProbeAllAsync(candidates, TimeSpan.FromSeconds(4));
                        }
                        else
                        {
                            results = await probe.ProbeAllAsync(candidates, TimeSpan.FromSeconds(4));
                        }
                        var chosen = keptSelection ?? ConnectionIntentScorer.PickServer(
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
                            var previousServer = _settings.App.ActiveSubscriptionServer;
                            if (!string.IsNullOrWhiteSpace(previousServer))
                                _smpChoiceNote = Strings.SmpServerSwitchedNote(previousServer);
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

    // Home screen (Simple page) state. Read-only projections of the state above; the page binds to these so that it
    // can show one status emblem, one sub-line and the right block for a fresh install without its own logic.
    public bool SmpHasConfig =>
        Servers.Count > 0 || SubscriptionServers.Count > 0 || Subscriptions.Count > 0 || CustomConfigs.Count > 0;

    public bool SmpNeedsConfig => !SmpHasConfig && !IsConnected && !IsConnecting;

    public bool SmpConfigEditorVisible => SmpNeedsConfig || SmpFormExpanded;

    // The first-run step above the button, and the "Change" panel inside the connection card.
    public bool SmpFirstRunEditorVisible => SmpNeedsConfig;

    public bool SmpPickerVisible => SmpFormExpanded && SmpHasConfig;

    public bool SmpHasSubscriptionServers => SubscriptionServers.Count > 0;

    public bool SmpCanConnect => SmpHasConfig || !string.IsNullOrWhiteSpace(SmpInput);

    // Error: the last connect attempt failed (SmpErrorText). Warn: the tunnel reported a problem while up (failover
    // alert). On / busy / idle are the plain connection states.
    public bool SmpHeroIsError => !IsConnecting && !string.IsNullOrEmpty(SmpErrorText);
    public bool SmpHeroIsWarn => !IsConnecting && !SmpHeroIsError && HasConnectionAlert;
    public bool SmpHeroIsOn => SimpleStatusIsOn && !SmpHeroIsError;
    public bool SmpHeroIsBusy => IsConnecting || IsApplying;
    public bool SmpHeroIsIdle => !SmpHeroIsOn && !SmpHeroIsBusy && !SmpHeroIsError && !SmpHeroIsWarn;

    public string SmpHomeTitle => SmpHeroIsError
        ? Strings.SmpHeroErrorTitle
        : SmpHeroIsWarn
            ? Strings.SmpHeroWarnTitle
            : SmpNeedsConfig
                ? Strings.SmpHeroAddConfigTitle
                : SimpleStatusTitle;

    public string SmpHomeSubline
    {
        get
        {
            if (IsConnecting) return Strings.SmpStatusConnectingHint;
            if (IsApplying) return Strings.SmpStatusApplyingHint;
            if (!string.IsNullOrEmpty(SmpErrorText)) return SmpErrorText;
            if (HasConnectionAlert) return _lastConnectionAlert!.TrimStart('\u26A0', '\uFE0F', ' ').Trim();
            if (SmpNeedsConfig) return Strings.SmpHeroAddConfigHint;
            return SimpleStatusDescription;
        }
    }

    public string SmpConfigName
    {
        get
        {
            var (name, _) = DeriveConnectedServerLabel();
            return !string.IsNullOrWhiteSpace(name) ? name! : SmpConfigKind;
        }
    }

    public string SmpConfigKind
    {
        get
        {
            var mode = _settings?.App.ConfigMode ?? "generated";
            if (IsConnected && !mode.Equals("subscribe", StringComparison.OrdinalIgnoreCase) &&
                ActiveServerMatcher.FindName(_engine.ActiveServerAddress, SubscriptionServers.Select(s => (s.DisplayName, s.Server))) is not null)
                mode = "subscribe";
            if (mode.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
                return SubscriptionServers.Count > 0
                    ? $"{Strings.SmpKindSubscription} · {Strings.SmpKindServers(SubscriptionServers.Count)}"
                    : Strings.SmpKindSubscription;
            return mode.Equals("generated", StringComparison.OrdinalIgnoreCase)
                ? Strings.SmpKindServer
                : Strings.SmpKindCustom;
        }
    }

    public string L_SmpHeroChange => Strings.SmpConfigRowChange;
    public string L_SmpCtaWait => Strings.SmpCtaWait;
    public string L_SmpSegSplit => Strings.SmpSegSplit;
    public string L_SmpSegFull => Strings.SmpSegFull;
    public string L_SmpTipSplitHome => Strings.SmpTipSplit;
    public string L_SmpTipFullHome => Strings.SmpTipFull;

    private static readonly string[] SmpHomeProps =
    {
        nameof(SmpHasConfig), nameof(SmpNeedsConfig), nameof(SmpConfigEditorVisible), nameof(SmpCanConnect),
        nameof(SmpFirstRunEditorVisible), nameof(SmpPickerVisible), nameof(SmpHasSubscriptionServers),
        nameof(SmpHeroIsError), nameof(SmpHeroIsWarn), nameof(SmpHeroIsOn), nameof(SmpHeroIsBusy), nameof(SmpHeroIsIdle),
        nameof(SmpHomeTitle), nameof(SmpHomeSubline), nameof(SmpConfigName), nameof(SmpConfigKind),
    };

    private void RaiseSimpleHomeProps()
    {
        foreach (var name in SmpHomeProps)
            OnPropertyChanged(name);
    }

    // Called once from the constructor: the home projections follow the properties and collections they read.
    private void WireSimpleHomeNotifications()
    {
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(IsConnected):
                case nameof(IsConnecting):
                case nameof(IsApplying):
                case nameof(SimpleStatusTitle):
                case nameof(SimpleStatusDescription):
                case nameof(SmpErrorText):
                case nameof(SmpInput):
                case nameof(SmpFormExpanded):
                case nameof(SelectedServer):
                case nameof(SelectedSubscriptionServer):
                case nameof(SelectedCustomConfig):
                case nameof(IsSubscribeMode):
                case nameof(IsVlessMode):
                    RaiseSimpleHomeProps();
                    break;
            }
        };
        Servers.CollectionChanged += (_, _) => RaiseSimpleHomeProps();
        SubscriptionServers.CollectionChanged += (_, _) => RaiseSimpleHomeProps();
        Subscriptions.CollectionChanged += (_, _) => RaiseSimpleHomeProps();
        CustomConfigs.CollectionChanged += (_, _) => RaiseSimpleHomeProps();
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
