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
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsWarn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(VpnBadgeTooltip))]
    private bool _smpStopUnconfirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(L_SmpConfigRowToggle))]
    private bool _smpFormExpanded;

    public string L_SmpConfigRowToggle => SmpFormExpanded
        ? Core.Localization.Strings.SmpCollapseCategory
        : Core.Localization.Strings.SmpExpandCategory;

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

    public bool SimpleStatusIsOn   => IsConnected && !SimpleStatusIsWarn;
    public bool SimpleStatusIsWarn => IsConnecting || IsApplying || HasConnectionAlert || SmpStopUnconfirmed;
    public bool SimpleStatusIsOff  => !IsConnected && !SimpleStatusIsWarn;

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
                if (DisplayedTunnel == null) return Strings.SmpStatusConnectedNoDetails;
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

    public string SimpleConfigModeSummary => DisplayedTunnel is { } tunnel
        ? Core.Localization.Strings.SmpActiveMode(FormatConfigMode(tunnel.ConfigMode, tunnel.RoutingMode == "split"))
        : IsConnected ? Core.Localization.Strings.SmpModeUnknown : FormatConfigMode(_settings.App.ConfigMode, IsSplitTunnel);

    public string SimplePendingSummary
    {
        get
        {
            var mode = FormatConfigMode(_settings.App.ConfigMode, IsSplitTunnel);
            var (name, _) = EditorServerLabel();
            return Core.Localization.Strings.SmpPendingConfig(string.IsNullOrEmpty(name) ? mode : $"{name} · {mode}");
        }
    }

    private static string FormatConfigMode(string? source, bool split)
    {
        var config = (source ?? "generated").ToLowerInvariant() switch
        {
            "subscribe" => Strings.SmpCfgSubscribe,
            "generated" => Strings.SmpCfgManual,
            _ => Strings.SmpCfgCustom
        };
        return $"{config} · {(split ? Strings.SmpCfgSplit : Strings.SmpCfgFull)}";
    }

    public string SimpleActiveOutboundLine
    {
        get
        {
            if (!IsConnected) return string.Empty;
            var (name, ip) = DeriveConnectedServerLabel();
            if (string.IsNullOrEmpty(ip)) return string.Empty;

            return string.IsNullOrEmpty(name)
                ? $"{Strings.SmpStatusConnectedVia} {ip}"
                : $"{Strings.SmpStatusConnectedVia} {name}@{ip}";
        }
    }

    public bool SimpleActiveOutboundIsSuspect
    {
        get
        {
            var tunnel = DisplayedTunnel;
            if (tunnel?.ConfigMode != "custom") return false;
            var ip = tunnel.Server.Address;
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

    internal SubscriptionServerPreference? SimpleServerPreference =>
        SubscriptionServerPreference.For(_settings.App);

    internal void ConfirmSimpleServer(ServerViewModel row)
    {
        if (row.SubscriptionId is not { } feedId || !SubscriptionServers.Contains(row)) return;
        if (!row.CanSelectInSimpleMode)
        {
            SmpErrorText = Core.Localization.Strings.SmpServerUnsupported;
            return;
        }
        ConfirmSimplePreference(new() { FeedId = feedId, RowKey = SubscriptionEntry.RowKey(row.SourceEntry) }, row);
    }

    internal void ConfirmSimpleCategory(ProtocolGroup category)
    {
        var row = SelectedSubscriptionServer is { } selected && SubscriptionServers.Contains(selected) &&
            selected.ProtocolGroup == category && SubscriptionServerPreference.CanAutoSelect(selected.SourceEntry)
            ? selected : SubscriptionServers.FirstOrDefault(row => row.ProtocolGroup == category &&
                SubscriptionServerPreference.CanAutoSelect(row.SourceEntry));
        if (row != null) ConfirmSimplePreference(SubscriptionServerPreference.InCategory(category), row);
    }

    private void ConfirmSimplePreference(SubscriptionServerPreference preference, ServerViewModel row)
    {
        if (_isLoadingUI || SimpleCtaIsConnecting || _isReconnecting ||
            (IsSimpleMode ? !CanPickSimpleSubscriptionServer : !IsSubscribeMode))
            return;

        if (IsSimpleMode) SmpFormExpanded = false;
        if (SimpleServerPreference == preference && ReferenceEquals(SelectedSubscriptionServer, row)) return;

        IsSubscribeMode = true;
        IsVlessMode = false;
        AutoSelectBestServer = false;
        _settings.App.ConfigMode = "subscribe";
        _settings.App.SubscriptionPreference = preference;
        _updatingServerPickerSelection = true;
        try { SelectedSubscriptionServer = row; }
        finally { _updatingServerPickerSelection = false; }

        if (Subscriptions.FirstOrDefault(feed => feed.Id == row.SubscriptionId) is { } source)
            SmpInput = source.Url;

        SmpErrorText = string.Empty;
        MarkRoutingSettingsChanged();
        RaiseSimpleHomeProps();

        if (IsConnected)
        {
            var label = preference.GetCategory() is { } category ? SimpleCategoryName(category) : row.DisplayName;
            if (IsServiceManagedVpn) WarnServiceManagedReconnect(label);
            else _ = ReconnectAsync(label, ReconnectIntent.Subscription);
            return;
        }

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning("[Simple] Server selection save failed ({ErrorType})", ex.GetType().Name);
            SmpErrorText = Strings.ApplyFailed;
        }
    }

    internal static string SimpleCategoryName(ProtocolGroup group) => group switch
    {
        ProtocolGroup.GamesVoice => Strings.ProtocolUseGamesVoice,
        ProtocolGroup.WebOnly => Strings.ProtocolUseWebOnly,
        ProtocolGroup.LowPing => Strings.ProtocolUseLowLatency,
        ProtocolGroup.Emergency => Strings.ProtocolUseEmergency,
        ProtocolGroup.Fallback => Strings.ProtocolUseFallback,
        ProtocolGroup.StealthWeb => Strings.ProtocolUseStealthWeb,
        ProtocolGroup.WebFallback => Strings.ProtocolUseWebFallback,
        _ => Core.Localization.Strings.SmpDailyCategory,
    };

    internal bool CanPickSimpleSubscriptionServer =>
        string.Equals(_settings.App.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase);

    internal void OpenSimpleApplicationRules()
    {
        if (!IsSimpleMode) return;
        IsSubscribeMode = CanPickSimpleSubscriptionServer;
        IsVlessMode = string.Equals(_settings.App.ConfigMode, "generated", StringComparison.OrdinalIgnoreCase);
        SelectedTabIndex = 3;
        ToggleUiMode();
    }

    // Only the command that raised IsConnecting may release it after pre-flight.
    private int? _simplePreparationGeneration;

    internal Func<IReadOnlyList<VlessServerEntry>, TimeSpan, Task<List<ServerLiveness>>>? SimpleProbe { get; set; }
    internal Func<AppSettings, CancellationToken, bool,
        Task<(Task StartTask, Internals.TwoPhaseStartOutcome Outcome)>>? SimpleTunnelStart { get; set; }

    [RelayCommand]
    private Task SmpToggleConnectAsync() => PrepareSubscriptionConnectionAsync(useSimpleInput: true);

    private async Task PrepareSubscriptionConnectionAsync(bool useSimpleInput)
    {
        if (SimpleCtaIsConnecting || _isReconnecting) return;
        SmpErrorText = string.Empty;

        if (IsConnected)
        {
            await ToggleConnectionAsync();
            return;
        }

        _smpChoiceNote = null;

        if (SimpleConnectRefreshPending) return;
        if (_engine.HasActiveTunnel)
        {
            AdoptRunningEngine();
            return;
        }
        IsConnecting = true;
        var generation = _connectionGeneration;
        _simplePreparationGeneration = generation;
        var input = SmpInput;
        var editorInput = input;
        var revision = _routingSettingsRevision;
        Func<bool>? choiceUnchanged = null;
        bool Preparing() => !_disposed && IsConnecting && !IsConnected && !IsApplying && !_isReconnecting
            && generation == _connectionGeneration;
        try
        {
            var kind = useSimpleInput ? SimpleInputDetector.Classify(input) : SmpInputKind.Invalid;
            var refresh = false;
            if (kind == SmpInputKind.ServerUri)
            {
                if (!TryApplyVless(input.Trim())) return;
            }
            else if (kind == SmpInputKind.SubscriptionUrl)
            {
                var action = ApplySimpleSubscriptionInput(input);
                if (action == SubscriptionInputAction.Disabled)
                {
                    SmpErrorText = SubscriptionDisabledMessage;
                    return;
                }
                refresh = action != SubscriptionInputAction.KeepCached;
            }
            else if (_settings.Vless.Servers is not { Count: > 0 } &&
                _settings.App.Subscriptions?.Any(s => s.Enabled && s.Servers.Count > 0) != true)
            {
                if (SimpleServerPreference != null)
                {
                    SmpErrorText = Core.Localization.Strings.SmpSelectionUnavailable;
                    return;
                }
                SmpErrorText = IsRussian
                    ? "Вставь ссылку (vless:// / vmess:// / hysteria2:// / tuic:// / ss:// / naive://) или URL подписки (http:// / https://)."
                    : "Paste a server link (vless:// / vmess:// / hysteria2:// / tuic:// / ss:// / naive://) or a subscription URL (http:// / https://).";
                return;
            }

            if (useSimpleInput && IsSplitTunnel) _settings.ActiveProfile = SimpleSplitProfile;
            var scoped = SimpleServerPreference != null;
            SaveSettingsFromControls(preserveConfigMode: scoped);
            var submitted = CaptureConnectionSettings();
            revision = _routingSettingsRevision;
            choiceUnchanged = CaptureSimpleChoice(out editorInput, includePresentation: !scoped);
            var selectedSource = submitted.App.Subscriptions.FirstOrDefault(s => s.Enabled && s.Id == submitted.App.ActiveSubscriptionId);
            if (kind == SmpInputKind.SubscriptionUrl && !SimpleConnectPolicy.SameUrl(selectedSource?.Url, input))
                selectedSource = submitted.App.Subscriptions.FirstOrDefault(s => s.Enabled && SimpleConnectPolicy.SameUrl(s.Url, input));
            var sameRegistration = selectedSource?.Id == submitted.App.ActiveSubscriptionId;
            var selectedName = sameRegistration ? submitted.App.ActiveSubscriptionServer : null;
            var selectedKey = sameRegistration ? submitted.App.ActiveSubscriptionRow : null;
            var previous = selectedSource?.FindExactRow(selectedName, selectedKey);
            var previousRoute = previous == null ? null : SubscriptionEntry.RouteKey(previous, selectedSource!.Servers);

            if (refresh)
            {
                var liveRows = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
                var rows = submitted.App.Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url))
                    .Select(s => new SubscriptionViewModel(s)).ToList();
                foreach (var row in liveRows) row.IsRefreshing = true;
                NotifySubscriptionRefreshState();
                try
                {
                    var updates = await FetchSubscriptionRowsAsync(rows, CancellationToken.None);
                    if (!Preparing()) return;
                    if (!PublishSubscriptionRows(rows, updates, submitted))
                    {
                        SmpErrorText = Strings.SyncEmpty;
                        return;
                    }
                }
                finally
                {
                    foreach (var row in liveRows) row.IsRefreshing = false;
                    NotifySubscriptionRefreshState();
                }
                if (!Preparing()) return;
                if (choiceUnchanged())
                {
                    PublishSimpleRefresh(submitted, liveRows, rows);
                    choiceUnchanged = CaptureSimpleChoice(out editorInput, includePresentation: !scoped);
                }
            }

            if (string.Equals(submitted.App.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase))
            {
                var preference = SubscriptionServerPreference.For(submitted.App);
                var candidates = preference != null
                    ? preference.Candidates(submitted.App.Subscriptions).Select(row => row.Server).ToList()
                    : submitted.App.Subscriptions.Where(s => s.Enabled).SelectMany(s => s.Servers).ToList();
                if (candidates.Count == 0)
                {
                    SmpErrorText = preference == null ? Strings.SyncEmpty : Core.Localization.Strings.SmpSelectionUnavailable;
                    return;
                }

                var preferred = preference != null
                    ? preference.Preferred(submitted.App)?.Server
                    : SubscriptionRefreshDiff.FindRegisteredServer(selectedSource, candidates,
                        selectedName, selectedKey, previous, previousRoute);
                if (preference == null && preferred == null && previous != null && (previous.Dnstt != null || !string.IsNullOrEmpty(previous.DetourVia)))
                {
                    SmpErrorText = Strings.SyncEmpty;
                    return;
                }
                preferred ??= candidates[0];
                StatusText = IsRussian ? "Подбираем рабочий сервер…" : "Finding a working server…";
                // A direct probe of an exit cannot judge the selected route through its required hop.
                var chosen = preference?.CategoryName != null
                    ? await PickCategoryServerAsync(candidates, preferred)
                    : preference != null || preferred.Dnstt != null || !string.IsNullOrEmpty(preferred.DetourVia)
                        ? preferred
                        : await PickSimpleServerAsync(candidates.Where(server => server.Dnstt == null).ToList(),
                            preferred, submitted.App.ConnectionIntent, Preparing);
                if (!Preparing() || preference != SimpleServerPreference ||
                    (preference != null && (revision != _routingSettingsRevision || !choiceUnchanged()))) return;
                if (chosen == null)
                {
                    SmpErrorText = IsRussian
                        ? "Все серверы недоступны — проверь подписку или интернет."
                        : "All servers are unreachable — check your subscription or internet.";
                    return;
                }

                var source = selectedSource != null && selectedSource.Servers.Contains(chosen)
                    ? selectedSource : submitted.App.Subscriptions.First(s => s.Enabled && s.Servers.Contains(chosen));
                if (Subscriptions.Any(s => !s.Enabled && s.Id == source.Id))
                {
                    SmpErrorText = SubscriptionDisabledMessage;
                    return;
                }
                if ((chosen.Dnstt != null || !string.IsNullOrEmpty(chosen.DetourVia)) &&
                    new SubscriptionChainSelection(source.Id, source.Url, chosen, SubscriptionEntry.RouteKey(chosen, source.Servers))
                        .Match(source, submitted.App.Subscriptions.Where(feed => feed.Enabled).SelectMany(feed => feed.Servers)) != chosen)
                {
                    SmpErrorText = Strings.SyncEmpty;
                    return;
                }
                var rowKey = SubscriptionEntry.RowKey(chosen);
                submitted.App.ActiveSubscriptionId = source.Id;
                submitted.App.ActiveSubscriptionRow = rowKey;
                submitted.App.ActiveSubscriptionServer = submitted.Vless.ActiveServer = chosen.Name;
                VlessServersResolver.Resolve(submitted);
                if (choiceUnchanged())
                {
                    var winner = SubscriptionServers.FirstOrDefault(row => row.SubscriptionId == source.Id &&
                        row.Name == chosen.Name && SubscriptionEntry.RowKey(row.SourceEntry) == rowKey);
                    if (winner != null) ApplySmartConnectSelection(chosen, winner);
                    if (ConnectionIntent.Normalize(submitted.App.ConnectionIntent) != ConnectionIntent.General)
                        StatusText = $"{ConnectionIntentStatusText} -> {chosen.Name}";
                    choiceUnchanged = CaptureSimpleChoice(out editorInput, includePresentation: !scoped);
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
                if (useSimpleInput && SmpInput != editorInput && (IsSimpleMode ||
                    (IsSubscribeMode && SimpleInputDetector.Classify(SmpInput) == SmpInputKind.SubscriptionUrl) ||
                    (IsVlessMode && SimpleInputDetector.Classify(SmpInput) == SmpInputKind.ServerUri)))
                    ApplyPendingSimpleInput();
                SaveSettingsFromControls(preserveConfigMode: SimpleServerPreference != null);
                HasPendingAppChanges = true;
            }
        }
    }

    private void PublishSimpleRefresh(AppSettings submitted, List<SubscriptionViewModel> liveRows,
        List<SubscriptionViewModel> rows)
    {
        // The editor and the engine must not share mutable servers after publishing the cache.
        _settings.App.SubscriptionPreference = submitted.App.SubscriptionPreference;
        var caches = VpnEngine.CopySessionSettings(submitted).App.Subscriptions
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url));
        foreach (var live in liveRows)
        {
            var refreshed = rows.First(s => s.Id == live.Id && s.Url == live.Url);
            var cache = caches.First(s => s.Id == live.Id && s.Url == live.Url);
            live.UnderlyingEntry.Servers = cache.Servers;
            live.UnderlyingEntry.UserInfo = cache.UserInfo;
            live.LastRefreshedAt = refreshed.LastRefreshedAt;
            live.LastServerCount = refreshed.LastServerCount;
            live.LastRefreshFailed = refreshed.LastRefreshFailed;
        }
        RebuildSubscriptionPool();
        SaveSettingsFromControls(preserveConfigMode: SimpleServerPreference != null);
    }

    private async Task<VlessServerEntry> PickCategoryServerAsync(List<VlessServerEntry> candidates, VlessServerEntry preferred)
    {
        if (candidates.Count < 2) return preferred;

        try
        {
            var probe = SimpleProbe ?? ((servers, timeout) => new ServerHealthProbe(_logger).ProbeAllAsync(servers, timeout));
            var results = await probe(candidates, TimeSpan.FromSeconds(4));
            return ServerHealthProbe.PickBest(results.Where(result => candidates.Contains(result.Server))) ?? preferred;
        }
        catch (Exception)
        {
            _logger.Warning("[SmartConnect] Category probe failed, using the preferred server");
            return preferred;
        }
    }

    private async Task<VlessServerEntry?> PickSimpleServerAsync(List<VlessServerEntry> candidates,
        VlessServerEntry preferred, string intent, Func<bool> preparing)
    {
        try
        {
            var probe = SimpleProbe ?? ((servers, timeout) => new ServerHealthProbe(_logger).ProbeAllAsync(servers, timeout));
            if (SimpleConnectPolicy.ShouldProbeSelectedFirst(preferred.Name, candidates,
                ConnectionIntent.Normalize(intent) == ConnectionIntent.General, preferred))
            {
                if (SimpleConnectPolicy.CannotBeVerifiedByProbe(preferred)) return preferred;
                var result = await probe(new[] { preferred }, TimeSpan.FromSeconds(2));
                if (!preparing()) return null;
                if (SimpleConnectPolicy.KeepSelectedAfterProbe(result) || result.Any(r => r.Alive)) return preferred;
            }
            var results = await probe(candidates, TimeSpan.FromSeconds(4));
            return ConnectionIntentScorer.PickRegisteredServer(results, intent, preferred);
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
        if (action == SubscriptionInputAction.Disabled) return action;
        var currentFeed = SelectedSubscriptionServer?.SubscriptionId ??
            (SimpleServerPreference is { } preference ? preference.FeedId ?? _settings.App.ActiveSubscriptionId : null);
        if (action == SubscriptionInputAction.Replace)
        {
            var entry = new SubscriptionEntry
            {
                Name = "simple",
                Url = input.Trim(),
                Enabled = true,
                Servers = new List<VlessServerEntry>(),
            };
            _settings.App.SubscriptionPreference = null;
            _settings.App.Subscriptions = new List<SubscriptionEntry> { entry };

            Subscriptions.Clear();
            Subscriptions.Add(CreateSubscriptionView(entry));
            RebuildSubscriptionPool();
        }
        else if (!Subscriptions.Any(s => s.Id == currentFeed && SimpleConnectPolicy.SameUrl(s.Url, input)))
        {
            var source = Subscriptions.FirstOrDefault(s => s.Enabled && SimpleConnectPolicy.SameUrl(s.Url, input));
            if (source != null)
            {
                _settings.App.SubscriptionPreference = null;
                SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault(row => row.SubscriptionId == source.Id);
            }
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

    private Func<bool> CaptureSimpleChoice(out string editorInput, bool includePresentation = true)
    {
        var input = editorInput = SmpInput;
        var mode = _settings.App.ConfigMode;
        var intent = ConnectionIntentIndex;
        var preference = SimpleServerPreference;
        var tabs = (IsSimpleMode, SelectedTabIndex, SelectedServerModeIndex, IsSubscribeMode, IsVlessMode, SelectedCustomConfig);
        var selected = SelectedSubscriptionServer;
        var manual = SelectedServer;
        var feeds = Subscriptions.Select(s => (s, s.Url, s.Enabled, s.UnderlyingEntry.Servers)).ToArray();
        return () => SmpInput == input && _settings.App.ConfigMode == mode && ConnectionIntentIndex == intent &&
            preference == SimpleServerPreference &&
            (!includePresentation || tabs == (IsSimpleMode, SelectedTabIndex, SelectedServerModeIndex,
                IsSubscribeMode, IsVlessMode, SelectedCustomConfig)) &&
            ReferenceEquals(selected, SelectedSubscriptionServer) && ReferenceEquals(manual, SelectedServer) &&
            feeds.SequenceEqual(Subscriptions.Select(s => (s, s.Url, s.Enabled, s.UnderlyingEntry.Servers)));
    }

    internal void ApplySmartConnectSelection(VlessServerEntry chosen, ServerViewModel? winnerVm = null)
    {
        var previousServer = _settings.App.ActiveSubscriptionServer;
        winnerVm ??= SubscriptionServers.FirstOrDefault(row => ReferenceEquals(row.SourceEntry, chosen));
        if (winnerVm == null) return;
        if (string.Equals(chosen.Name, previousServer, StringComparison.Ordinal) &&
            ReferenceEquals(SelectedSubscriptionServer, winnerVm)) return;

        _logger.Information("[SmartConnect] Switching to a reachable server");
        if (SimpleServerPreference == null && !string.IsNullOrWhiteSpace(previousServer))
            _smpChoiceNote = Strings.SmpServerSwitchedNote(previousServer);
        _settings.App.ActiveSubscriptionServer = chosen.Name ?? previousServer;
        SelectedSubscriptionServer = winnerVm;
        SaveSettingsFromControls(preserveConfigMode: SimpleServerPreference != null);
    }

    private bool TryApplyVless(string uri)
    {
        var entry = ParseSimpleServerLink(uri, out var error);
        if (entry == null)
        {
            SmpErrorText = error;
            return false;
        }

        _settings.App.SubscriptionPreference = null;
        _settings.Vless.Servers = [entry];
        _settings.Vless.ActiveServer = entry.Name ?? string.Empty;
        RememberSimpleDirectLink(_settings.App, entry, uri);

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

    private VlessServerEntry? ParseSimpleServerLink(string uri, out string error)
    {
        try
        {
            if (uri.StartsWith("naive", StringComparison.OrdinalIgnoreCase) &&
                !ServerUriParser.NaiveRuntimeAvailable)
            {
                error = IsRussian
                    ? "NaiveProxy работает только на Windows и Linux (нужен libcronet)."
                    : "NaiveProxy works only on Windows and Linux (needs libcronet).";
                return null;
            }

            if (uri.StartsWith("dnstt://", StringComparison.OrdinalIgnoreCase) && !SingBoxFeatures.DnsttAvailable)
            {
                error = Strings.SmpDnsttCoreRequired;
                return null;
            }

            error = string.Empty;
            return ServerUriParser.Parse(uri);
        }
        catch (Exception)
        {
            _logger.Warning("[Simple] Server URI parse failed");
            error = Strings.SmpInvalidServerLink;
            return null;
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

    public bool SmpHasRefreshableSubscriptions => Subscriptions.Any(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url));

    public bool SmpSubscriptionsRefreshing => Subscriptions.Any(s => s.Enabled && s.IsRefreshing);

    public bool SmpCanRefreshSubscriptions => !SimpleCtaIsConnecting && !IsTestingServers && !IsDeepTestingServers;

    private bool SimpleConnectRefreshPending =>
        SmpSubscriptionsRefreshing &&
        SimpleConnectPolicy.DecideSubscriptionInput(SmpInput, _settings.App.Subscriptions) == SubscriptionInputAction.KeepAndRefresh;

    public bool SmpCanConnect => !SimpleConnectRefreshPending && (SmpHasConfig || !string.IsNullOrWhiteSpace(SmpInput));

    // Error represents a failed start. Warn covers a connection alert or an unconfirmed stop.
    public bool SmpHeroIsError => !IsConnected && !string.IsNullOrEmpty(SmpErrorText);
    public bool SmpHeroIsWarn => !IsConnecting && !SmpHeroIsError && (SmpStopUnconfirmed || HasConnectionAlert);
    public bool SmpHeroIsOn => SimpleStatusIsOn && !SmpHeroIsError;
    public bool SmpHeroIsBusy => (IsConnecting || IsApplying) && !SmpHeroIsError;
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
            if (SmpHeroIsError) return SmpErrorText;
            if (SmpStopUnconfirmed) return Strings.TunnelStopUnconfirmed;
            if (IsConnecting) return Strings.SmpStatusConnectingHint;
            if (IsApplying) return Strings.SmpStatusApplyingHint;
            if (HasConnectionAlert) return _lastConnectionAlert!.TrimStart('\u26A0', '\uFE0F', ' ').Trim();
            if (SmpNeedsConfig) return Strings.SmpHeroAddConfigHint;
            return SimpleStatusDescription;
        }
    }

    public string SmpConfigName
    {
        get
        {
            if (IsConnected && DisplayedTunnel == null) return Strings.SmpStatusConnectedNoDetails;
            if (!IsConnected && SimpleServerPreference != null && SelectedSubscriptionServer == null)
                return Core.Localization.Strings.SmpSelectionUnavailableTitle;
            var (name, address) = DeriveConnectedServerLabel();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return string.IsNullOrWhiteSpace(address) ? SmpConfigKind : address;
        }
    }

    public ServerViewModel? SmpConfigServer
    {
        get
        {
            if (DisplayedTunnel is { } tunnel)
            {
                var row = DisplayedServer(tunnel);
                if (row == null || tunnel.ConfigMode == "custom") return null;
                var rows = tunnel.ConfigMode == "subscribe" ? SubscriptionServers : Servers;
                return rows.FirstOrDefault(s => s.Port == row.Port && s.Name == row.Name &&
                    string.Equals(s.Server, row.Address, StringComparison.OrdinalIgnoreCase) &&
                    s.SubscriptionId == row.FeedId && SubscriptionEntry.RowKey(s.SourceEntry) == row.RowKey);
            }
            return IsConnected ? null : EditorServerRow(_settings?.App.ConfigMode ?? "generated");
        }
    }

    public bool SmpHasConfigServer => SmpConfigServer is not null;

    public string SmpConfigKind
    {
        get
        {
            var tunnel = DisplayedTunnel;
            var app = _settings?.App;
            var mode = tunnel?.ConfigMode ?? app?.ConfigMode ?? "generated";
            if (IsConnected && tunnel == null) return string.Empty;
            if (mode.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            {
                if (tunnel != null) return $"{Strings.SmpCurrentServer} · {Strings.SmpKindSubscription}";
                if (app != null && SubscriptionServerPreference.For(app) is { } preference)
                {
                    if (preference.Preferred(app) != null)
                        return preference.GetCategory() is { } category
                            ? $"{SimpleCategoryName(category)} · {Core.Localization.Strings.SmpAutoInCategory}"
                            : Core.Localization.Strings.SmpPinnedServer;
                }
                var count = SubscriptionServers.Count;
                return count > 0 ? $"{Strings.SmpKindSubscription} · {Strings.SmpKindServers(count)}" : Strings.SmpKindSubscription;
            }
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
        nameof(SmpFirstRunEditorVisible), nameof(SmpPickerVisible), nameof(SmpHasSubscriptionServers), nameof(L_SmpConfigRowToggle),
        nameof(SmpHasRefreshableSubscriptions), nameof(SmpSubscriptionsRefreshing), nameof(SmpCanRefreshSubscriptions),
        nameof(SmpHeroIsError), nameof(SmpHeroIsWarn), nameof(SmpHeroIsOn), nameof(SmpHeroIsBusy), nameof(SmpHeroIsIdle),
        nameof(SmpHomeTitle), nameof(SmpHomeSubline), nameof(SmpConfigName), nameof(SmpConfigKind),
        nameof(SmpConfigServer), nameof(SmpHasConfigServer), nameof(SimpleConfigModeSummary), nameof(SimplePendingSummary),
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
                case nameof(IsTestingServers):
                case nameof(IsDeepTestingServers):
                case nameof(HasPendingAppChanges):
                case nameof(IsSplitTunnel):
                case nameof(SimpleStatusTitle):
                case nameof(SimpleStatusDescription):
                case nameof(SmpErrorText):
                case nameof(SmpStopUnconfirmed):
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
