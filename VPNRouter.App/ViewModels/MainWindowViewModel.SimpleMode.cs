using System.Threading;
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
        Interlocked.Increment(ref _connectionGeneration);
        if (!value) _simplePreparationGeneration = null;
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

    // Only the command that raised IsConnecting may release it after pre-flight.
    private int? _simplePreparationGeneration;

    internal Func<IReadOnlyList<VlessServerEntry>, TimeSpan, Task<List<ServerLiveness>>>? SimpleProbe { get; set; }
    internal Func<AppSettings, CancellationToken, bool,
        Task<(Task StartTask, Internals.TwoPhaseStartOutcome Outcome)>>? SimpleTunnelStart { get; set; }

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
        if (_engine.HasActiveTunnel)
        {
            AdoptRunningEngine();
            return;
        }
        IsConnecting = true;
        var generation = _connectionGeneration;
        _simplePreparationGeneration = generation;
        var input = SmpInput;
        var revision = _routingSettingsRevision;
        Func<bool>? choiceUnchanged = null;
        bool Preparing() => !_disposed && IsConnecting && !IsConnected && !IsApplying && !_isReconnecting
            && generation == _connectionGeneration;
        try
        {
            var kind = SimpleInputDetector.Classify(input);
            var refresh = false;
            if (kind == SmpInputKind.ServerUri)
            {
                if (!TryApplyVless(input.Trim())) return;
            }
            else if (kind == SmpInputKind.SubscriptionUrl)
            {
                var action = ApplySimpleSubscriptionInput(input);
                refresh = action != SubscriptionInputAction.KeepCached;
            }
            else if (_settings.Vless.Servers is not { Count: > 0 } &&
                _settings.App.Subscriptions?.Any(s => s.Enabled && s.Servers.Count > 0) != true)
            {
                SmpErrorText = IsRussian
                    ? "Вставь ссылку (vless:// / hysteria2:// / tuic:// / ss:// / naive://) или URL подписки (http:// / https://)."
                    : "Paste a server link (vless:// / hysteria2:// / tuic:// / ss:// / naive://) or a subscription URL (http:// / https://).";
                return;
            }

            if (IsSplitTunnel) _settings.ActiveProfile = SimpleSplitProfile;
            SaveSettings();
            var submitted = CaptureBusinessConnectionSettings();
            revision = _routingSettingsRevision;
            choiceUnchanged = CaptureSimpleChoice();
            var selected = SelectedSubscriptionServer;
            var selectedIndex = selected == null ? -1 : SubscriptionServers.IndexOf(selected);
            var preference = (Uuid: selected?.Uuid, Server: selected?.Server, Port: selected?.Port ?? 0);

            if (refresh)
            {
                var liveRows = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
                var rows = submitted.App.Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url))
                    .Select(s => new SubscriptionViewModel(s)).ToList();
                foreach (var row in liveRows) row.IsRefreshing = true;
                try { await RefreshSubscriptionRowsAsync(rows); }
                finally { foreach (var row in liveRows) row.IsRefreshing = false; }
                if (!Preparing()) return;
                if (choiceUnchanged())
                {
                    PublishSimpleRefresh(submitted, liveRows, rows);
                    choiceUnchanged = CaptureSimpleChoice();
                }
            }

            if (string.Equals(submitted.App.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = submitted.App.Subscriptions.Where(s => s.Enabled).SelectMany(s => s.Servers).ToList();
                if (candidates.Count == 0)
                {
                    SmpErrorText = Strings.SyncEmpty;
                    return;
                }

                var preferred = !refresh && selectedIndex >= 0 && selectedIndex < candidates.Count
                    ? candidates[selectedIndex]
                    : SubscriptionRefreshDiff.FindActiveServer(candidates, submitted.App.ActiveSubscriptionServer,
                        preference.Uuid, preference.Server, preference.Port) ?? candidates[0];
                StatusText = IsRussian ? "Подбираем рабочий сервер…" : "Finding a working server…";
                var chosen = await PickSimpleServerAsync(candidates, preferred, submitted.App.ConnectionIntent, Preparing);
                if (!Preparing()) return;
                if (chosen == null)
                {
                    SmpErrorText = IsRussian
                        ? "Все серверы недоступны — проверь подписку или интернет."
                        : "All servers are unreachable — check your subscription or internet.";
                    return;
                }

                var chosenIndex = candidates.IndexOf(chosen);
                SelectSubmittedSubscriptionServer(submitted, candidates, chosen);
                if (choiceUnchanged())
                {
                    ApplySmartConnectSelection(chosen, SubscriptionServers.ElementAtOrDefault(chosenIndex));
                    if (ConnectionIntent.Normalize(submitted.App.ConnectionIntent) != ConnectionIntent.General)
                        StatusText = $"{ConnectionIntentStatusText} -> {chosen.Name}";
                    choiceUnchanged = CaptureSimpleChoice();
                }
            }

            if (!Preparing()) return;
            IsConnecting = false;
            await ToggleConnectionCoreAsync(submitted);
        }
        finally
        {
            if (_simplePreparationGeneration == generation) IsConnecting = false;
            if (!_disposed && choiceUnchanged != null && (revision != _routingSettingsRevision || !choiceUnchanged()))
            {
                if (SmpInput != input) ApplyPendingSimpleInput();
                SaveSettings();
                HasPendingAppChanges = true;
            }
        }
    }

    private void PublishSimpleRefresh(AppSettings submitted, List<SubscriptionViewModel> liveRows,
        List<SubscriptionViewModel> rows)
    {
        // The editor and the engine must not share mutable servers after publishing the cache.
        var caches = VpnEngine.CopySessionSettings(submitted).App.Subscriptions
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url));
        foreach (var (live, refreshed, cache) in liveRows.Zip(rows, caches))
        {
            live.UnderlyingEntry.Servers = cache.Servers;
            live.UnderlyingEntry.UserInfo = cache.UserInfo;
            live.LastRefreshedAt = refreshed.LastRefreshedAt;
            live.LastServerCount = refreshed.LastServerCount;
            live.LastRefreshFailed = refreshed.LastRefreshFailed;
        }
        RebuildSubscriptionPool();
        SaveSettings();
    }

    private async Task<VlessServerEntry?> PickSimpleServerAsync(List<VlessServerEntry> candidates,
        VlessServerEntry preferred, string intent, Func<bool> preparing)
    {
        try
        {
            var probe = SimpleProbe ?? ((servers, timeout) => new ServerHealthProbe(_logger).ProbeAllAsync(servers, timeout));
            if (SimpleConnectPolicy.ShouldProbeSelectedFirst(preferred.Name, candidates,
                ConnectionIntent.Normalize(intent) == ConnectionIntent.General))
            {
                if (SimpleConnectPolicy.CannotBeVerifiedByProbe(preferred)) return preferred;
                var result = await probe(new[] { preferred }, TimeSpan.FromSeconds(2));
                if (!preparing()) return null;
                if (SimpleConnectPolicy.KeepSelectedAfterProbe(result) || result.Any(r => r.Alive)) return preferred;
            }
            var results = await probe(candidates, TimeSpan.FromSeconds(4));
            return ConnectionIntentScorer.PickServer(results, intent, preferred.Name);
        }
        catch (Exception)
        {
            _logger.Warning("[SmartConnect] Probe failed, connecting without pre-flight");
            return preferred;
        }
    }

    private SubscriptionInputAction ApplySimpleSubscriptionInput(string input)
    {
        var action = SimpleConnectPolicy.DecideSubscriptionInput(input, _settings.App.Subscriptions);
        if (action == SubscriptionInputAction.Replace)
        {
            var entry = new SubscriptionEntry
            {
                Name = "simple",
                Url = input.Trim(),
                Enabled = true,
                Servers = new List<VlessServerEntry>(),
            };
            _settings.App.Subscriptions = new List<SubscriptionEntry> { entry };

            Subscriptions.Clear();
            Subscriptions.Add(new SubscriptionViewModel(entry));
            RebuildSubscriptionPool();
        }
        _settings.App.ConfigMode = "subscribe";
        IsSubscribeMode = true;
        IsVlessMode = false;
        return action;
    }

    private void ApplyPendingSimpleInput()
    {
        // Materialize the next choice without triggering a reconnect or fetching the draft URL.
        var loading = _isLoadingUI;
        _isLoadingUI = true;
        try
        {
            var input = SmpInput.Trim();
            if (SimpleInputDetector.Classify(input) == SmpInputKind.ServerUri)
            {
                var submissionError = SmpErrorText;
                if (!TryApplyVless(input)) SmpErrorText = submissionError;
            }
            else if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                ApplySimpleSubscriptionInput(input);
        }
        finally { _isLoadingUI = loading; }
    }

    private Func<bool> CaptureSimpleChoice()
    {
        var input = SmpInput;
        var mode = _settings.App.ConfigMode;
        var intent = ConnectionIntentIndex;
        var tabs = (IsSubscribeMode, IsVlessMode, SelectedCustomConfig);
        var selected = SelectedSubscriptionServer;
        var manual = SelectedServer;
        var feeds = Subscriptions.Select(s => (s, s.Url, s.Enabled, s.UnderlyingEntry.Servers)).ToArray();
        return () => SmpInput == input && _settings.App.ConfigMode == mode && ConnectionIntentIndex == intent &&
            tabs == (IsSubscribeMode, IsVlessMode, SelectedCustomConfig) &&
            ReferenceEquals(selected, SelectedSubscriptionServer) && ReferenceEquals(manual, SelectedServer) &&
            feeds.SequenceEqual(Subscriptions.Select(s => (s, s.Url, s.Enabled, s.UnderlyingEntry.Servers)));
    }

    private static void SelectSubmittedSubscriptionServer(AppSettings submitted, List<VlessServerEntry> candidates,
        VlessServerEntry chosen)
    {
        submitted.App.ActiveSubscriptionServer = submitted.Vless.ActiveServer = chosen.Name;
        var firstMatch = candidates.First(s => string.Equals(s.Name, chosen.Name, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(firstMatch, chosen))
        {
            // The engine rebuilds the pool from enabled feeds and resolves the active name in order.
            var feeds = submitted.App.Subscriptions;
            var source = feeds.First(s => s.Enabled && s.Servers.Contains(chosen));
            source.Servers.Remove(chosen);
            source.Servers.Insert(0, chosen);
            feeds.Remove(source);
            feeds.Insert(0, source);
            candidates = feeds.Where(s => s.Enabled).SelectMany(s => s.Servers).ToList();
        }
        submitted.Vless.Servers = candidates;
    }

    internal void ApplySmartConnectSelection(VlessServerEntry chosen, ServerViewModel? winnerVm = null)
    {
        var previousServer = _settings.App.ActiveSubscriptionServer;
        winnerVm ??= SubscriptionServers.FirstOrDefault(s => s.Name == chosen.Name && s.Port == chosen.Port &&
            s.Uuid == chosen.Uuid && string.Equals(s.Server, chosen.Server, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(chosen.Name, previousServer, StringComparison.Ordinal) &&
            ReferenceEquals(SelectedSubscriptionServer, winnerVm)) return;

        _logger.Information("[SmartConnect] Switching to a reachable server");
        if (!string.IsNullOrWhiteSpace(previousServer))
            _smpChoiceNote = Strings.SmpServerSwitchedNote(previousServer);
        _settings.App.ActiveSubscriptionServer = chosen.Name ?? previousServer;
        if (winnerVm is not null)
            SelectedSubscriptionServer = winnerVm;
        SaveSettings();
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
        catch (Exception)
        {
            _logger.Warning("[Simple] Server URI parse failed");
            SmpErrorText = IsRussian
                ? "Некорректная ссылка. Поддерживаются vless:// / hysteria2:// / tuic:// / ss:// / naive://, должна заканчиваться '#имя'."
                : "Invalid server link. Supported: vless:// / hysteria2:// / tuic:// / ss:// / naive://, must end with '#name'.";
            return false;
        }
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

    public ServerViewModel? SmpConfigServer
    {
        get
        {
            var (name, _) = DeriveConnectedServerLabel();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return SubscriptionServers.FirstOrDefault(s =>
                       string.Equals(s.DisplayName, name, StringComparison.Ordinal))
                   ?? Servers.FirstOrDefault(s =>
                       string.Equals(s.DisplayName, name, StringComparison.Ordinal));
        }
    }

    public bool SmpHasConfigServer => SmpConfigServer is not null;

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
        nameof(SmpConfigServer), nameof(SmpHasConfigServer),
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

        if (IsSimpleMode && IsConnected && !IsConnecting && !_suppressRoutingAutoApply)
        {
            _ = ApplyPendingChangesInternalAsync(forceRestart: true);
        }
    }

}
