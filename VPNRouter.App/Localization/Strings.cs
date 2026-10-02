namespace VPNRouter.App.Localization;

public static class Strings
{
    public static string Lang
    {
        get => global::VPNRouter.Core.Localization.Strings.Lang;
        set => global::VPNRouter.Core.Localization.Strings.Lang = value;
    }
    private static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    public static string OsDisplayName => global::VPNRouter.Core.Localization.Strings.OsDisplayName;

    public static string TabServers => global::VPNRouter.Core.Localization.Strings.TabServers;
    public static string TabApps => global::VPNRouter.Core.Localization.Strings.TabApps;
    public static string TabNetwork => global::VPNRouter.Core.Localization.Strings.TabNetwork;
    public static string TabSettings => global::VPNRouter.Core.Localization.Strings.TabSettings;
    public static string TabZapret => global::VPNRouter.Core.Localization.Strings.TabZapret;
    public static string TabTgWsProxy => global::VPNRouter.Core.Localization.Strings.TabTgWsProxy;

    public static string VlessServers => global::VPNRouter.Core.Localization.Strings.VlessServers;
    public static string CustomConfigJson => global::VPNRouter.Core.Localization.Strings.CustomConfigJson;
    public static string ModeManual => global::VPNRouter.Core.Localization.Strings.ModeManual;
    public static string ModeSubscribe => global::VPNRouter.Core.Localization.Strings.ModeSubscribe;
    public static string ModeCustomConfig => global::VPNRouter.Core.Localization.Strings.ModeCustomConfig;
    public static string SubscribeMode => global::VPNRouter.Core.Localization.Strings.SubscribeMode;
    public static string SubscriptionUrlHint => global::VPNRouter.Core.Localization.Strings.SubscriptionUrlHint;
    public static string SyncButton => global::VPNRouter.Core.Localization.Strings.SyncButton;
    public static string Syncing => global::VPNRouter.Core.Localization.Strings.Syncing;
    public static string SyncComplete(int count) => global::VPNRouter.Core.Localization.Strings.SyncComplete(count);
    public static string SyncFailed(string err) => global::VPNRouter.Core.Localization.Strings.SyncFailed(err);
    public static string SyncEmpty => global::VPNRouter.Core.Localization.Strings.SyncEmpty;
    public static string PasteVlessUri => global::VPNRouter.Core.Localization.Strings.PasteVlessUri;

    public static string StartVPN => global::VPNRouter.Core.Localization.Strings.StartVPN;
    public static string StopVPN => global::VPNRouter.Core.Localization.Strings.StopVPN;
    public static string AddServers => global::VPNRouter.Core.Localization.Strings.AddServers;
    public static string Remove => global::VPNRouter.Core.Localization.Strings.Remove;
    public static string AddConfig => global::VPNRouter.Core.Localization.Strings.AddConfig;
    public static string Apply => global::VPNRouter.Core.Localization.Strings.Apply;
    public static string BtnAdd => global::VPNRouter.Core.Localization.Strings.BtnAdd;
    public static string RemoveChecked => global::VPNRouter.Core.Localization.Strings.RemoveChecked;

    public static string SplitTunnel => global::VPNRouter.Core.Localization.Strings.SplitTunnel;
    public static string FullTunnel => global::VPNRouter.Core.Localization.Strings.FullTunnel;
    public static string AppsHint => global::VPNRouter.Core.Localization.Strings.AppsHint;
    public static string CustomAppLabel => global::VPNRouter.Core.Localization.Strings.CustomAppLabel;
    public static string AddCustomAppBtn => global::VPNRouter.Core.Localization.Strings.AddCustomAppBtn;
    public static string BrowseExe => global::VPNRouter.Core.Localization.Strings.BrowseExe;
    public static string BrowseExeTooltip => global::VPNRouter.Core.Localization.Strings.BrowseExeTooltip;
    public static string RunningProcesses => global::VPNRouter.Core.Localization.Strings.RunningProcesses;
    public static string RunningProcessesTooltip => global::VPNRouter.Core.Localization.Strings.RunningProcessesTooltip;
    public static string SelectExecutableDialogTitle => global::VPNRouter.Core.Localization.Strings.SelectExecutableDialogTitle;
    public static string ExecutableFileFilter => global::VPNRouter.Core.Localization.Strings.ExecutableFileFilter;
    public static string AllFilesFilter => global::VPNRouter.Core.Localization.Strings.AllFilesFilter;
    public static string LoadingProcesses => global::VPNRouter.Core.Localization.Strings.LoadingProcesses;
    public static string NoRunningProcessesFound => global::VPNRouter.Core.Localization.Strings.NoRunningProcessesFound;

    public static string ThemeDark => global::VPNRouter.Core.Localization.Strings.ThemeDark;
    public static string ThemeLight => global::VPNRouter.Core.Localization.Strings.ThemeLight;

    public static string NotConnected => global::VPNRouter.Core.Localization.Strings.NotConnected;
    public static string Connected(string mode, string? serverName, string? serverIp) =>
        global::VPNRouter.Core.Localization.Strings.Connected(mode, serverName, serverIp);

    public static string AutoSelectStatusLabel => Ru ? "авто-выбор" : "auto-select";

    public static string TrueSplitBadge => Ru ? "True split: активен" : "True split: active";
    public static string TrueSplitTooltip => Ru
        ? "Исключённые приложения идут мимо VPN на уровне ОС и переживают перезапуск sing-box.\n" +
          "Ограничения: DNS через svchost может уходить в туннель; localhost-UDP (127.0.0.1) у " +
          "исключённых может ломаться; multicast-приём и UWP/Store-приложения исключить нельзя."
        : "Excluded apps bypass the VPN at the OS level and survive a sing-box restart.\n" +
          "Caveats: DNS via svchost may still tunnel; excluded apps' localhost-UDP (127.0.0.1) may " +
          "break; multicast receive and UWP/Store apps can't be excluded.";
    public static string TrueSplitRetry => Ru ? "Проверить True Split" : "Retry True Split";
    public static string TrueSplitStarting => Ru ? "True Split запускается..." : "True Split is starting...";
    public static string TrueSplitActive => TrueSplitBadge;
    public static string TrueSplitMissing => Ru
        ? "True Split недоступен: драйвер не входит в эту сборку."
        : "True Split unavailable: the driver is not bundled in this build.";
    public static string TrueSplitFallback => Ru
        ? "Обычный split активен; True Split не запустился."
        : "Ordinary split is active; True Split did not start.";
    public static string TrueSplitNoRunningApps => Ru
        ? "True Split не включён: ни одно из исключённых приложений сейчас не запущено. Работает обычный split; True Split включится при следующем подключении, если эти приложения уже запущены."
        : "True Split is off: none of the excluded apps is running right now. Ordinary split is active; True Split engages on the next connect if those apps are already running.";
    public static string TrueSplitDeviceBusy => Ru
        ? "True Split не запустился: split-драйвер занят Amnezia/Mullvad/VPNRouter Service (err=5). VPNRouter не будет останавливать чужой kernel driver автоматически; закройте тот VPN, отключите его split tunneling и перезагрузите Windows."
        : "True Split did not start: the split driver is held by Amnezia/Mullvad/VPNRouter Service (err=5). VPNRouter will not stop another kernel driver automatically; close that VPN, disable its split tunneling, and reboot Windows.";
    public static string TrueSplitServiceManaged => Ru
        ? "VPN запущен службой Windows. True Split контролирует служба; чтобы перезапустить его вручную, остановите VPN и запустите его из приложения."
        : "VPN is running in the Windows Service. True Split is controlled by the service; stop VPN and start it from the app to retry manually.";
    public static string TrueSplitNotApplicable => Ru
        ? "True Split доступен только для списка «Мимо VPN»."
        : "True Split applies only to the bypass list.";

    public static string Starting => global::VPNRouter.Core.Localization.Strings.Starting;
    public static string Stopping => global::VPNRouter.Core.Localization.Strings.Stopping;
    public static string Stopped => global::VPNRouter.Core.Localization.Strings.Stopped;

    public static string BadgeTooltipVpn => global::VPNRouter.Core.Localization.Strings.BadgeTooltipVpn;
    public static string BadgeTooltipZapret => global::VPNRouter.Core.Localization.Strings.BadgeTooltipZapret;
    public static string BadgeTooltipTgProxy => global::VPNRouter.Core.Localization.Strings.BadgeTooltipTgProxy;
    public static string SubscriptionEnterUrl => global::VPNRouter.Core.Localization.Strings.SubscriptionEnterUrl;
    public static string SubscriptionCleared => global::VPNRouter.Core.Localization.Strings.SubscriptionCleared;

    public static string ServerTestCancel => global::VPNRouter.Core.Localization.Strings.ServerTestCancel;
    public static string ServerTestAll => global::VPNRouter.Core.Localization.Strings.ServerTestAll;
    public static string ServerDeepStop => global::VPNRouter.Core.Localization.Strings.ServerDeepStop;
    public static string ServerDeepVerify => global::VPNRouter.Core.Localization.Strings.ServerDeepVerify;
    public static string ServerTestingManual => global::VPNRouter.Core.Localization.Strings.ServerTestingManual;
    public static string ServerTestingSubscriptions => global::VPNRouter.Core.Localization.Strings.ServerTestingSubscriptions;
    public static string ServerTestNoServers => global::VPNRouter.Core.Localization.Strings.ServerTestNoServers;
    public static string ServerTestCancelled => global::VPNRouter.Core.Localization.Strings.ServerTestCancelled;
    public static string PingUnavailableWhenConnected => global::VPNRouter.Core.Localization.Strings.PingUnavailableWhenConnected;
    public static string ServerDeepVerifyManual => global::VPNRouter.Core.Localization.Strings.ServerDeepVerifyManual;
    public static string ServerDeepVerifySubscription => global::VPNRouter.Core.Localization.Strings.ServerDeepVerifySubscription;

    public static string TgProxyStatsActive => global::VPNRouter.Core.Localization.Strings.TgProxyStatsActive;
    public static string TgProxyStatsTotal => global::VPNRouter.Core.Localization.Strings.TgProxyStatsTotal;

    public static string RuleParserMissingValue => global::VPNRouter.Core.Localization.Strings.RuleParserMissingValue;
    public static string RuleParserUnknownType(string type) => global::VPNRouter.Core.Localization.Strings.RuleParserUnknownType(type);
    public static string RulesAllDeleted => global::VPNRouter.Core.Localization.Strings.RulesAllDeleted;
    public static string RulesAlreadySorted => global::VPNRouter.Core.Localization.Strings.RulesAlreadySorted;
    public static string RulesEmptyValue => global::VPNRouter.Core.Localization.Strings.RulesEmptyValue;
    public static string ClickToActivateConfig => global::VPNRouter.Core.Localization.Strings.ClickToActivateConfig;

    public static string RuleActionHintDirect => global::VPNRouter.Core.Localization.Strings.RuleActionHintDirect;
    public static string RuleActionHintProxy => global::VPNRouter.Core.Localization.Strings.RuleActionHintProxy;
    public static string RuleActionHintBlock => global::VPNRouter.Core.Localization.Strings.RuleActionHintBlock;
    public static string RuleTypeHintDomain => global::VPNRouter.Core.Localization.Strings.RuleTypeHintDomain;
    public static string RuleTypeHintDomainSuffix => global::VPNRouter.Core.Localization.Strings.RuleTypeHintDomainSuffix;
    public static string RuleTypeHintDomainKeyword => global::VPNRouter.Core.Localization.Strings.RuleTypeHintDomainKeyword;
    public static string RuleTypeHintIpCidr => global::VPNRouter.Core.Localization.Strings.RuleTypeHintIpCidr;
    public static string RuleTypeHintPort => global::VPNRouter.Core.Localization.Strings.RuleTypeHintPort;
    public static string RuleTypeHintPortRange => global::VPNRouter.Core.Localization.Strings.RuleTypeHintPortRange;
    public static string RuleTypeHintNetwork => global::VPNRouter.Core.Localization.Strings.RuleTypeHintNetwork;
    public static string RuleTypeHintProcessName => global::VPNRouter.Core.Localization.Strings.RuleTypeHintProcessName;
    public static string RuleTypeHintProcessPath => global::VPNRouter.Core.Localization.Strings.RuleTypeHintProcessPath;
    public static string RuleTypeHintGeosite => global::VPNRouter.Core.Localization.Strings.RuleTypeHintGeosite;
    public static string RuleTypeHintGeoip => global::VPNRouter.Core.Localization.Strings.RuleTypeHintGeoip;

    public static string ZapretProbeElapsedAndEta(int elapsedSec, int? etaSec) => global::VPNRouter.Core.Localization.Strings.ZapretProbeElapsedAndEta(elapsedSec, etaSec);
    public static string ZapretStartSelectedStrategyButton => global::VPNRouter.Core.Localization.Strings.ZapretStartSelectedStrategyButton;
    public static string ZapretStartSelectedStrategyHint => global::VPNRouter.Core.Localization.Strings.ZapretStartSelectedStrategyHint;
    public static string ZapretStartingSelected(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretStartingSelected(strategy);
    public static string ZapretRunningSelected(string strategy, int pid) => global::VPNRouter.Core.Localization.Strings.ZapretRunningSelected(strategy, pid);
    public static string ZapretSelectedStrategyFailed(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretSelectedStrategyFailed(strategy);

    public static string ZapretForceFreshProbeButton => global::VPNRouter.Core.Localization.Strings.ZapretForceFreshProbeButton;
    public static string ZapretClearCacheButton => global::VPNRouter.Core.Localization.Strings.ZapretClearCacheButton;
    public static string ZapretCacheCleared => global::VPNRouter.Core.Localization.Strings.ZapretCacheCleared;
    public static string ZapretCacheInfo(string strategy, int successCount) => global::VPNRouter.Core.Localization.Strings.ZapretCacheInfo(strategy, successCount);
    public static string ZapretCacheEmpty => global::VPNRouter.Core.Localization.Strings.ZapretCacheEmpty;

    public static string ZapretSummaryHeaderFresh(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretSummaryHeaderFresh(strategy);
    public static string ZapretSummaryHeaderStale(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretSummaryHeaderStale(strategy);
    public static string ZapretSummarySubtextWithScore(int p, int t, string r) => global::VPNRouter.Core.Localization.Strings.ZapretSummarySubtextWithScore(p, t, r);
    public static string ZapretSummarySubtextNoScore(string r) => global::VPNRouter.Core.Localization.Strings.ZapretSummarySubtextNoScore(r);
    public static string ZapretReverifyButton => global::VPNRouter.Core.Localization.Strings.ZapretReverifyButton;
    public static string ZapretReverifyHint => global::VPNRouter.Core.Localization.Strings.ZapretReverifyHint;
    public static string ZapretSummaryDetailsButton => global::VPNRouter.Core.Localization.Strings.ZapretSummaryDetailsButton;
    public static string ZapretSummaryStaleHint => global::VPNRouter.Core.Localization.Strings.ZapretSummaryStaleHint;
    public static string ZapretCancelProbeButton => global::VPNRouter.Core.Localization.Strings.ZapretCancelProbeButton;
    public static string RelativeTimeJustNow => global::VPNRouter.Core.Localization.Strings.RelativeTimeJustNow;
    public static string RelativeTimeMinutes(int n) => global::VPNRouter.Core.Localization.Strings.RelativeTimeMinutes(n);
    public static string RelativeTimeHours(int n) => global::VPNRouter.Core.Localization.Strings.RelativeTimeHours(n);
    public static string RelativeTimeDays(int n) => global::VPNRouter.Core.Localization.Strings.RelativeTimeDays(n);
    public static string RelativeTimeLongAgo => global::VPNRouter.Core.Localization.Strings.RelativeTimeLongAgo;

    public static string RulesFilePickerOpenFailed => global::VPNRouter.Core.Localization.Strings.RulesFilePickerOpenFailed;
    public static string RulesImportDialogTitle => global::VPNRouter.Core.Localization.Strings.RulesImportDialogTitle;
    public static string RulesExportDialogTitle => global::VPNRouter.Core.Localization.Strings.RulesExportDialogTitle;
    public static string RulesImportFailed(string warning) => global::VPNRouter.Core.Localization.Strings.RulesImportFailed(warning);
    public static string RulesImportNoRules => global::VPNRouter.Core.Localization.Strings.RulesImportNoRules;
    public static string RulesImported(int count, string format) => global::VPNRouter.Core.Localization.Strings.RulesImported(count, format);
    public static string RulesImportWithWarnings(int count) => global::VPNRouter.Core.Localization.Strings.RulesImportWithWarnings(count);
    public static string RulesImportError(string err) => global::VPNRouter.Core.Localization.Strings.RulesImportError(err);
    public static string RulesExportNothing => global::VPNRouter.Core.Localization.Strings.RulesExportNothing;
    public static string RulesExported(int count, string filename) => global::VPNRouter.Core.Localization.Strings.RulesExported(count, filename);
    public static string RulesExportError(string err) => global::VPNRouter.Core.Localization.Strings.RulesExportError(err);

    public static string StartTimeoutPhaseA => global::VPNRouter.Core.Localization.Strings.StartTimeoutPhaseA;
    public static string StartTimeoutPhaseB => global::VPNRouter.Core.Localization.Strings.StartTimeoutPhaseB;

    public static string ColName => global::VPNRouter.Core.Localization.Strings.ColName;
    public static string ColServer => global::VPNRouter.Core.Localization.Strings.ColServer;
    public static string ColPort => global::VPNRouter.Core.Localization.Strings.ColPort;
    public static string ColSecurity => global::VPNRouter.Core.Localization.Strings.ColSecurity;
    public static string ColIp => global::VPNRouter.Core.Localization.Strings.ColIp;
    public static string ColPing => global::VPNRouter.Core.Localization.Strings.ColPing;
    public static string ColPingTooltip => global::VPNRouter.Core.Localization.Strings.ColPingTooltip;

    public static string ProtocolUseDaily => Ru ? "Повседневно" : "Daily";
    public static string ProtocolUseDailyTip => Ru
        ? "Обычный выбор для браузера, приложений и стабильного TCP-трафика."
        : "Default choice for browsing, apps, and stable TCP traffic.";
    public static string ProtocolUseGamesVoice => Ru ? "Игры/звонки" : "Games/voice";
    public static string ProtocolUseGamesVoiceTip => Ru
        ? "UDP-friendly транспорт. Пробуйте для игр, Discord и голосовых звонков."
        : "UDP-friendly transport. Try it for games, Discord, and voice calls.";
    public static string ProtocolUseWebOnly => Ru ? "Только веб" : "Web only";
    public static string ProtocolUseWebOnlyTip => Ru
        ? "Хорош для web/TCP. Для игр и звонков нужен UDP-парный сервер."
        : "Good for web/TCP. Games and calls need a paired UDP server.";
    public static string ProtocolUseWebUdpPair => Ru ? "Веб + UDP" : "Web + UDP";
    public static string ProtocolUseWebUdpPairTip => Ru
        ? "Naive ведёт web/TCP, а парный HY2/TUIC сервер забирает UDP."
        : "Naive handles web/TCP while a paired HY2/TUIC server carries UDP.";
    public static string ProtocolUseLowLatency => Ru ? "Низкий ping" : "Low ping";
    public static string ProtocolUseLowLatencyTip => Ru
        ? "WireGuard/AWG-подобный транспорт. Быстрый, но проверяйте стабильность сети."
        : "WireGuard/AWG-like transport. Fast, but check network stability.";
    public static string ProtocolUseEmergency => Ru ? "Аварийный" : "Emergency";
    public static string ProtocolUseEmergencyTip => Ru
        ? "Последний шанс через DNS-туннель. Обычно медленнее обычных серверов."
        : "Last-resort DNS tunnel. Usually slower than normal servers.";
    public static string ProtocolUseFallback => Ru ? "Запасной" : "Fallback";
    public static string ProtocolUseFallbackTip => Ru
        ? "Совместимый запасной вариант, если основные протоколы не проходят."
        : "Compatibility fallback when primary protocols do not pass.";
    public static string ProtocolUseStealthWeb => Ru ? "Скрытный веб" : "Stealth web";
    public static string ProtocolUseStealthWebTip => Ru
        ? "XHTTP для жёстких сетей и web-трафика. Для игр проверяйте отдельно."
        : "XHTTP for restrictive networks and web traffic. Test games separately.";
    public static string ProtocolUseWebFallback => Ru ? "Веб-резерв" : "Web fallback";
    public static string ProtocolUseWebFallbackTip => Ru
        ? "WebSocket/gRPC вариант для сетей, где обычный TCP хуже проходит."
        : "WebSocket/gRPC fallback for networks where plain TCP works poorly.";

    public static string RoutingDescription => global::VPNRouter.Core.Localization.Strings.RoutingDescription;
    public static string SplitTunnelTitle => global::VPNRouter.Core.Localization.Strings.SplitTunnelTitle;
    public static string SplitTunnelSubtitle => global::VPNRouter.Core.Localization.Strings.SplitTunnelSubtitle;
    public static string FullTunnelTitle => global::VPNRouter.Core.Localization.Strings.FullTunnelTitle;
    public static string FullTunnelSubtitle => global::VPNRouter.Core.Localization.Strings.FullTunnelSubtitle;

    public static string ServiceStatusLabel => global::VPNRouter.Core.Localization.Strings.ServiceStatusLabel;
    public static string ServiceRunningText => global::VPNRouter.Core.Localization.Strings.ServiceRunningText;
    public static string ServiceStoppedText => global::VPNRouter.Core.Localization.Strings.ServiceStoppedText;
    public static string ServiceInstalledText => global::VPNRouter.Core.Localization.Strings.ServiceInstalledText;
    public static string ServiceNotInstalledText => global::VPNRouter.Core.Localization.Strings.ServiceNotInstalledText;

    public static string ServiceMasterTitle => global::VPNRouter.Core.Localization.Strings.ServiceMasterTitle;
    public static string ServiceMasterSubtitle => global::VPNRouter.Core.Localization.Strings.ServiceMasterSubtitle;
    public static string ServiceEnableLabel => global::VPNRouter.Core.Localization.Strings.ServiceEnableLabel;
    public static string ServiceInstalling => global::VPNRouter.Core.Localization.Strings.ServiceInstalling;
    public static string ServiceRemoving => global::VPNRouter.Core.Localization.Strings.ServiceRemoving;
    public static string ServiceComponentsHeader => global::VPNRouter.Core.Localization.Strings.ServiceComponentsHeader;
    public static string ServiceComponentsDisabledHint => global::VPNRouter.Core.Localization.Strings.ServiceComponentsDisabledHint;
    public static string AutostartUiSessionHeader => global::VPNRouter.Core.Localization.Strings.AutostartUiSessionHeader;

    public static string AutostartBootSectionTitle => global::VPNRouter.Core.Localization.Strings.AutostartBootSectionTitle;
    public static string AutostartBootSectionSub => global::VPNRouter.Core.Localization.Strings.AutostartBootSectionSub;
    public static string AutostartComponentsInfoHint => global::VPNRouter.Core.Localization.Strings.AutostartComponentsInfoHint;
    public static string AutostartStatusBoot => global::VPNRouter.Core.Localization.Strings.AutostartStatusBoot;
    public static string AutostartStatusLoginFallback => global::VPNRouter.Core.Localization.Strings.AutostartStatusLoginFallback;
    public static string AutostartStatusNoBoot => global::VPNRouter.Core.Localization.Strings.AutostartStatusNoBoot;
    public static string BtnInstallServiceInlineCta => global::VPNRouter.Core.Localization.Strings.BtnInstallServiceInlineCta;
    public static string TipInstallServiceInlineCta => global::VPNRouter.Core.Localization.Strings.TipInstallServiceInlineCta;
    public static string TipSubscriptionMetadata => global::VPNRouter.Core.Localization.Strings.TipSubscriptionMetadata;
    public static string AutostartLoginSectionTitle => global::VPNRouter.Core.Localization.Strings.AutostartLoginSectionTitle;
    public static string AutostartLoginAppDescription => global::VPNRouter.Core.Localization.Strings.AutostartLoginAppDescription;

    public static string ServiceRunningLine(int pid) => global::VPNRouter.Core.Localization.Strings.ServiceRunningLine(pid);
    public static string ServiceStoppedLine => global::VPNRouter.Core.Localization.Strings.ServiceStoppedLine;

    public static string SmpAutostartCardTitle => global::VPNRouter.Core.Localization.Strings.SmpAutostartCardTitle;
    public static string SmpAutostartCardOn => global::VPNRouter.Core.Localization.Strings.SmpAutostartCardOn;
    public static string SmpAutostartCardOff => global::VPNRouter.Core.Localization.Strings.SmpAutostartCardOff;

    public static string FailedStartVpn => global::VPNRouter.Core.Localization.Strings.FailedStartVpn;
    public static string AddServerFirst => global::VPNRouter.Core.Localization.Strings.AddServerFirst;
    public static string SelectSingBoxConfig => global::VPNRouter.Core.Localization.Strings.SelectSingBoxConfig;
    public static string InvalidConfig => global::VPNRouter.Core.Localization.Strings.InvalidConfig;
    public static string ConfigExists(string name) => global::VPNRouter.Core.Localization.Strings.ConfigExists(name);

    public static string TrayStart => global::VPNRouter.Core.Localization.Strings.TrayStart;
    public static string TrayStop => global::VPNRouter.Core.Localization.Strings.TrayStop;
    public static string TraySettings => global::VPNRouter.Core.Localization.Strings.TraySettings;
    public static string TrayExit => global::VPNRouter.Core.Localization.Strings.TrayExit;

    public static string FieldName => global::VPNRouter.Core.Localization.Strings.FieldName;
    public static string FieldServer => global::VPNRouter.Core.Localization.Strings.FieldServer;
    public static string FieldPort => global::VPNRouter.Core.Localization.Strings.FieldPort;
    public static string FieldUuid => global::VPNRouter.Core.Localization.Strings.FieldUuid;
    public static string FieldPublicKey => global::VPNRouter.Core.Localization.Strings.FieldPublicKey;
    public static string FieldShortId => global::VPNRouter.Core.Localization.Strings.FieldShortId;

    public static string DoubleClickEditServer => global::VPNRouter.Core.Localization.Strings.DoubleClickEditServer;
    public static string DoubleClickActiveConfig => global::VPNRouter.Core.Localization.Strings.DoubleClickActiveConfig;
    public static string AddCustomAppHint => global::VPNRouter.Core.Localization.Strings.AddCustomAppHint;
    public static string TcpUdpHint => global::VPNRouter.Core.Localization.Strings.TcpUdpHint;

    public static string BypassRussianTrafficLabel => global::VPNRouter.Core.Localization.Strings.BypassRussianTrafficLabel;
    public static string BypassRussianTrafficHint => global::VPNRouter.Core.Localization.Strings.BypassRussianTrafficHint;
    public static string CheckLeaks => global::VPNRouter.Core.Localization.Strings.CheckLeaks;
    public static string ShowLogs => global::VPNRouter.Core.Localization.Strings.ShowLogs;

    public static string StrictModeLabel => global::VPNRouter.Core.Localization.Strings.StrictModeLabel;
    public static string StrictModeHint => global::VPNRouter.Core.Localization.Strings.StrictModeHint;
    public static string MtuLabel => global::VPNRouter.Core.Localization.Strings.MtuLabel;
    public static string MtuHint => global::VPNRouter.Core.Localization.Strings.MtuHint;
    public static string MtuWarningLow => global::VPNRouter.Core.Localization.Strings.MtuWarningLow;
    public static string MtuWarningHigh => global::VPNRouter.Core.Localization.Strings.MtuWarningHigh;
    public static string MtuAutoTuneButton => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneButton;
    public static string MtuAutoTuneRunning => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneRunning;
    public static string MtuAutoTuneWindowsOnly => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneWindowsOnly;
    public static string MtuAutoTuneApplied(int mtu) => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneApplied(mtu);
    public static string MtuAutoTuneBlocked => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneBlocked;
    public static string MtuAutoTuneNoResult => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneNoResult;
    public static string MtuAutoTuneTooLow(int mtu) => global::VPNRouter.Core.Localization.Strings.MtuAutoTuneTooLow(mtu);
    public static string ForceIpv4Label => global::VPNRouter.Core.Localization.Strings.ForceIpv4Label;
    public static string FlushDnsLabel => global::VPNRouter.Core.Localization.Strings.FlushDnsLabel;
    public static string StrictDnsLabel => global::VPNRouter.Core.Localization.Strings.StrictDnsLabel;
    public static string DnsLeakLockdownLabel => global::VPNRouter.Core.Localization.Strings.DnsLeakLockdownLabel;
    public static string DnsLeakLockdownUnavailableNote => global::VPNRouter.Core.Localization.Strings.DnsLeakLockdownUnavailableNote;
    public static string TipDnsLeakLockdown => global::VPNRouter.Core.Localization.Strings.TipDnsLeakLockdown;

    public static string CheckForUpdates => global::VPNRouter.Core.Localization.Strings.CheckForUpdates;
    public static string Checking => global::VPNRouter.Core.Localization.Strings.Checking;
    public static string UpToDate => global::VPNRouter.Core.Localization.Strings.UpToDate;
    public static string CheckFailed => global::VPNRouter.Core.Localization.Strings.CheckFailed;
    public static string UpdateAvailableShort => global::VPNRouter.Core.Localization.Strings.UpdateAvailableShort;
    public static string UpdateAvailableMessage => global::VPNRouter.Core.Localization.Strings.UpdateAvailableMessage;
    public static string UpdateButton => global::VPNRouter.Core.Localization.Strings.UpdateButton;
    public static string UpdateDownloading => global::VPNRouter.Core.Localization.Strings.UpdateDownloading;
    public static string UpdateApplying => global::VPNRouter.Core.Localization.Strings.UpdateApplying;
    public static string UpdateRestarting => global::VPNRouter.Core.Localization.Strings.UpdateRestarting;
    public static string UpdateFailed => global::VPNRouter.Core.Localization.Strings.UpdateFailed;
    public static string OtherVersions => global::VPNRouter.Core.Localization.Strings.OtherVersions;
    public static string HideOlderVersions => global::VPNRouter.Core.Localization.Strings.HideOlderVersions;
    public static string LoadingVersions => global::VPNRouter.Core.Localization.Strings.LoadingVersions;
    public static string NoOlderVersions => global::VPNRouter.Core.Localization.Strings.NoOlderVersions;
    public static string VersionHistoryFailed => global::VPNRouter.Core.Localization.Strings.VersionHistoryFailed;
    public static string InstalledVersion => global::VPNRouter.Core.Localization.Strings.InstalledVersion;
    public static string RollbackAction => global::VPNRouter.Core.Localization.Strings.RollbackAction;
    public static string RollbackSafetyHint => global::VPNRouter.Core.Localization.Strings.RollbackSafetyHint;
    public static string RollbackConfirmation => global::VPNRouter.Core.Localization.Strings.RollbackConfirmation;
    public static string ConfirmRollback => global::VPNRouter.Core.Localization.Strings.ConfirmRollback;
    public static string Cancel => global::VPNRouter.Core.Localization.Strings.Cancel;

    public static string SettingsAutosaved => global::VPNRouter.Core.Localization.Strings.SettingsAutosaved;
    public static string ApplyNowReloadVpn => global::VPNRouter.Core.Localization.Strings.ApplyNowReloadVpn;
    public static string ApplyNowHint => global::VPNRouter.Core.Localization.Strings.ApplyNowHint;

    public static string ChannelStable => global::VPNRouter.Core.Localization.Strings.ChannelStable;
    public static string ChannelExperimental => global::VPNRouter.Core.Localization.Strings.ChannelExperimental;

    public static string TabTelegram => global::VPNRouter.Core.Localization.Strings.TabTelegram;
    public static string TgProxyDescription => global::VPNRouter.Core.Localization.Strings.TgProxyDescription;
    public static string TgProxySetupHint => global::VPNRouter.Core.Localization.Strings.TgProxySetupHint;
    public static string TgProxyPort => global::VPNRouter.Core.Localization.Strings.TgProxyPort;
    public static string TgProxySecret => global::VPNRouter.Core.Localization.Strings.TgProxySecret;
    public static string TgProxyLink => global::VPNRouter.Core.Localization.Strings.TgProxyLink;
    public static string TgProxyCopy => global::VPNRouter.Core.Localization.Strings.TgProxyCopy;
    public static string TgProxyCopied => global::VPNRouter.Core.Localization.Strings.TgProxyCopied;
    public static string TgProxyRegenerate => global::VPNRouter.Core.Localization.Strings.TgProxyRegenerate;
    public static string TgProxyStart => global::VPNRouter.Core.Localization.Strings.TgProxyStart;
    public static string TgProxyStop => global::VPNRouter.Core.Localization.Strings.TgProxyStop;
    public static string TgProxyOpenInTelegram => global::VPNRouter.Core.Localization.Strings.TgProxyOpenInTelegram;

    public static string TgProxyStartAndOpen => global::VPNRouter.Core.Localization.Strings.TgProxyStartAndOpen;
    public static string TgProxySetupOnce => global::VPNRouter.Core.Localization.Strings.TgProxySetupOnce;

    public static string TgProxyReopenInTelegram => global::VPNRouter.Core.Localization.Strings.TgProxyReopenInTelegram;

    public static string TgProxyCopySecretA11y => global::VPNRouter.Core.Localization.Strings.TgProxyCopySecretA11y;
    public static string TgProxyRegenerateSecretA11y => global::VPNRouter.Core.Localization.Strings.TgProxyRegenerateSecretA11y;

    public static string TgProxyPortBusy => global::VPNRouter.Core.Localization.Strings.TgProxyPortBusy;
    public static string TgProxyPortBusyWithOwner => global::VPNRouter.Core.Localization.Strings.TgProxyPortBusyWithOwner;
    public static string TgProxyExitedImmediately => global::VPNRouter.Core.Localization.Strings.TgProxyExitedImmediately;
    public static string TgProxyTelegramNotInstalled => global::VPNRouter.Core.Localization.Strings.TgProxyTelegramNotInstalled;
    public static string TgProxySchemeMissingWarning => global::VPNRouter.Core.Localization.Strings.TgProxySchemeMissingWarning;
    public static string TgProxyDownloadStep1Python => global::VPNRouter.Core.Localization.Strings.TgProxyDownloadStep1Python;
    public static string TgProxyDownloadStep2Wheels => global::VPNRouter.Core.Localization.Strings.TgProxyDownloadStep2Wheels;
    public static string TgProxyDownloadStep3Source => global::VPNRouter.Core.Localization.Strings.TgProxyDownloadStep3Source;

    public static string TgProxyOneTapTitleStopped => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapTitleStopped;
    public static string TgProxyOneTapTitleRunning => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapTitleRunning;
    public static string TgProxyOneTapLedeStopped  => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapLedeStopped;
    public static string TgProxyOneTapLedeRunning(int port) => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapLedeRunning(port);
    public static string TgProxyOneTapStep1 => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapStep1;
    public static string TgProxyOneTapStep2 => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapStep2;
    public static string TgProxyOneTapStep3 => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapStep3;
    public static string TgProxyOneTapTune  => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapTune;
    public static string TgProxyOneTapAirPill(int port) => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapAirPill(port);

    public static string TgProxyTabSettings => global::VPNRouter.Core.Localization.Strings.TgProxyTabSettings;
    public static string TgProxyTabVersion  => global::VPNRouter.Core.Localization.Strings.TgProxyTabVersion;
    public static string TgProxyTabHelp     => global::VPNRouter.Core.Localization.Strings.TgProxyTabHelp;

    public static string ZapretOneTapTitleStopped => global::VPNRouter.Core.Localization.Strings.ZapretOneTapTitleStopped;
    public static string ZapretOneTapTitleProbing => global::VPNRouter.Core.Localization.Strings.ZapretOneTapTitleProbing;
    public static string ZapretOneTapTitleRunning(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretOneTapTitleRunning(strategy);
    public static string ZapretOneTapTitleFallback => global::VPNRouter.Core.Localization.Strings.ZapretOneTapTitleFallback;
    public static string ZapretOneTapLedeStopped => global::VPNRouter.Core.Localization.Strings.ZapretOneTapLedeStopped;
    public static string ZapretOneTapLedeProbing(int i, int t, string s) => global::VPNRouter.Core.Localization.Strings.ZapretOneTapLedeProbing(i, t, s);
    public static string ZapretOneTapLedeProbingScored(int i, int t, string s, int p, int tp) => global::VPNRouter.Core.Localization.Strings.ZapretOneTapLedeProbingScored(i, t, s, p, tp);
    public static string ZapretOneTapLedeRunning => global::VPNRouter.Core.Localization.Strings.ZapretOneTapLedeRunning;
    public static string ZapretOneTapLedeFallback => global::VPNRouter.Core.Localization.Strings.ZapretOneTapLedeFallback;
    public static string ZapretOneTapStep1 => global::VPNRouter.Core.Localization.Strings.ZapretOneTapStep1;
    public static string ZapretOneTapStep2 => global::VPNRouter.Core.Localization.Strings.ZapretOneTapStep2;
    public static string ZapretOneTapStep3 => global::VPNRouter.Core.Localization.Strings.ZapretOneTapStep3;
    public static string ZapretOneTapTune => global::VPNRouter.Core.Localization.Strings.ZapretOneTapTune;
    public static string ZapretOneTapStartButton => global::VPNRouter.Core.Localization.Strings.ZapretOneTapStartButton;
    public static string ZapretOneTapStopButton => global::VPNRouter.Core.Localization.Strings.ZapretOneTapStopButton;
    public static string ZapretOneTapAirPill(string s, int p) => global::VPNRouter.Core.Localization.Strings.ZapretOneTapAirPill(s, p);
    public static string ZapretOneTapAirPillScored(string s, int p, int t) => global::VPNRouter.Core.Localization.Strings.ZapretOneTapAirPillScored(s, p, t);
    public static string ZapretOneTapDownloading => global::VPNRouter.Core.Localization.Strings.ZapretOneTapDownloading;
    public static string ZapretOneTapInstallingHosts => global::VPNRouter.Core.Localization.Strings.ZapretOneTapInstallingHosts;
    public static string ZapretOneTapAllFailedToast => global::VPNRouter.Core.Localization.Strings.ZapretOneTapAllFailedToast;
    public static string ZapretOneTapNoSignalToast => global::VPNRouter.Core.Localization.Strings.ZapretOneTapNoSignalToast;

    public static string OpenFolder => global::VPNRouter.Core.Localization.Strings.OpenFolder;
    public static string OpenGitHub => global::VPNRouter.Core.Localization.Strings.OpenGitHub;

    public static string SubscriptionsSection => global::VPNRouter.Core.Localization.Strings.SubscriptionsSection;
    public static string SubscriptionNameHint => global::VPNRouter.Core.Localization.Strings.SubscriptionNameHint;
    public static string AddSubscription => global::VPNRouter.Core.Localization.Strings.AddSubscription;
    public static string RefreshAll => global::VPNRouter.Core.Localization.Strings.RefreshAll;
    public static string NeverRefreshed => global::VPNRouter.Core.Localization.Strings.NeverRefreshed;
    public static string SubUpdatedAt => global::VPNRouter.Core.Localization.Strings.SubUpdatedAt;

    public static string ToolsSection => global::VPNRouter.Core.Localization.Strings.ToolsSection;
    public static string RunDiagnostics => global::VPNRouter.Core.Localization.Strings.RunDiagnostics;
    public static string ClearDiscordCache => global::VPNRouter.Core.Localization.Strings.ClearDiscordCache;
    public static string UpdateHostsFile => global::VPNRouter.Core.Localization.Strings.UpdateHostsFile;
    public static string OpenServiceMenu => global::VPNRouter.Core.Localization.Strings.OpenServiceMenu;
    public static string TipOpenServiceMenu => global::VPNRouter.Core.Localization.Strings.TipOpenServiceMenu;

    public static string ZapretSecStatus => global::VPNRouter.Core.Localization.Strings.ZapretSecStatus;
    public static string ZapretSecStrategy => global::VPNRouter.Core.Localization.Strings.ZapretSecStrategy;
    public static string ZapretSecHosts => global::VPNRouter.Core.Localization.Strings.ZapretSecHosts;
    public static string ZapretSecFilters => global::VPNRouter.Core.Localization.Strings.ZapretSecFilters;
    public static string ZapretSecUpdates => global::VPNRouter.Core.Localization.Strings.ZapretSecUpdates;
    public static string ZapretSecDiagnostics => global::VPNRouter.Core.Localization.Strings.ZapretSecDiagnostics;
    public static string ZapretSecAdvanced => global::VPNRouter.Core.Localization.Strings.ZapretSecAdvanced;

    public static string ZapretSecStrategyDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecStrategyDesc;
    public static string ZapretSecHostsDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecHostsDesc;
    public static string ZapretSecFiltersDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecFiltersDesc;
    public static string ZapretSecAdvancedDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecAdvancedDesc;

    public static string GameFilter => global::VPNRouter.Core.Localization.Strings.GameFilter;
    public static string GameFilterOff => global::VPNRouter.Core.Localization.Strings.GameFilterOff;
    public static string GameFilterAll => global::VPNRouter.Core.Localization.Strings.GameFilterAll;
    public static string GameFilterTcp => global::VPNRouter.Core.Localization.Strings.GameFilterTcp;
    public static string GameFilterUdp => global::VPNRouter.Core.Localization.Strings.GameFilterUdp;

    public static string IpSetFilter => global::VPNRouter.Core.Localization.Strings.IpSetFilter;
    public static string IpSetAny => global::VPNRouter.Core.Localization.Strings.IpSetAny;
    public static string IpSetLoaded => global::VPNRouter.Core.Localization.Strings.IpSetLoaded;
    public static string IpSetNone => global::VPNRouter.Core.Localization.Strings.IpSetNone;

    public static string UpdateIpSet => global::VPNRouter.Core.Localization.Strings.UpdateIpSet;
    public static string AutoUpdateCheckLabel => global::VPNRouter.Core.Localization.Strings.AutoUpdateCheckLabel;

    public static string RunTestsLabel => global::VPNRouter.Core.Localization.Strings.RunTestsLabel;
    public static string RemoveServiceLabel => global::VPNRouter.Core.Localization.Strings.RemoveServiceLabel;

    public static string ApplyChanges => global::VPNRouter.Core.Localization.Strings.ApplyChanges;
    public static string ChangesApplied => global::VPNRouter.Core.Localization.Strings.ChangesApplied;
    public static string ApplyFailed => global::VPNRouter.Core.Localization.Strings.ApplyFailed;

    public static string AddCategory => global::VPNRouter.Core.Localization.Strings.AddCategory;
    public static string EnableWholeGroup => global::VPNRouter.Core.Localization.Strings.EnableWholeGroup;
    public static string CategoryNamePrompt => global::VPNRouter.Core.Localization.Strings.CategoryNamePrompt;
    public static string AddAppHint => global::VPNRouter.Core.Localization.Strings.AddAppHint;

    public static string GroupDisplayName(string internalName) => global::VPNRouter.Core.Localization.Strings.GroupDisplayName(internalName);

    public static string SectionRouting => global::VPNRouter.Core.Localization.Strings.SectionRouting;
    public static string SectionRules => global::VPNRouter.Core.Localization.Strings.SectionRules;
    public static string SectionLeakProtection => global::VPNRouter.Core.Localization.Strings.SectionLeakProtection;
    public static string SectionContent => global::VPNRouter.Core.Localization.Strings.SectionContent;
    public static string SectionUpdates => global::VPNRouter.Core.Localization.Strings.SectionUpdates;
    public static string AutostartSection => global::VPNRouter.Core.Localization.Strings.AutostartSection;
    public static string AutostartVpn => global::VPNRouter.Core.Localization.Strings.AutostartVpn;
    public static string AutostartZapret => global::VPNRouter.Core.Localization.Strings.AutostartZapret;
    public static string AutostartTgProxy => global::VPNRouter.Core.Localization.Strings.AutostartTgProxy;
    public static string AutostartUi => global::VPNRouter.Core.Localization.Strings.AutostartUi;

    public static string TabFreeConfigs => global::VPNRouter.Core.Localization.Strings.TabFreeConfigs;
    public static string FcDashboardTotal => global::VPNRouter.Core.Localization.Strings.FcDashboardTotal;
    public static string FcDashboardWorking => global::VPNRouter.Core.Localization.Strings.FcDashboardWorking;
    public static string FcDashboardTimeout => global::VPNRouter.Core.Localization.Strings.FcDashboardTimeout;
    public static string FcDashboardUnreach => global::VPNRouter.Core.Localization.Strings.FcDashboardUnreach;
    public static string FcDashboardTlsFail => global::VPNRouter.Core.Localization.Strings.FcDashboardTlsFail;
    public static string FcDashboardVerified => global::VPNRouter.Core.Localization.Strings.FcDashboardVerified;
    public static string FcDashboardFake => global::VPNRouter.Core.Localization.Strings.FcDashboardFake;
    public static string FcDeepVerify => global::VPNRouter.Core.Localization.Strings.FcDeepVerify;
    public static string FcStatusNoDeepCandidates => global::VPNRouter.Core.Localization.Strings.FcStatusNoDeepCandidates;
    public static string FcStatusDeepVerifyStart(int target) => global::VPNRouter.Core.Localization.Strings.FcStatusDeepVerifyStart(target);
    public static string FcStatusDeepVerifyProbe(int found, int target, int tested, string host) => global::VPNRouter.Core.Localization.Strings.FcStatusDeepVerifyProbe(found, target, tested, host);
    public static string FcStatusDeepVerifyProgress(int found, int target, int tested, int totalQueue) => global::VPNRouter.Core.Localization.Strings.FcStatusDeepVerifyProgress(found, target, tested, totalQueue);
    public static string FcStatusDeepVerifyDone(int verified) => global::VPNRouter.Core.Localization.Strings.FcStatusDeepVerifyDone(verified);
    public static string FcStatusDeepVerifyExhausted(int verified, int tested) => global::VPNRouter.Core.Localization.Strings.FcStatusDeepVerifyExhausted(verified, tested);

    public static string FcStatusBatchedSearchStart(int target, int poolSize) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedSearchStart(target, poolSize);
    public static string FcStatusBatchedTcpTls(int found, int target, int batchNum, int totalBatches) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedTcpTls(found, target, batchNum, totalBatches);
    public static string FcStatusBatchedTcpTlsProgress(int found, int target, int batchNum, int totalBatches, int done, int total) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedTcpTlsProgress(found, target, batchNum, totalBatches, done, total);
    public static string FcStatusBatchedDeepVerify(int found, int target, int batchNum, int totalBatches, int candidates) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedDeepVerify(found, target, batchNum, totalBatches, candidates);
    public static string FcStatusBatchedFound(int found, int target) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedFound(found, target);
    public static string FcStatusBatchedProbing(int found, int target, string host, int port, string cc) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedProbing(found, target, host, port, cc);

    public static string FcDeepTargetLabel => global::VPNRouter.Core.Localization.Strings.FcDeepTargetLabel;
    public static string FcDeepExcludeRu => global::VPNRouter.Core.Localization.Strings.FcDeepExcludeRu;
    public static string FcDeepHint => global::VPNRouter.Core.Localization.Strings.FcDeepHint;
    public static string FcStatusMainVpnActive => global::VPNRouter.Core.Localization.Strings.FcStatusMainVpnActive;
    public static string FcOpenLogs => global::VPNRouter.Core.Localization.Strings.FcOpenLogs;
    public static string FcClearFailed => global::VPNRouter.Core.Localization.Strings.FcClearFailed;
    public static string FcKeepVerified => global::VPNRouter.Core.Localization.Strings.FcKeepVerified;
    public static string FcKeepVerifiedOnly => global::VPNRouter.Core.Localization.Strings.FcKeepVerifiedOnly;
    public static string FcClearAll => global::VPNRouter.Core.Localization.Strings.FcClearAll;
    public static string FcCleanupHint => global::VPNRouter.Core.Localization.Strings.FcCleanupHint;
    public static string FcStatusCleared(int removed, int kept) => global::VPNRouter.Core.Localization.Strings.FcStatusCleared(removed, kept);
    public static string FcCountryFilter => global::VPNRouter.Core.Localization.Strings.FcCountryFilter;
    public static string FcRefreshSources => global::VPNRouter.Core.Localization.Strings.FcRefreshSources;
    public static string FcRetestAll => global::VPNRouter.Core.Localization.Strings.FcRetestAll;
    public static string FcConnectHint => global::VPNRouter.Core.Localization.Strings.FcConnectHint;
    public static string FcTipVpnActive => global::VPNRouter.Core.Localization.Strings.FcTipVpnActive;
    public static string FcCancel => global::VPNRouter.Core.Localization.Strings.FcCancel;
    public static string FcApplySelected => global::VPNRouter.Core.Localization.Strings.FcApplySelected;
    public static string FcCountryAll => global::VPNRouter.Core.Localization.Strings.FcCountryAll;
    public static string FcColCountry => global::VPNRouter.Core.Localization.Strings.FcColCountry;
    public static string FcColEndpoint => global::VPNRouter.Core.Localization.Strings.FcColEndpoint;
    public static string FcColLatency => global::VPNRouter.Core.Localization.Strings.FcColLatency;
    public static string FcColBandwidth => global::VPNRouter.Core.Localization.Strings.FcColBandwidth;
    public static string FcSpeedColumnTooltip => global::VPNRouter.Core.Localization.Strings.FcSpeedColumnTooltip;
    public static string HealthCheckSavedToast => global::VPNRouter.Core.Localization.Strings.HealthCheckSavedToast;
    public static string FcColSni => global::VPNRouter.Core.Localization.Strings.FcColSni;
    public static string FcColTransport => global::VPNRouter.Core.Localization.Strings.FcColTransport;
    public static string FcEmptyHint => global::VPNRouter.Core.Localization.Strings.FcEmptyHint;
    public static string FcEmptyCtaTitle => global::VPNRouter.Core.Localization.Strings.FcEmptyCtaTitle;
    public static string FcEmptyCtaSubtitle => global::VPNRouter.Core.Localization.Strings.FcEmptyCtaSubtitle;
    public static string FcEmptyCtaButton => global::VPNRouter.Core.Localization.Strings.FcEmptyCtaButton;
    public static string FcFilteredEmpty => global::VPNRouter.Core.Localization.Strings.FcFilteredEmpty;
    public static string FcRefreshHint => global::VPNRouter.Core.Localization.Strings.FcRefreshHint;

    public static string FcSmartRefreshLabel => global::VPNRouter.Core.Localization.Strings.FcSmartRefreshLabel;
    public static string FcTargetNLabel => global::VPNRouter.Core.Localization.Strings.FcTargetNLabel;
    public static string FcConfigsWord => global::VPNRouter.Core.Localization.Strings.FcConfigsWord;
    public static string FcWithPingUnder => global::VPNRouter.Core.Localization.Strings.FcWithPingUnder;
    public static string FcMsUnit => global::VPNRouter.Core.Localization.Strings.FcMsUnit;
    public static string FcSmartRefreshHint => global::VPNRouter.Core.Localization.Strings.FcSmartRefreshHint;

    public static string FcMoreOptions => global::VPNRouter.Core.Localization.Strings.FcMoreOptions;

    public static string FcListHeader => global::VPNRouter.Core.Localization.Strings.FcListHeader;
    public static string FcListShown => global::VPNRouter.Core.Localization.Strings.FcListShown;

    public static string FcDeepStop => global::VPNRouter.Core.Localization.Strings.FcDeepStop;
    public static string FcDeepStopTooltip => global::VPNRouter.Core.Localization.Strings.FcDeepStopTooltip;

    public static string FcAdvancedSettings => global::VPNRouter.Core.Localization.Strings.FcAdvancedSettings;
    public static string SmpConfigRowChange => global::VPNRouter.Core.Localization.Strings.SmpConfigRowChange;
    public static string SmpConfigRowHide => global::VPNRouter.Core.Localization.Strings.SmpConfigRowHide;
    public static string SectionShow => global::VPNRouter.Core.Localization.Strings.SectionShow;
    public static string SectionHide => global::VPNRouter.Core.Localization.Strings.SectionHide;
    public static string SrvManualEmptyHint => global::VPNRouter.Core.Localization.Strings.SrvManualEmptyHint;

    public static string FcTabSearch => global::VPNRouter.Core.Localization.Strings.FcTabSearch;
    public static string FcTabSaved => global::VPNRouter.Core.Localization.Strings.FcTabSaved;
    public static string FcTabSavedWithCount(int n) => global::VPNRouter.Core.Localization.Strings.FcTabSavedWithCount(n);
    public static string FcSavedTabHint => global::VPNRouter.Core.Localization.Strings.FcSavedTabHint;
    public static string FcSavedRecheckStaleBtn(int n) => global::VPNRouter.Core.Localization.Strings.FcSavedRecheckStaleBtn(n);
    public static string FcSavedRecheckAllBtn => global::VPNRouter.Core.Localization.Strings.FcSavedRecheckAllBtn;
    public static string FcSavedClearAllBtn => global::VPNRouter.Core.Localization.Strings.FcSavedClearAllBtn;
    public static string FcSavedColStatus => global::VPNRouter.Core.Localization.Strings.FcSavedColStatus;
    public static string FcSavedEmpty => global::VPNRouter.Core.Localization.Strings.FcSavedEmpty;
    public static string FcSavedRecheckOneTooltip => global::VPNRouter.Core.Localization.Strings.FcSavedRecheckOneTooltip;
    public static string FcSavedRemoveOneTooltip => global::VPNRouter.Core.Localization.Strings.FcSavedRemoveOneTooltip;
    public static string FcFreshnessFresh => global::VPNRouter.Core.Localization.Strings.FcFreshnessFresh;
    public static string FcFreshnessAgeingDays(int d) => global::VPNRouter.Core.Localization.Strings.FcFreshnessAgeingDays(d);
    public static string FcFreshnessStale => global::VPNRouter.Core.Localization.Strings.FcFreshnessStale;
    public static string FcFreshnessFailed => global::VPNRouter.Core.Localization.Strings.FcFreshnessFailed;
    public static string FcStatusRecheckOne(string host, int port, string cc) => global::VPNRouter.Core.Localization.Strings.FcStatusRecheckOne(host, port, cc);
    public static string FcStatusRecheckAllStart(int total) => global::VPNRouter.Core.Localization.Strings.FcStatusRecheckAllStart(total);
    public static string FcStatusRecheckAllProgress(int done, int total) => global::VPNRouter.Core.Localization.Strings.FcStatusRecheckAllProgress(done, total);
    public static string FcStatusRecheckAllDone(int verified, int failed) => global::VPNRouter.Core.Localization.Strings.FcStatusRecheckAllDone(verified, failed);

    public static string FcSearchListEmptyHint => global::VPNRouter.Core.Localization.Strings.FcSearchListEmptyHint;

    public static string FcFastScanLabel => global::VPNRouter.Core.Localization.Strings.FcFastScanLabel;
    public static string FcFastScanHint => global::VPNRouter.Core.Localization.Strings.FcFastScanHint;

    public static string FcPresetLabel => global::VPNRouter.Core.Localization.Strings.FcPresetLabel;
    public static string FcPresetGaming => global::VPNRouter.Core.Localization.Strings.FcPresetGaming;
    public static string FcPresetStream => global::VPNRouter.Core.Localization.Strings.FcPresetStream;
    public static string FcPresetChat => global::VPNRouter.Core.Localization.Strings.FcPresetChat;
    public static string FcPresetBest => global::VPNRouter.Core.Localization.Strings.FcPresetBest;
    public static string FcPresetCustom => global::VPNRouter.Core.Localization.Strings.FcPresetCustom;
    public static string FcCustomPing => global::VPNRouter.Core.Localization.Strings.FcCustomPing;
    public static string FcCustomBw => global::VPNRouter.Core.Localization.Strings.FcCustomBw;
    public static string FcMbpsUnit => global::VPNRouter.Core.Localization.Strings.FcMbpsUnit;
    public static string FcBandwidthHint => global::VPNRouter.Core.Localization.Strings.FcBandwidthHint;

    public static string FcUserSrcSection => global::VPNRouter.Core.Localization.Strings.FcUserSrcSection;
    public static string FcUserSrcNamePlaceholder => global::VPNRouter.Core.Localization.Strings.FcUserSrcNamePlaceholder;
    public static string FcUserSrcUrlPlaceholder => global::VPNRouter.Core.Localization.Strings.FcUserSrcUrlPlaceholder;
    public static string FcUserSrcAdd => global::VPNRouter.Core.Localization.Strings.FcUserSrcAdd;
    public static string FcUserSrcHint => global::VPNRouter.Core.Localization.Strings.FcUserSrcHint;
    public static string FcUserSrcEmpty => global::VPNRouter.Core.Localization.Strings.FcUserSrcEmpty;
    public static string FcUserSrcAdded => global::VPNRouter.Core.Localization.Strings.FcUserSrcAdded;
    public static string FcUserSrcRemoved => global::VPNRouter.Core.Localization.Strings.FcUserSrcRemoved;
    public static string FcUserSrcDuplicate => global::VPNRouter.Core.Localization.Strings.FcUserSrcDuplicate;
    public static string FcUserSrcInvalidUrl => global::VPNRouter.Core.Localization.Strings.FcUserSrcInvalidUrl;
    public static string FcUserSrcEmptyUrl => global::VPNRouter.Core.Localization.Strings.FcUserSrcEmptyUrl;

    public static string FcRefreshTooltip => global::VPNRouter.Core.Localization.Strings.FcRefreshTooltip;
    public static string FcRetestTooltip => global::VPNRouter.Core.Localization.Strings.FcRetestTooltip;
    public static string FcDeepVerifyTooltip => global::VPNRouter.Core.Localization.Strings.FcDeepVerifyTooltip;

    public static string FcSecWarnTitle => global::VPNRouter.Core.Localization.Strings.FcSecWarnTitle;
    public static string FcSecWarnHeader => global::VPNRouter.Core.Localization.Strings.FcSecWarnHeader;
    public static string FcSecWarnBody => global::VPNRouter.Core.Localization.Strings.FcSecWarnBody;
    public static string FcSecWarnDontUseList => global::VPNRouter.Core.Localization.Strings.FcSecWarnDontUseList;
    public static string FcSecWarnGoodFor => global::VPNRouter.Core.Localization.Strings.FcSecWarnGoodFor;
    public static string FcSecWarnProceed => global::VPNRouter.Core.Localization.Strings.FcSecWarnProceed;
    public static string FcSecWarnCancel => global::VPNRouter.Core.Localization.Strings.FcSecWarnCancel;
    public static string FcPageDescription => global::VPNRouter.Core.Localization.Strings.FcPageDescription;
    public static string FcStatusEmpty => global::VPNRouter.Core.Localization.Strings.FcStatusEmpty;
    public static string FcStatusCancelled => global::VPNRouter.Core.Localization.Strings.FcStatusCancelled;
    public static string FcStatusApplyFailed => global::VPNRouter.Core.Localization.Strings.FcStatusApplyFailed;
    public static string FcConnectNeedsVerify => global::VPNRouter.Core.Localization.Strings.FcConnectNeedsVerify;
    public static string FcStatusCacheAge(string age) => global::VPNRouter.Core.Localization.Strings.FcStatusCacheAge(age);
    public static string FcStatusRefreshed(int n) => global::VPNRouter.Core.Localization.Strings.FcStatusRefreshed(n);
    public static string FcStatusTested(int n) => global::VPNRouter.Core.Localization.Strings.FcStatusTested(n);
    public static string FcStatusFailed(string err) => global::VPNRouter.Core.Localization.Strings.FcStatusFailed(err);
    public static string FcStatusApplying(string ep) => global::VPNRouter.Core.Localization.Strings.FcStatusApplying(ep);
    public static string FcStatusApplied(string ep) => global::VPNRouter.Core.Localization.Strings.FcStatusApplied(ep);

    public static string AutostartWithWindows => global::VPNRouter.Core.Localization.Strings.AutostartWithWindows;
    public static string RestartService => global::VPNRouter.Core.Localization.Strings.RestartService;
    public static string ReinstallService => global::VPNRouter.Core.Localization.Strings.ReinstallService;
    public static string InstallingService => global::VPNRouter.Core.Localization.Strings.InstallingService;
    public static string RemovingService => global::VPNRouter.Core.Localization.Strings.RemovingService;

    public static string ServerListHint => global::VPNRouter.Core.Localization.Strings.ServerListHint;
    public static string ZapretHostsHint => global::VPNRouter.Core.Localization.Strings.ZapretHostsHint;
    public static string AppsGroupEmpty => global::VPNRouter.Core.Localization.Strings.AppsGroupEmpty;

    public static string AppsFullTunnelBanner => global::VPNRouter.Core.Localization.Strings.AppsFullTunnelBanner;
    public static string AppsFullTunnelBannerAction => global::VPNRouter.Core.Localization.Strings.AppsFullTunnelBannerAction;

    public static string AppsModeSectionTitle => Ru
        ? "Как направлять приложения"
        : "How to route applications";
    public static string AppsModeInclude => Ru
        ? "Только выбранные — через VPN"
        : "Only selected apps use VPN";
    public static string AppsModeExclude => Ru
        ? "Все, кроме выбранных — через VPN"
        : "All except selected apps use VPN";
    public static string AppsSelectAll => Ru ? "Выбрать все" : "Select all";
    public static string AppsClearAll => Ru ? "Очистить" : "Clear";
    public static string SteamGamesNotFound => Ru
        ? "Steam-игры не найдены"
        : "No Steam games found";
    public static string NoNewSteamGamesFound => Ru
        ? "Новых Steam-игр не найдено"
        : "No new Steam games found";
    public static string AppsPendingApplyHint => Ru
        ? "Изменения сохранены. Примените их к текущему подключению."
        : "Changes are saved. Apply them to the current connection.";
    public static string AppsModeIncludeHint => Ru
        ? "Отмеченные приложения идут через VPN, остальные — напрямую (обычный split-tunnel)."
        : "Checked apps go through VPN; everything else stays direct (regular split-tunnel).";
    public static string AppsModeExcludeHint => Ru
        ? "Отмеченные приложения идут напрямую (мимо VPN), остальной трафик идёт через VPN."
        : "Checked apps bypass VPN (direct); everything else goes through VPN.";

    public static string AppsListSectionTitle => Ru
        ? "Что редактировать"
        : "What to edit";
    public static string AppsListInclude => Ru
        ? "Через VPN"
        : "Through VPN";
    public static string AppsListExclude => Ru
        ? "Мимо VPN"
        : "Bypass VPN";

    public static string ServersOrphanBadge => Ru ? "Не из подписки" : "Not in subscription";
    public static string ServersOrphanTooltip => Ru
        ? "Этот сервер не входит в активные подписки — старая ручная запись. Если он вам не нужен, удалите его."
        : "This server isn't part of any active subscription — it's a legacy manual entry. If you don't need it, remove it.";

    public static string CustomRulesTitle => global::VPNRouter.Core.Localization.Strings.CustomRulesTitle;
    public static string CustomRulesDescription => global::VPNRouter.Core.Localization.Strings.CustomRulesDescription;
    public static string CustomRulesPlaceholder => global::VPNRouter.Core.Localization.Strings.CustomRulesPlaceholder;
    public static string CustomRulesErrorHeader => global::VPNRouter.Core.Localization.Strings.CustomRulesErrorHeader;
    public static string CustomRulesConflictHeader => global::VPNRouter.Core.Localization.Strings.CustomRulesConflictHeader;

    public static string CustomRulesPageDescription => global::VPNRouter.Core.Localization.Strings.CustomRulesPageDescription;

    public static string CustomRulesEmpty => global::VPNRouter.Core.Localization.Strings.CustomRulesEmpty;

    public static string CustomRulesAddTitle => global::VPNRouter.Core.Localization.Strings.CustomRulesAddTitle;
    public static string CustomRulesAddBtn => global::VPNRouter.Core.Localization.Strings.CustomRulesAddBtn;
    public static string CustomRulesActionLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesActionLabel;
    public static string CustomRulesTypeLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesTypeLabel;
    public static string CustomRulesValueLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesValueLabel;
    public static string CustomRulesCommentLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesCommentLabel;

    public static string CustomRulesActionDirect => global::VPNRouter.Core.Localization.Strings.CustomRulesActionDirect;
    public static string CustomRulesActionProxy => global::VPNRouter.Core.Localization.Strings.CustomRulesActionProxy;
    public static string CustomRulesActionBlock => global::VPNRouter.Core.Localization.Strings.CustomRulesActionBlock;

    public static string CustomRulesValuePlaceholder => global::VPNRouter.Core.Localization.Strings.CustomRulesValuePlaceholder;

    public static string CustomRulesAdvancedMode => global::VPNRouter.Core.Localization.Strings.CustomRulesAdvancedMode;

    public static string CustomRulesValidationFailed => global::VPNRouter.Core.Localization.Strings.CustomRulesValidationFailed;

    public static string CustomRulesActionDirectLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesActionDirectLabel;
    public static string CustomRulesActionProxyLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesActionProxyLabel;
    public static string CustomRulesActionBlockLabel => global::VPNRouter.Core.Localization.Strings.CustomRulesActionBlockLabel;

    public static string CustomRulesDelete => global::VPNRouter.Core.Localization.Strings.CustomRulesDelete;
    public static string CustomRulesEdit => global::VPNRouter.Core.Localization.Strings.CustomRulesEdit;
    public static string CustomRulesMoveUp => global::VPNRouter.Core.Localization.Strings.CustomRulesMoveUp;
    public static string CustomRulesMoveDown => global::VPNRouter.Core.Localization.Strings.CustomRulesMoveDown;

    public static string CustomRulesImport => global::VPNRouter.Core.Localization.Strings.CustomRulesImport;
    public static string CustomRulesExport => global::VPNRouter.Core.Localization.Strings.CustomRulesExport;
    public static string CustomRulesImportTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesImportTooltip;
    public static string CustomRulesExportTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesExportTooltip;

    public static string CustomRulesSearchPlaceholder => global::VPNRouter.Core.Localization.Strings.CustomRulesSearchPlaceholder;
    public static string CustomRulesClearAll => global::VPNRouter.Core.Localization.Strings.CustomRulesClearAll;
    public static string CustomRulesEnableAll => global::VPNRouter.Core.Localization.Strings.CustomRulesEnableAll;
    public static string CustomRulesDisableAll => global::VPNRouter.Core.Localization.Strings.CustomRulesDisableAll;
    public static string CustomRulesClearAllTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesClearAllTooltip;
    public static string CustomRulesEnableAllTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesEnableAllTooltip;
    public static string CustomRulesDisableAllTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesDisableAllTooltip;
    public static string CustomRulesNoMatchHint => global::VPNRouter.Core.Localization.Strings.CustomRulesNoMatchHint;

    public static string CustomRulesExistingHeader => global::VPNRouter.Core.Localization.Strings.CustomRulesExistingHeader;

    public static string RulesViewCards => global::VPNRouter.Core.Localization.Strings.RulesViewCards;
    public static string RulesViewRead => global::VPNRouter.Core.Localization.Strings.RulesViewRead;
    public static string RulesViewEdit => global::VPNRouter.Core.Localization.Strings.RulesViewEdit;
    public static string RulesViewCardsTooltip => global::VPNRouter.Core.Localization.Strings.RulesViewCardsTooltip;
    public static string RulesViewReadTooltip => global::VPNRouter.Core.Localization.Strings.RulesViewReadTooltip;
    public static string RulesViewEditTooltip => global::VPNRouter.Core.Localization.Strings.RulesViewEditTooltip;

    public static string RulesEditorApply => global::VPNRouter.Core.Localization.Strings.RulesEditorApply;
    public static string RulesEditorRevert => global::VPNRouter.Core.Localization.Strings.RulesEditorRevert;
    public static string RulesEditorDirty => global::VPNRouter.Core.Localization.Strings.RulesEditorDirty;
    public static string RulesEditorFormatHint => global::VPNRouter.Core.Localization.Strings.RulesEditorFormatHint;

    public static string RulesFilterAll => global::VPNRouter.Core.Localization.Strings.RulesFilterAll;
    public static string RulesBulkActions => global::VPNRouter.Core.Localization.Strings.RulesBulkActions;

    public static string RulesSortByType => global::VPNRouter.Core.Localization.Strings.RulesSortByType;

    public static string RulesClearAllHint => global::VPNRouter.Core.Localization.Strings.RulesClearAllHint;
    public static string RulesClearAllConfirm => global::VPNRouter.Core.Localization.Strings.RulesClearAllConfirm;
    public static string CommonCancel => global::VPNRouter.Core.Localization.Strings.CommonCancel;

    public static string RulesCustomAboveToggles => global::VPNRouter.Core.Localization.Strings.RulesCustomAboveToggles;
    public static string RulesCustomAboveTogglesHint => global::VPNRouter.Core.Localization.Strings.RulesCustomAboveTogglesHint;

    public static string RulesAddLabelAction => global::VPNRouter.Core.Localization.Strings.RulesAddLabelAction;
    public static string RulesAddLabelType => global::VPNRouter.Core.Localization.Strings.RulesAddLabelType;
    public static string RulesAddLabelValue => global::VPNRouter.Core.Localization.Strings.RulesAddLabelValue;
    public static string RulesAddLabelComment => global::VPNRouter.Core.Localization.Strings.RulesAddLabelComment;
    public static string RulesAddLabelOpt => global::VPNRouter.Core.Localization.Strings.RulesAddLabelOpt;

    public static string RulesHelpHeader => global::VPNRouter.Core.Localization.Strings.RulesHelpHeader;

    public static string RulesHelpB1Pre => global::VPNRouter.Core.Localization.Strings.RulesHelpB1Pre;
    public static string RulesHelpB1T1 => global::VPNRouter.Core.Localization.Strings.RulesHelpB1T1;
    public static string RulesHelpB1Mid => global::VPNRouter.Core.Localization.Strings.RulesHelpB1Mid;
    public static string RulesHelpB1T2 => global::VPNRouter.Core.Localization.Strings.RulesHelpB1T2;
    public static string RulesHelpB1Suf => global::VPNRouter.Core.Localization.Strings.RulesHelpB1Suf;

    public static string RulesHelpB2Pre => global::VPNRouter.Core.Localization.Strings.RulesHelpB2Pre;
    public static string RulesHelpB2Mid => global::VPNRouter.Core.Localization.Strings.RulesHelpB2Mid;
    public static string RulesHelpB2Suf => global::VPNRouter.Core.Localization.Strings.RulesHelpB2Suf;

    public static string RulesHelpB3Pre => global::VPNRouter.Core.Localization.Strings.RulesHelpB3Pre;
    public static string RulesHelpB3Bold => global::VPNRouter.Core.Localization.Strings.RulesHelpB3Bold;
    public static string RulesHelpB3Suf => global::VPNRouter.Core.Localization.Strings.RulesHelpB3Suf;

    public static string RulesHelpBanner => global::VPNRouter.Core.Localization.Strings.RulesHelpBanner;

    public static string SelectCategoryHint => global::VPNRouter.Core.Localization.Strings.SelectCategoryHint;

    public static string TipBypassRu => global::VPNRouter.Core.Localization.Strings.TipBypassRu;
    public static string TipLeakBlockOnFail => global::VPNRouter.Core.Localization.Strings.TipLeakBlockOnFail;
    public static string TipLeakStrictMode => global::VPNRouter.Core.Localization.Strings.TipLeakStrictMode;
    public static string TipLeakForceIpv4 => global::VPNRouter.Core.Localization.Strings.TipLeakForceIpv4;
    public static string TipLeakStrictDns => global::VPNRouter.Core.Localization.Strings.TipLeakStrictDns;
    public static string TipLeakFlushDns => global::VPNRouter.Core.Localization.Strings.TipLeakFlushDns;
    public static string TipBlockAds => global::VPNRouter.Core.Localization.Strings.TipBlockAds;

    public static string TipZapretAutoUpdate => global::VPNRouter.Core.Localization.Strings.TipZapretAutoUpdate;

    public static string TipFcFastScan => global::VPNRouter.Core.Localization.Strings.TipFcFastScan;
    public static string TipFcSmartRefresh => global::VPNRouter.Core.Localization.Strings.TipFcSmartRefresh;
    public static string TipFcSkipRu => global::VPNRouter.Core.Localization.Strings.TipFcSkipRu;

    public static string SmpToggleToAdvanced => global::VPNRouter.Core.Localization.Strings.SmpToggleToAdvanced;
    public static string SmpToggleToSimple => global::VPNRouter.Core.Localization.Strings.SmpToggleToSimple;
    public static string SmpToggleTooltip => global::VPNRouter.Core.Localization.Strings.SmpToggleTooltip;

    public static string SmpPlaceholderTitle => global::VPNRouter.Core.Localization.Strings.SmpPlaceholderTitle;
    public static string SmpPlaceholderBody => global::VPNRouter.Core.Localization.Strings.SmpPlaceholderBody;
    public static string SmpPlaceholderSwitchToAdvanced => global::VPNRouter.Core.Localization.Strings.SmpPlaceholderSwitchToAdvanced;

    public static string SmpInputLabel => global::VPNRouter.Core.Localization.Strings.SmpInputLabel;
    public static string SmpInputWatermark => global::VPNRouter.Core.Localization.Strings.SmpInputWatermark;
    public static string SmpInputHint => global::VPNRouter.Core.Localization.Strings.SmpInputHint;
    public static string SmpTunnelModeLabel => global::VPNRouter.Core.Localization.Strings.SmpTunnelModeLabel;
    public static string SmpSplitOption => global::VPNRouter.Core.Localization.Strings.SmpSplitOption;
    public static string SmpSplitHint => global::VPNRouter.Core.Localization.Strings.SmpSplitHint;
    public static string SmpFullOption => global::VPNRouter.Core.Localization.Strings.SmpFullOption;
    public static string SmpFullHint => global::VPNRouter.Core.Localization.Strings.SmpFullHint;
    public static string SmpAdvancedLink => global::VPNRouter.Core.Localization.Strings.SmpAdvancedLink;
    public static string SmpAdvancedHint => global::VPNRouter.Core.Localization.Strings.SmpAdvancedHint;
    public static string SmpChangeConfig => global::VPNRouter.Core.Localization.Strings.SmpChangeConfig;
    public static string SmpConnectedTitle => global::VPNRouter.Core.Localization.Strings.SmpConnectedTitle;
    public static string SmpDisconnectedTitle => global::VPNRouter.Core.Localization.Strings.SmpDisconnectedTitle;
    public static string SmpTipSplit => global::VPNRouter.Core.Localization.Strings.SmpTipSplit;
    public static string SmpTipFull => global::VPNRouter.Core.Localization.Strings.SmpTipFull;
    public static string SmpAutostartLabel => global::VPNRouter.Core.Localization.Strings.SmpAutostartLabel;
    public static string SmpTipAutostart => global::VPNRouter.Core.Localization.Strings.SmpTipAutostart;
    public static string SmpStartVpn => global::VPNRouter.Core.Localization.Strings.SmpStartVpn;
    public static string SmpStopVpn => global::VPNRouter.Core.Localization.Strings.SmpStopVpn;
    public static string SmpActiveThrough => global::VPNRouter.Core.Localization.Strings.SmpActiveThrough;

    public static string SmpStatusProtected => global::VPNRouter.Core.Localization.Strings.SmpStatusProtected;
    public static string SmpStatusConnecting => global::VPNRouter.Core.Localization.Strings.SmpStatusConnecting;
    public static string SmpStatusNotConnected => global::VPNRouter.Core.Localization.Strings.SmpStatusNotConnected;

    public static string SmpStatusConnectedVia => global::VPNRouter.Core.Localization.Strings.SmpStatusConnectedVia;
    public static string SmpStatusConnectedNoDetails => global::VPNRouter.Core.Localization.Strings.SmpStatusConnectedNoDetails;
    public static string SmpStatusConnectingHint => global::VPNRouter.Core.Localization.Strings.SmpStatusConnectingHint;
    public static string SmpStatusDisconnectedHint => global::VPNRouter.Core.Localization.Strings.SmpStatusDisconnectedHint;

    public static string SmpConfigRowLabel => global::VPNRouter.Core.Localization.Strings.SmpConfigRowLabel;
    public static string SmpCfgSubscribe => global::VPNRouter.Core.Localization.Strings.SmpCfgSubscribe;
    public static string SmpCfgManual => global::VPNRouter.Core.Localization.Strings.SmpCfgManual;
    public static string SmpCfgCustom => global::VPNRouter.Core.Localization.Strings.SmpCfgCustom;
    public static string SmpCfgSplit => global::VPNRouter.Core.Localization.Strings.SmpCfgSplit;
    public static string SmpCfgFull => global::VPNRouter.Core.Localization.Strings.SmpCfgFull;

    public static string SmpHeroAddConfigTitle => global::VPNRouter.Core.Localization.Strings.SmpHeroAddConfigTitle;
    public static string SmpHeroAddConfigHint => global::VPNRouter.Core.Localization.Strings.SmpHeroAddConfigHint;
    public static string SmpHeroErrorTitle => global::VPNRouter.Core.Localization.Strings.SmpHeroErrorTitle;
    public static string SmpSegSplit => global::VPNRouter.Core.Localization.Strings.SmpSegSplit;
    public static string SmpSegFull => global::VPNRouter.Core.Localization.Strings.SmpSegFull;
    public static string SmpKindServer => global::VPNRouter.Core.Localization.Strings.SmpKindServer;
    public static string SmpKindSubscription => global::VPNRouter.Core.Localization.Strings.SmpKindSubscription;
    public static string SmpKindCustom => global::VPNRouter.Core.Localization.Strings.SmpKindCustom;
    public static string SmpKindServers(int n) => global::VPNRouter.Core.Localization.Strings.SmpKindServers(n);

    public static string SmpCtaConnect => global::VPNRouter.Core.Localization.Strings.SmpCtaConnect;
    public static string SmpCtaDisconnect => global::VPNRouter.Core.Localization.Strings.SmpCtaDisconnect;
    public static string SmpCtaCancel => global::VPNRouter.Core.Localization.Strings.SmpCtaCancel;

    public static string SmpAdvCardTitle => global::VPNRouter.Core.Localization.Strings.SmpAdvCardTitle;
    public static string SmpAdvCardSubtitle => Ru
        ? "Серверы · Подписки · Zapret · Telegram-прокси · Публичные"
        : "Servers · Subscriptions · Zapret · Telegram proxy · Public";

    public static string SmpMenuTheme => global::VPNRouter.Core.Localization.Strings.SmpMenuTheme;
    public static string SmpMenuLanguage => global::VPNRouter.Core.Localization.Strings.SmpMenuLanguage;
    public static string SmpMenuOpenLogs => global::VPNRouter.Core.Localization.Strings.SmpMenuOpenLogs;
    public static string SmpMenuCheckLeaks => global::VPNRouter.Core.Localization.Strings.SmpMenuCheckLeaks;
    public static string SmpMenuCheckUpdates => global::VPNRouter.Core.Localization.Strings.SmpMenuCheckUpdates;
    public static string SmpMenuSwitchToAdv => global::VPNRouter.Core.Localization.Strings.SmpMenuSwitchToAdv;
    public static string DiagSupportHeader => global::VPNRouter.Core.Localization.Strings.DiagSupportHeader;
    public static string DiagExportButton => global::VPNRouter.Core.Localization.Strings.DiagExportButton;
    public static string DiagExporting => global::VPNRouter.Core.Localization.Strings.DiagExporting;
    public static string DiagExportHint => global::VPNRouter.Core.Localization.Strings.DiagExportHint;
    public static string SmpMenuHealthCheck => global::VPNRouter.Core.Localization.Strings.SmpMenuHealthCheck;
    public static string SmpMenuSetupWizard => global::VPNRouter.Core.Localization.Strings.SmpMenuSetupWizard;
    public static string SmpMenuSafeMode => global::VPNRouter.Core.Localization.Strings.SmpMenuSafeMode;
    public static string SmpMenuResetConfig => global::VPNRouter.Core.Localization.Strings.SmpMenuResetConfig;
    public static string SmpMenuResetConfirm => global::VPNRouter.Core.Localization.Strings.SmpMenuResetConfirm;
    public static string TipSmpMenuHealthCheck => global::VPNRouter.Core.Localization.Strings.TipSmpMenuHealthCheck;
    public static string TipSmpMenuSetupWizard => global::VPNRouter.Core.Localization.Strings.TipSmpMenuSetupWizard;
    public static string TipSmpMenuSafeMode => global::VPNRouter.Core.Localization.Strings.TipSmpMenuSafeMode;
    public static string TipSmpMenuResetConfig => global::VPNRouter.Core.Localization.Strings.TipSmpMenuResetConfig;

    public static string AutostartPlatformNotice => global::VPNRouter.Core.Localization.Strings.AutostartPlatformNotice;

    public static string SmpMenuViewSection => global::VPNRouter.Core.Localization.Strings.SmpMenuViewSection;
    public static string SmpMenuDiagnosticsSection => global::VPNRouter.Core.Localization.Strings.SmpMenuDiagnosticsSection;
    public static string SmpMenuTroubleshootingSection => global::VPNRouter.Core.Localization.Strings.SmpMenuTroubleshootingSection;
    public static string SmpSegLight => global::VPNRouter.Core.Localization.Strings.SmpSegLight;
    public static string SmpSegDark => global::VPNRouter.Core.Localization.Strings.SmpSegDark;
    public static string SmpSegSystem => global::VPNRouter.Core.Localization.Strings.SmpSegSystem;
    public static string SmpSegRu => global::VPNRouter.Core.Localization.Strings.SmpSegRu;
    public static string SmpSegEn => global::VPNRouter.Core.Localization.Strings.SmpSegEn;

    public static string LanguageSwitching => global::VPNRouter.Core.Localization.Strings.LanguageSwitching;

    public static string SmpMenuAbout => global::VPNRouter.Core.Localization.Strings.SmpMenuAbout;
    public static string TipSmpMenuAbout => global::VPNRouter.Core.Localization.Strings.TipSmpMenuAbout;
    public static string AboutTitle => global::VPNRouter.Core.Localization.Strings.AboutTitle;
    public static string AboutBrandName => global::VPNRouter.Core.Localization.Strings.AboutBrandName;
    public static string AboutTagline => global::VPNRouter.Core.Localization.Strings.AboutTagline;
    public static string AboutVersionLabel => global::VPNRouter.Core.Localization.Strings.AboutVersionLabel;
    public static string AboutSingBoxLabel => global::VPNRouter.Core.Localization.Strings.AboutSingBoxLabel;
    public static string AboutCreatorLabel => global::VPNRouter.Core.Localization.Strings.AboutCreatorLabel;
    public static string AboutRepoLabel => global::VPNRouter.Core.Localization.Strings.AboutRepoLabel;
    public static string AboutCloseBtn => global::VPNRouter.Core.Localization.Strings.AboutCloseBtn;

    public static string TipOpenLogs => global::VPNRouter.Core.Localization.Strings.TipOpenLogs;
    public static string TipIpLeak => global::VPNRouter.Core.Localization.Strings.TipIpLeak;

    public static string TipRemoveCategory => global::VPNRouter.Core.Localization.Strings.TipRemoveCategory;
    public static string TipRemoveApp => global::VPNRouter.Core.Localization.Strings.TipRemoveApp;

    public static string TipOpenFreeConfigLogs => global::VPNRouter.Core.Localization.Strings.TipOpenFreeConfigLogs;
    public static string TipClearFailed => global::VPNRouter.Core.Localization.Strings.TipClearFailed;
    public static string TipKeepVerifiedOnly => global::VPNRouter.Core.Localization.Strings.TipKeepVerifiedOnly;
    public static string TipClearAllCache => global::VPNRouter.Core.Localization.Strings.TipClearAllCache;

    public static string TipTcpTlsPing => global::VPNRouter.Core.Localization.Strings.TipTcpTlsPing;
    public static string TipTestTcpTls => global::VPNRouter.Core.Localization.Strings.TipTestTcpTls;
    public static string TipCloseServerDetail => global::VPNRouter.Core.Localization.Strings.TipCloseServerDetail;
    public static string TipDismiss => global::VPNRouter.Core.Localization.Strings.TipDismiss;
    public static string TipDeleteServer => global::VPNRouter.Core.Localization.Strings.TipDeleteServer;
    public static string TipTestAllServers => global::VPNRouter.Core.Localization.Strings.TipTestAllServers;
    public static string TipDeepVerifyServers => global::VPNRouter.Core.Localization.Strings.TipDeepVerifyServers;
    public static string TipRefreshSubscription => global::VPNRouter.Core.Localization.Strings.TipRefreshSubscription;
    public static string TipRemoveSubscription => global::VPNRouter.Core.Localization.Strings.TipRemoveSubscription;

    public static string LblName => global::VPNRouter.Core.Localization.Strings.LblName;
    public static string LblServer => global::VPNRouter.Core.Localization.Strings.LblServer;
    public static string LblPort => global::VPNRouter.Core.Localization.Strings.LblPort;
    public static string LblUuid => global::VPNRouter.Core.Localization.Strings.LblUuid;
    public static string LblPublicKey => global::VPNRouter.Core.Localization.Strings.LblPublicKey;
    public static string LblShortId => global::VPNRouter.Core.Localization.Strings.LblShortId;

    public static string LblRoutingMode => global::VPNRouter.Core.Localization.Strings.LblRoutingMode;
    public static string LblNoServers => global::VPNRouter.Core.Localization.Strings.LblNoServers;
    public static string LblAddSubscriptionHint => global::VPNRouter.Core.Localization.Strings.LblAddSubscriptionHint;

    public static string LblCustomBadge => global::VPNRouter.Core.Localization.Strings.LblCustomBadge;

    public static string WmZapretCustomArgs => global::VPNRouter.Core.Localization.Strings.WmZapretCustomArgs;
    public static string WmVlessUri => global::VPNRouter.Core.Localization.Strings.WmVlessUri;
    public static string WmTgProxyPort => global::VPNRouter.Core.Localization.Strings.WmTgProxyPort;
    public static string WmTgProxySecret => global::VPNRouter.Core.Localization.Strings.WmTgProxySecret;

    public static string StatusStopped => global::VPNRouter.Core.Localization.Strings.StatusStopped;
    public static string StatusRunning => global::VPNRouter.Core.Localization.Strings.StatusRunning;

    public static string CurrentVersion => global::VPNRouter.Core.Localization.Strings.CurrentVersion;

    public static string CustomConfigsEmptyTitle => global::VPNRouter.Core.Localization.Strings.CustomConfigsEmptyTitle;
    public static string CustomConfigsEmptyHint => global::VPNRouter.Core.Localization.Strings.CustomConfigsEmptyHint;

    public static string SettingsRecoveredFromBadConfig(string backupPath) => global::VPNRouter.Core.Localization.Strings.SettingsRecoveredFromBadConfig(backupPath);

    public static string PlaceholderPruneBanner => global::VPNRouter.Core.Localization.Strings.PlaceholderPruneBanner;

    public static string PlaceholderPruneBannerAllGone => global::VPNRouter.Core.Localization.Strings.PlaceholderPruneBannerAllGone;

    public static string ConflictOtherVpnDetectedTitle => global::VPNRouter.Core.Localization.Strings.ConflictOtherVpnDetectedTitle;

    public static string ConflictOtherVpnDetectedMessage(string processName, int pid) => global::VPNRouter.Core.Localization.Strings.ConflictOtherVpnDetectedMessage(processName, pid);

    public static string ConflictRefreshButton => global::VPNRouter.Core.Localization.Strings.ConflictRefreshButton;

    public static string ConflictKillButton => Ru ? "Завершить" : "Kill";

    public static string ConflictKillTooltip => Ru
        ? "Принудительно завершить конфликтующий VPN-процесс."
        : "Force-terminate the conflicting VPN process.";

    public static string ConflictKillPartialFailure(int killed, int failed) => Ru
        ? $"Завершено: {killed}. Не удалось: {failed}. " +
          "Запустите VPNRouter от имени администратора или закройте процесс вручную через Диспетчер задач."
        : $"Killed: {killed}. Failed: {failed}. " +
          "Run VPNRouter as administrator, or close the process manually via Task Manager.";

    public static string ConflictIgnoreButton => Ru ? "Игнорировать" : "Ignore";

    public static string ConflictIgnoreTooltip => Ru
        ? "Игнорировать предупреждение и попытаться подключиться. " +
          "Полезно если другой VPN запущен, но не подключён."
        : "Ignore the warning and try to connect anyway. " +
          "Useful when the other VPN is running but not connected.";

    public static string ZapretAvBlockToast => global::VPNRouter.Core.Localization.Strings.ZapretAvBlockToast;

    public static string ZapretAvBlockCopyPath => global::VPNRouter.Core.Localization.Strings.ZapretAvBlockCopyPath;
}
