namespace VPNRouter.App.Localization;

/// <summary>
/// Bilingual string provider (EN/RU). Port of VPNRouter.GUI.Localization.Strings for Avalonia.
/// </summary>
public static class Strings
{
    // Settable Lang shim — keeps legacy "Strings.Lang = ..." call sites
    // working while delegating to Core's single source of truth. Reads + writes
    // pass through, so any VM that flips this also flips the shared Core copy.
    public static string Lang
    {
        get => global::VPNRouter.Core.Localization.Strings.Lang;
        set => global::VPNRouter.Core.Localization.Strings.Lang = value;
    }
    private static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    // v2.29.0: dynamic OS name shown in user-facing autostart copy. Mac
    // users were seeing "Windows" hardcoded in Simple-mode autostart card
    // and Network → Autostart labels (reported 2026-04-29). Now Strings
    // detect runtime platform and substitute "macOS" / "Linux" / "Windows"
    // into RU+EN templates. Does NOT change Windows-Service-tech labels
    // (those reference an actual Windows-only API surface).
    public static string OsDisplayName => global::VPNRouter.Core.Localization.Strings.OsDisplayName;

    // ── Tabs ──
    public static string TabServers => global::VPNRouter.Core.Localization.Strings.TabServers;
    public static string TabApps => global::VPNRouter.Core.Localization.Strings.TabApps;
    public static string TabNetwork => global::VPNRouter.Core.Localization.Strings.TabNetwork;
    public static string TabSettings => global::VPNRouter.Core.Localization.Strings.TabSettings;
    public static string TabZapret => global::VPNRouter.Core.Localization.Strings.TabZapret;
    // v2.30.6-r1 (UX-46 fix): sub-tab + every reference elsewhere in app
    // ("Telegram-прокси" in Simple-mode hints, ServiceMasterSubtitle,
    // AutostartBootSectionSub, etc.) used the user-friendly name. Sub-tab
    // and all VM labels (LblTabTelegram / LblToolTgProxy) read from this.
    public static string TabTgWsProxy => global::VPNRouter.Core.Localization.Strings.TabTgWsProxy;

    // ── Config mode ──
    // v2.30.1-r3: was "VLESS Серверы" / "VLESS Servers". Renamed to plain
    // "Серверы" / "Servers" — the sub-tab is no longer VLESS-specific
    // conceptually; future protocol support (Hysteria2, TUIC, SS2022)
    // would also live here, so the VLESS prefix would be misleading.
    public static string VlessServers => global::VPNRouter.Core.Localization.Strings.VlessServers;
    public static string CustomConfigJson => global::VPNRouter.Core.Localization.Strings.CustomConfigJson;
    public static string ModeManual => global::VPNRouter.Core.Localization.Strings.ModeManual;
    public static string ModeSubscribe => global::VPNRouter.Core.Localization.Strings.ModeSubscribe;
    public static string ModeCustomConfig => global::VPNRouter.Core.Localization.Strings.ModeCustomConfig;
    public static string SubscribeMode => global::VPNRouter.Core.Localization.Strings.SubscribeMode;
    // v2.30.5-r1 (UX-34 fix): drop the EN duplicate inside RU placeholder.
    // Pre-r1 was "URL подписки (subscription link)" — same translation
    // shown twice. Now just "URL подписки".
    public static string SubscriptionUrlHint => global::VPNRouter.Core.Localization.Strings.SubscriptionUrlHint;
    public static string SyncButton => global::VPNRouter.Core.Localization.Strings.SyncButton;
    public static string Syncing => global::VPNRouter.Core.Localization.Strings.Syncing;
    public static string SyncComplete(int count) => global::VPNRouter.Core.Localization.Strings.SyncComplete(count);
    public static string SyncFailed(string err) => global::VPNRouter.Core.Localization.Strings.SyncFailed(err);
    public static string SyncEmpty => global::VPNRouter.Core.Localization.Strings.SyncEmpty;
    public static string PasteVlessUri => global::VPNRouter.Core.Localization.Strings.PasteVlessUri;

    // ── Buttons ──
    public static string StartVPN => global::VPNRouter.Core.Localization.Strings.StartVPN;
    public static string StopVPN => global::VPNRouter.Core.Localization.Strings.StopVPN;
    public static string AddServers => global::VPNRouter.Core.Localization.Strings.AddServers;
    public static string Remove => global::VPNRouter.Core.Localization.Strings.Remove;
    public static string AddConfig => global::VPNRouter.Core.Localization.Strings.AddConfig;
    public static string Apply => global::VPNRouter.Core.Localization.Strings.Apply;
    public static string BtnAdd => global::VPNRouter.Core.Localization.Strings.BtnAdd;
    public static string RemoveChecked => global::VPNRouter.Core.Localization.Strings.RemoveChecked;

    // ── Apps tab ──
    public static string SplitTunnel => global::VPNRouter.Core.Localization.Strings.SplitTunnel;
    public static string FullTunnel => global::VPNRouter.Core.Localization.Strings.FullTunnel;
    public static string AppsHint => global::VPNRouter.Core.Localization.Strings.AppsHint;
    public static string CustomAppLabel => global::VPNRouter.Core.Localization.Strings.CustomAppLabel;
    // v2.30.6-r1 (UX-41 fix): bilingual button label for the Apps tab
    // "+ Add" button. Pre-r1 was hardcoded EN string in
    // ApplicationsPage.axaml (D1 rule violation).
    public static string AddCustomAppBtn => global::VPNRouter.Core.Localization.Strings.AddCustomAppBtn;

    // ── Header ──
    public static string ThemeDark => global::VPNRouter.Core.Localization.Strings.ThemeDark;
    public static string ThemeLight => global::VPNRouter.Core.Localization.Strings.ThemeLight;

    // ── Status ──
    public static string NotConnected => global::VPNRouter.Core.Localization.Strings.NotConnected;
    public static string Connected(string mode, string? serverName, string? serverIp)
    {
        var prefix = Ru ? $"Подключено [{mode}]" : $"Connected [{mode}]";
        if (string.IsNullOrEmpty(serverName) && string.IsNullOrEmpty(serverIp))
            return prefix;
        var name = serverName ?? "";
        var ip = string.IsNullOrEmpty(serverIp) ? "" : $" ({serverIp})";
        return $"{prefix} → {name}{ip}";
    }

    // v2.44.1-r6 — connected-status label shown when AutoSelectBestServer's
    // urltest hasn't yet reported which member it picked (brief clash_api race
    // right after connect). Once resolved, the real server name replaces it.
    public static string AutoSelectStatusLabel => Ru ? "авто-выбор" : "auto-select";

    // W1.3 — "True split" badge shown in the status zone when the kernel split-tunnel driver is
    // ENGAGED (exclude-mode on Windows). Tooltip lists the honest bind-redirect caveats (goal Scope).
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
    public static string TrueSplitDeviceBusy => Ru
        ? "True Split не запустился: split-драйвер занят Amnezia/Mullvad/VPNRouter Service (err=5). VPNRouter не будет останавливать чужой kernel driver автоматически; закройте тот VPN, отключите его split tunneling и перезагрузите Windows."
        : "True Split did not start: the split driver is held by Amnezia/Mullvad/VPNRouter Service (err=5). VPNRouter will not stop another kernel driver automatically; close that VPN, disable its split tunneling, and reboot Windows.";
    public static string TrueSplitServiceManaged => Ru
        ? "VPN запущен службой Windows. True Split контролирует служба; чтобы перезапустить его вручную, остановите VPN и запустите его из приложения."
        : "VPN is running in the Windows Service. True Split is controlled by the service; stop VPN and start it from the app to retry manually.";
    public static string TrueSplitNotApplicable => Ru
        ? "True Split доступен только для списка «Мимо VPN»."
        : "True Split applies only to the bypass list.";

    // ── Action states ──
    public static string Starting => global::VPNRouter.Core.Localization.Strings.Starting;
    public static string Stopping => global::VPNRouter.Core.Localization.Strings.Stopping;
    // v2.37.0-r7 — pass-through to Core for idle/quiescent status text
    // (RU "Остановлен" / EN "Stopped"). Used by Zapret + TgProxy status fields.
    public static string Stopped => global::VPNRouter.Core.Localization.Strings.Stopped;

    // v2.37.0-r18 — RuntimeStatus tooltip + Subscriptions status pass-throughs.
    public static string BadgeTooltipVpn => global::VPNRouter.Core.Localization.Strings.BadgeTooltipVpn;
    public static string BadgeTooltipZapret => global::VPNRouter.Core.Localization.Strings.BadgeTooltipZapret;
    public static string BadgeTooltipTgProxy => global::VPNRouter.Core.Localization.Strings.BadgeTooltipTgProxy;
    public static string SubscriptionEnterUrl => global::VPNRouter.Core.Localization.Strings.SubscriptionEnterUrl;
    public static string SubscriptionCleared => global::VPNRouter.Core.Localization.Strings.SubscriptionCleared;

    // v2.37.0-r17 — ServerTesting label pass-throughs.
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

    // v2.37.0-r16 — TgProxy stats label pass-throughs.
    public static string TgProxyStatsActive => global::VPNRouter.Core.Localization.Strings.TgProxyStatsActive;
    public static string TgProxyStatsTotal => global::VPNRouter.Core.Localization.Strings.TgProxyStatsTotal;

    // v2.37.0-r14 — more inline-ternary localization pass-throughs.
    public static string RuleParserMissingValue => global::VPNRouter.Core.Localization.Strings.RuleParserMissingValue;
    public static string RuleParserUnknownType(string type) => global::VPNRouter.Core.Localization.Strings.RuleParserUnknownType(type);
    public static string RulesAllDeleted => global::VPNRouter.Core.Localization.Strings.RulesAllDeleted;
    public static string RulesAlreadySorted => global::VPNRouter.Core.Localization.Strings.RulesAlreadySorted;
    public static string RulesEmptyValue => global::VPNRouter.Core.Localization.Strings.RulesEmptyValue;
    public static string ClickToActivateConfig => global::VPNRouter.Core.Localization.Strings.ClickToActivateConfig;

    // v2.37.0-r13 — Custom Rules type-help pass-throughs (moved from
    // inline IsRussian-ternaries in MainWindowViewModel switch arms).
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

    // v2.37.0-r21 — probe info + direct-start pass-throughs.
    public static string ZapretProbeElapsedAndEta(int elapsedSec, int? etaSec) => global::VPNRouter.Core.Localization.Strings.ZapretProbeElapsedAndEta(elapsedSec, etaSec);
    public static string ZapretStartSelectedStrategyButton => global::VPNRouter.Core.Localization.Strings.ZapretStartSelectedStrategyButton;
    public static string ZapretStartSelectedStrategyHint => global::VPNRouter.Core.Localization.Strings.ZapretStartSelectedStrategyHint;
    public static string ZapretStartingSelected(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretStartingSelected(strategy);
    public static string ZapretRunningSelected(string strategy, int pid) => global::VPNRouter.Core.Localization.Strings.ZapretRunningSelected(strategy, pid);
    public static string ZapretSelectedStrategyFailed(string strategy) => global::VPNRouter.Core.Localization.Strings.ZapretSelectedStrategyFailed(strategy);

    // v2.37.0-r10 — Zapret cache UI pass-throughs.
    public static string ZapretForceFreshProbeButton => global::VPNRouter.Core.Localization.Strings.ZapretForceFreshProbeButton;
    public static string ZapretClearCacheButton => global::VPNRouter.Core.Localization.Strings.ZapretClearCacheButton;
    public static string ZapretCacheCleared => global::VPNRouter.Core.Localization.Strings.ZapretCacheCleared;
    public static string ZapretCacheInfo(string strategy, int successCount) => global::VPNRouter.Core.Localization.Strings.ZapretCacheInfo(strategy, successCount);
    public static string ZapretCacheEmpty => global::VPNRouter.Core.Localization.Strings.ZapretCacheEmpty;

    // v2.37.0-r24 — Hero strategy summary card pass-throughs.
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

    // v2.37.0-r9 — Custom Rules import/export pass-throughs.
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

    // Task #41 Stage 2 (PinkuDani 2026-05-21) — two-phase Start timer diagnostics.
    public static string StartTimeoutPhaseA => global::VPNRouter.Core.Localization.Strings.StartTimeoutPhaseA;
    public static string StartTimeoutPhaseB => global::VPNRouter.Core.Localization.Strings.StartTimeoutPhaseB;

    // ── Server list columns ──
    public static string ColName => global::VPNRouter.Core.Localization.Strings.ColName;
    public static string ColServer => global::VPNRouter.Core.Localization.Strings.ColServer;
    public static string ColPort => global::VPNRouter.Core.Localization.Strings.ColPort;
    public static string ColSecurity => global::VPNRouter.Core.Localization.Strings.ColSecurity;
    // v2.25.3 — extra column labels for the redesigned Servers / Subscribe rows
    public static string ColIp => global::VPNRouter.Core.Localization.Strings.ColIp;
    // app-only: Core has bilingual "Пинг"/"Ping" (Bug-AND-016 fix), App stayed
    // EN-only. Classified as TEXT-DRIFT to avoid silent behaviour change in
    // this dedup pass. Follow-up: switch to pass-through (Core's bilingual
    // version is strictly better) once we verify no Windows-test snapshot pins
    // the EN-only string.
    public static string ColPing => "Ping";
    // v2.30.6-r1 (UX-23/32 fix): tooltip on Ping column header — explains
    // the "—" placeholder users see before any test has been run.
    public static string ColPingTooltip => global::VPNRouter.Core.Localization.Strings.ColPingTooltip;

    // Server protocol use-case chips shown in Servers / Subscription lists.
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

    // v2.25.4 — Settings/Routing radio-card descriptions (Phase 4 redesign).
    // Each tunnel mode gets a one-line subtitle under the title so the user
    // understands the choice without hovering for a tooltip.
    public static string RoutingDescription => global::VPNRouter.Core.Localization.Strings.RoutingDescription;
    // v2.30.3-r1 (UX-9 D1 rule): localize tunnel mode titles. Previous
    // pre-r1 used hardcoded English in both locales which violated the
    // "no English in RU UI" project rule.
    public static string SplitTunnelTitle => global::VPNRouter.Core.Localization.Strings.SplitTunnelTitle;
    public static string SplitTunnelSubtitle => global::VPNRouter.Core.Localization.Strings.SplitTunnelSubtitle;
    public static string FullTunnelTitle => global::VPNRouter.Core.Localization.Strings.FullTunnelTitle;
    public static string FullTunnelSubtitle => global::VPNRouter.Core.Localization.Strings.FullTunnelSubtitle;

    // Service actions in Settings → Autostart (moved here from the footer
    // when MainWindow compacted its footer in v2.25.0).
    public static string ServiceStatusLabel => global::VPNRouter.Core.Localization.Strings.ServiceStatusLabel;
    public static string ServiceRunningText => global::VPNRouter.Core.Localization.Strings.ServiceRunningText;
    public static string ServiceStoppedText => global::VPNRouter.Core.Localization.Strings.ServiceStoppedText;
    public static string ServiceInstalledText => global::VPNRouter.Core.Localization.Strings.ServiceInstalledText;
    public static string ServiceNotInstalledText => global::VPNRouter.Core.Localization.Strings.ServiceNotInstalledText;

    // v2.26.0 — master service toggle + grouping labels for the refactored
    // Autostart panel (single source of truth for the install state +
    // clearly-named sub-groups for the two categories of autostart).
    public static string ServiceMasterTitle => global::VPNRouter.Core.Localization.Strings.ServiceMasterTitle;
    public static string ServiceMasterSubtitle => global::VPNRouter.Core.Localization.Strings.ServiceMasterSubtitle;
    public static string ServiceEnableLabel => global::VPNRouter.Core.Localization.Strings.ServiceEnableLabel;
    public static string ServiceInstalling => global::VPNRouter.Core.Localization.Strings.ServiceInstalling;
    public static string ServiceRemoving => global::VPNRouter.Core.Localization.Strings.ServiceRemoving;
    public static string ServiceComponentsHeader => global::VPNRouter.Core.Localization.Strings.ServiceComponentsHeader;
    public static string ServiceComponentsDisabledHint => global::VPNRouter.Core.Localization.Strings.ServiceComponentsDisabledHint;
    public static string AutostartUiSessionHeader => global::VPNRouter.Core.Localization.Strings.AutostartUiSessionHeader;

    // v2.27 Bug C — two-section layout for the Autostart panel, grouping
    // controls by WHEN the autostart happens rather than by which Windows
    // mechanism it's wired to. Makes "I want VPN on boot" actionable via a
    // single checkbox instead of forcing users to understand service vs.
    // Run-key vs. yaml flag.
    public static string AutostartBootSectionTitle => global::VPNRouter.Core.Localization.Strings.AutostartBootSectionTitle;
    public static string AutostartBootSectionSub => global::VPNRouter.Core.Localization.Strings.AutostartBootSectionSub;
    public static string AutostartComponentsInfoHint => global::VPNRouter.Core.Localization.Strings.AutostartComponentsInfoHint;
    // v2.31.10 (autostart UX clarity): per-component status badge text shown
    // below each VPN/Zapret/TgProxy autostart CheckBox. User report — "Auto-
    // start with Windows for tgproxy doesn't work". Without a status indicator
    // a user toggling AutostartTgProxy=true on a host without the Service has
    // no way to learn that the toggle is a no-op. Three states cover every
    // permutation of (Service installed?, App-side bootstrap exists?):
    //   • Green ✓: service installed → the existing flag-driven boot path
    //     in VPNRouterService.AutostartTgProxyAsync handles it
    //   • Amber ⚠: no service, but App has a per-component bootstrap (after
    //     DBG-2 lands the App-side bootstrap for vpn/zapret/tgproxy) → fires
    //     when the user logs into the App, not at OS boot
    //   • Red ⛔: no service AND no App-side bootstrap → the toggle does
    //     literally nothing; show the strongest hint to install the service
    public static string AutostartStatusBoot => global::VPNRouter.Core.Localization.Strings.AutostartStatusBoot;
    public static string AutostartStatusLoginFallback => global::VPNRouter.Core.Localization.Strings.AutostartStatusLoginFallback;
    public static string AutostartStatusNoBoot => global::VPNRouter.Core.Localization.Strings.AutostartStatusNoBoot;
    // v2.31.1-r1 (F-4 / UX-6): inline CTA below the warning hint when the
    // service isn't installed — pre-fix the only way to install was scrolling
    // up to the master toggle, which wasn't obvious.
    public static string BtnInstallServiceInlineCta => global::VPNRouter.Core.Localization.Strings.BtnInstallServiceInlineCta;
    public static string TipInstallServiceInlineCta => global::VPNRouter.Core.Localization.Strings.TipInstallServiceInlineCta;
    // v2.31.1-r1 (F-6 / UX-33): tooltip explaining the subscription card
    // metadata format `URL · Ns · refreshed-time`. Pre-fix users wondered
    // what "7s · –" meant — the "s" plural marker on server count read as
    // a time unit and the "–" was opaque.
    public static string TipSubscriptionMetadata => global::VPNRouter.Core.Localization.Strings.TipSubscriptionMetadata;
    public static string AutostartLoginSectionTitle => global::VPNRouter.Core.Localization.Strings.AutostartLoginSectionTitle;
    // v2.29.0: section A "At system startup (before sign-in)" only renders
    // on Windows (Service-based, no Mac/Linux equivalent yet); section B
    // description should not reference it on Mac/Linux. Pre-r2 EN+RU
    // pointed at "above" assuming Section A was visible, which broke on
    // Mac. Now branches by OS.
    public static string AutostartLoginAppDescription => global::VPNRouter.Core.Localization.Strings.AutostartLoginAppDescription;

    private static string AutostartLoginAppDescriptionUnix => Ru
        ? "Запускает приложение VPNRouter в трей после входа в систему. VPN придётся стартануть вручную."
        : "Launches VPNRouter into the tray after you sign in. VPN itself must be started manually.";

    private static string AutostartLoginAppDescriptionWindows => Ru
        ? "Запускает приложение VPNRouter после входа. VPN придётся стартануть вручную или включить «на старте Windows» выше."
        : "Launches VPNRouter after you sign in. VPN itself must be started manually, or enable \u201Cat Windows startup\u201D above.";

    /// <summary>Prominent running-state line with PID, e.g. "● Running — PID 1234".
    /// Replaces the tiny pill that was easy to miss in v2.26.x.</summary>
    public static string ServiceRunningLine(int pid) => global::VPNRouter.Core.Localization.Strings.ServiceRunningLine(pid);
    public static string ServiceStoppedLine => global::VPNRouter.Core.Localization.Strings.ServiceStoppedLine;

    // v2.27.0-r2 — Simple-mode autostart link-card. Replaces the old
    // SmpAutostartChecked checkbox whose computed-state UX caused the
    // "how do I disable it?" confusion in r1 testing. The card now just
    // navigates into Advanced → Network → Autostart where the full flow
    // (install / configure / uninstall) lives.
    public static string SmpAutostartCardTitle => global::VPNRouter.Core.Localization.Strings.SmpAutostartCardTitle;
    public static string SmpAutostartCardOn => global::VPNRouter.Core.Localization.Strings.SmpAutostartCardOn;
    public static string SmpAutostartCardOff => global::VPNRouter.Core.Localization.Strings.SmpAutostartCardOff;

    // ── Dialogs ──
    public static string FailedStartVpn => global::VPNRouter.Core.Localization.Strings.FailedStartVpn;
    public static string AddServerFirst => global::VPNRouter.Core.Localization.Strings.AddServerFirst;
    public static string SelectSingBoxConfig => global::VPNRouter.Core.Localization.Strings.SelectSingBoxConfig;
    public static string InvalidConfig => global::VPNRouter.Core.Localization.Strings.InvalidConfig;
    public static string ConfigExists(string name) => global::VPNRouter.Core.Localization.Strings.ConfigExists(name);

    // ── Tray ──
    public static string TrayStart => global::VPNRouter.Core.Localization.Strings.TrayStart;
    public static string TrayStop => global::VPNRouter.Core.Localization.Strings.TrayStop;
    public static string TraySettings => global::VPNRouter.Core.Localization.Strings.TraySettings;
    public static string TrayExit => global::VPNRouter.Core.Localization.Strings.TrayExit;

    // ── Server detail editor ──
    public static string FieldName => global::VPNRouter.Core.Localization.Strings.FieldName;
    public static string FieldServer => global::VPNRouter.Core.Localization.Strings.FieldServer;
    public static string FieldPort => global::VPNRouter.Core.Localization.Strings.FieldPort;
    public static string FieldUuid => global::VPNRouter.Core.Localization.Strings.FieldUuid;
    public static string FieldPublicKey => global::VPNRouter.Core.Localization.Strings.FieldPublicKey;
    public static string FieldShortId => global::VPNRouter.Core.Localization.Strings.FieldShortId;

    // ── Hints ──
    public static string DoubleClickEditServer => global::VPNRouter.Core.Localization.Strings.DoubleClickEditServer;
    public static string DoubleClickActiveConfig => global::VPNRouter.Core.Localization.Strings.DoubleClickActiveConfig;
    public static string AddCustomAppHint => global::VPNRouter.Core.Localization.Strings.AddCustomAppHint;
    // v2.30.6-r1 (UX-25 fix): drop EN "Custom Config" + "outbound" inside
    // the otherwise-Russian hint. Use natural RU "своим конфигом" +
    // "исходящим" so the sentence reads cleanly in both languages.
    public static string TcpUdpHint => global::VPNRouter.Core.Localization.Strings.TcpUdpHint;

    // ── Bypass / Strict ──
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
    public static string ForceIpv4Label => global::VPNRouter.Core.Localization.Strings.ForceIpv4Label;
    public static string FlushDnsLabel => global::VPNRouter.Core.Localization.Strings.FlushDnsLabel;
    // v2.31.6-r18: hint expanded — user feedback iter#7 audit asked
    // why ISP DNS sometimes appears in browserleaks.com / ipleak.net.
    // Default split-tunnel sends non-routed apps' DNS through Cloudflare
    // DoH on the real NIC (not ISP, but leak-tests interpret "Cloudflare
    // DoH client = real IP" as a leak). Strict DNS forces all DNS through
    // the VPN tunnel for that perfect-on-tests outcome.
    public static string StrictDnsLabel => global::VPNRouter.Core.Localization.Strings.StrictDnsLabel;
    // Wave 39 (v2.35.0-r5) — firewall-level DNS lockdown setting.
    public static string DnsLeakLockdownLabel => global::VPNRouter.Core.Localization.Strings.DnsLeakLockdownLabel;
    public static string DnsLeakLockdownUnavailableNote => global::VPNRouter.Core.Localization.Strings.DnsLeakLockdownUnavailableNote;
    public static string TipDnsLeakLockdown => global::VPNRouter.Core.Localization.Strings.TipDnsLeakLockdown;

    // ── Updates ──
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

    // ── Channel ──
    // v2.30.3-r1 (BUG-7 fix): footer text shortened so it fits next to
    // the Apply button at narrow window widths (510 px) without
    // overlapping. Pre-r1 the auto-save hint was 44 chars + 38-char
    // button = visible truncation behind the button background.
    public static string SettingsAutosaved => global::VPNRouter.Core.Localization.Strings.SettingsAutosaved;
    public static string ApplyNowReloadVpn => global::VPNRouter.Core.Localization.Strings.ApplyNowReloadVpn;
    public static string ApplyNowHint => global::VPNRouter.Core.Localization.Strings.ApplyNowHint;

    public static string ChannelStable => global::VPNRouter.Core.Localization.Strings.ChannelStable;
    public static string ChannelExperimental => global::VPNRouter.Core.Localization.Strings.ChannelExperimental;

    // ── Telegram Proxy ──
    public static string TabTelegram => global::VPNRouter.Core.Localization.Strings.TabTelegram;
    public static string TgProxyDescription => global::VPNRouter.Core.Localization.Strings.TgProxyDescription;
    public static string TgProxySetupHint => global::VPNRouter.Core.Localization.Strings.TgProxySetupHint;
    public static string TgProxyPort => global::VPNRouter.Core.Localization.Strings.TgProxyPort;
    public static string TgProxySecret => global::VPNRouter.Core.Localization.Strings.TgProxySecret;
    public static string TgProxyLink => global::VPNRouter.Core.Localization.Strings.TgProxyLink;
    public static string TgProxyCopy => global::VPNRouter.Core.Localization.Strings.TgProxyCopy;
    public static string TgProxyCopied => global::VPNRouter.Core.Localization.Strings.TgProxyCopied;
    public static string TgProxyRegenerate => global::VPNRouter.Core.Localization.Strings.TgProxyRegenerate;
    // v2.30.7-r4 — F-16 fix: was "Запустить Telegram Proxy" / "Остановить Telegram Proxy"
    // — mixed-case "Telegram Proxy" inside RU sentence (D1 violation) +
    // inconsistent with the sub-tab name "Telegram-прокси" (with hyphen, lowercase).
    // Aligned both labels with the canonical sub-tab name.
    public static string TgProxyStart => global::VPNRouter.Core.Localization.Strings.TgProxyStart;
    public static string TgProxyStop => global::VPNRouter.Core.Localization.Strings.TgProxyStop;
    public static string TgProxyOpenInTelegram => global::VPNRouter.Core.Localization.Strings.TgProxyOpenInTelegram;

    // v2.31.6-r5 (TG-2) — unified footer action label per user feedback
    // 2026-05-03 night: «запуск прокси и открыть телеграм нужно объединить,
    // сейчас они очень далеко». Footer becomes the primary CTA on first run
    // (download → start → open-in-Telegram in one click) so the user no
    // longer plays "click body button + click footer button" two-step.
    // The body button demotes to a secondary "re-pair" fallback for
    // sessions where Telegram client lost the proxy entry.
    public static string TgProxyStartAndOpen => global::VPNRouter.Core.Localization.Strings.TgProxyStartAndOpen;
    public static string TgProxySetupOnce => global::VPNRouter.Core.Localization.Strings.TgProxySetupOnce;

    // v2.31.6-r9 — purged 5 unused TgProxySetup* + TgProxyClientAutoHint
    // + TgProxyAdvanced strings that were added in v2.31.6-r1's two-state
    // setup-cascade but dropped in r3 (full redo per design handoff cell 6).
    // The XAML referenced them via `L_TgProxySetupCta` etc. only in r1/r2;
    // r3+ pages no longer bind them. Iter#4 audit confirmed zero XAML hits.
    // TgProxyReopenInTelegram below is the only string from that batch
    // still in use (TelegramPage body button label).
    public static string TgProxyReopenInTelegram => global::VPNRouter.Core.Localization.Strings.TgProxyReopenInTelegram;

    // v2.31.6-r9 — A11y: full-sentence announcements for short
    // button labels («Copy» / «New») that screen readers can't
    // disambiguate without context. Used in
    // <c>AutomationProperties.Name</c> bindings on the secret-row
    // buttons in TelegramPage. Visible button text stays short
    // («Copy» / «New») per the design — only Narrator/VoiceOver
    // hears the longer phrase.
    public static string TgProxyCopySecretA11y => global::VPNRouter.Core.Localization.Strings.TgProxyCopySecretA11y;
    public static string TgProxyRegenerateSecretA11y => global::VPNRouter.Core.Localization.Strings.TgProxyRegenerateSecretA11y;

    // v2.36 (MVP one-button) — delegate getters for new strings.
    public static string TgProxyPortBusy => global::VPNRouter.Core.Localization.Strings.TgProxyPortBusy;
    public static string TgProxyPortBusyWithOwner => global::VPNRouter.Core.Localization.Strings.TgProxyPortBusyWithOwner;
    public static string TgProxySchemeMissingWarning => global::VPNRouter.Core.Localization.Strings.TgProxySchemeMissingWarning;
    public static string TgProxyDownloadStep1Python => global::VPNRouter.Core.Localization.Strings.TgProxyDownloadStep1Python;
    public static string TgProxyDownloadStep2Wheels => global::VPNRouter.Core.Localization.Strings.TgProxyDownloadStep2Wheels;
    public static string TgProxyDownloadStep3Source => global::VPNRouter.Core.Localization.Strings.TgProxyDownloadStep3Source;

    // v2.36.0-r7 — TgProxyOneTap hero copy (variant A · Centered stack).
    public static string TgProxyOneTapTitleStopped => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapTitleStopped;
    public static string TgProxyOneTapTitleRunning => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapTitleRunning;
    public static string TgProxyOneTapLedeStopped  => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapLedeStopped;
    public static string TgProxyOneTapLedeRunning(int port) => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapLedeRunning(port);
    public static string TgProxyOneTapStep1 => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapStep1;
    public static string TgProxyOneTapStep2 => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapStep2;
    public static string TgProxyOneTapStep3 => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapStep3;
    public static string TgProxyOneTapTune  => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapTune;
    public static string TgProxyOneTapAirPill(int port) => global::VPNRouter.Core.Localization.Strings.TgProxyOneTapAirPill(port);

    // v2.37.0-r25 — TgProxy TabControl tab-header pass-throughs.
    public static string TgProxyTabSettings => global::VPNRouter.Core.Localization.Strings.TgProxyTabSettings;
    public static string TgProxyTabVersion  => global::VPNRouter.Core.Localization.Strings.TgProxyTabVersion;
    public static string TgProxyTabHelp     => global::VPNRouter.Core.Localization.Strings.TgProxyTabHelp;

    // v2.36.0-r8 — ZapretOneTap hero copy (variant A · Centered stack).
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

    // ── Autostart ──
    // ── Subscriptions (multi) ──
    public static string SubscriptionsSection => global::VPNRouter.Core.Localization.Strings.SubscriptionsSection;
    public static string SubscriptionNameHint => global::VPNRouter.Core.Localization.Strings.SubscriptionNameHint;
    public static string AddSubscription => global::VPNRouter.Core.Localization.Strings.AddSubscription;
    public static string RefreshAll => global::VPNRouter.Core.Localization.Strings.RefreshAll;
    public static string NeverRefreshed => global::VPNRouter.Core.Localization.Strings.NeverRefreshed;
    public static string SubUpdatedAt => global::VPNRouter.Core.Localization.Strings.SubUpdatedAt;

    // ── Zapret tools ──
    public static string ToolsSection => global::VPNRouter.Core.Localization.Strings.ToolsSection;
    public static string RunDiagnostics => global::VPNRouter.Core.Localization.Strings.RunDiagnostics;
    public static string ClearDiscordCache => global::VPNRouter.Core.Localization.Strings.ClearDiscordCache;
    public static string UpdateHostsFile => global::VPNRouter.Core.Localization.Strings.UpdateHostsFile;
    public static string OpenServiceMenu => global::VPNRouter.Core.Localization.Strings.OpenServiceMenu;
    // v2.31.0-r4 (F-15): tooltip on the service.bat menu button.
    public static string TipOpenServiceMenu => global::VPNRouter.Core.Localization.Strings.TipOpenServiceMenu;

    // ── Zapret sections (master-detail) ──
    public static string ZapretSecStatus => global::VPNRouter.Core.Localization.Strings.ZapretSecStatus;
    public static string ZapretSecStrategy => global::VPNRouter.Core.Localization.Strings.ZapretSecStrategy;
    public static string ZapretSecHosts => global::VPNRouter.Core.Localization.Strings.ZapretSecHosts;
    public static string ZapretSecFilters => global::VPNRouter.Core.Localization.Strings.ZapretSecFilters;
    public static string ZapretSecUpdates => global::VPNRouter.Core.Localization.Strings.ZapretSecUpdates;
    public static string ZapretSecDiagnostics => global::VPNRouter.Core.Localization.Strings.ZapretSecDiagnostics;
    public static string ZapretSecAdvanced => global::VPNRouter.Core.Localization.Strings.ZapretSecAdvanced;

    // v2.31.6-r7 — section descriptions for the Zapret master-detail.
    // Iter#3 audit (2026-05-04) flagged the page as «возможно слишком
    // сложная» — 5 unlabelled sections looked intimidating to first-run
    // users who only wanted to click Start DPI Bypass. Adding a 1-line
    // hint under each section header so first-time visitors understand
    // each section's purpose at a glance and can ignore power-user
    // sections without feeling they're missing something. Status keeps
    // its existing LblDpiDescription which already serves this role.
    public static string ZapretSecStrategyDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecStrategyDesc;
    public static string ZapretSecHostsDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecHostsDesc;
    public static string ZapretSecFiltersDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecFiltersDesc;
    public static string ZapretSecAdvancedDesc => global::VPNRouter.Core.Localization.Strings.ZapretSecAdvancedDesc;

    // Filters
    public static string GameFilter => global::VPNRouter.Core.Localization.Strings.GameFilter;
    public static string GameFilterOff => global::VPNRouter.Core.Localization.Strings.GameFilterOff;
    public static string GameFilterAll => global::VPNRouter.Core.Localization.Strings.GameFilterAll;
    public static string GameFilterTcp => global::VPNRouter.Core.Localization.Strings.GameFilterTcp;
    public static string GameFilterUdp => global::VPNRouter.Core.Localization.Strings.GameFilterUdp;

    public static string IpSetFilter => global::VPNRouter.Core.Localization.Strings.IpSetFilter;
    // v2.30.7-r4 — F-13 fix: "Any" / "Loaded" were left as English in the
    // RU dropdown, mixing inside an otherwise-Russian sub-section
    // (D1 violation). Localized while keeping the parenthetical
    // explainers intact.
    public static string IpSetAny => global::VPNRouter.Core.Localization.Strings.IpSetAny;
    public static string IpSetLoaded => global::VPNRouter.Core.Localization.Strings.IpSetLoaded;
    // v2.30.4-r1 (UX-51 fix): align off-state copy with GameFilterOff
    // ("Выкл" / "Off"). Pre-r1 had "None (отключено)" inconsistent with
    // the dropdown sibling.
    public static string IpSetNone => global::VPNRouter.Core.Localization.Strings.IpSetNone;

    // Updates
    public static string UpdateIpSet => global::VPNRouter.Core.Localization.Strings.UpdateIpSet;
    // v2.30.4-r1 (UX-52 fix): align case with the sub-tab name "Zapret"
    // (capitalized). Pre-r1 had "zapret" lowercase here while everywhere
    // else it's "Zapret" — inconsistent.
    public static string AutoUpdateCheckLabel => global::VPNRouter.Core.Localization.Strings.AutoUpdateCheckLabel;

    // Advanced
    public static string RunTestsLabel => global::VPNRouter.Core.Localization.Strings.RunTestsLabel;
    public static string RemoveServiceLabel => global::VPNRouter.Core.Localization.Strings.RemoveServiceLabel;

    public static string ApplyChanges => global::VPNRouter.Core.Localization.Strings.ApplyChanges;
    public static string ChangesApplied => global::VPNRouter.Core.Localization.Strings.ChangesApplied;
    public static string ApplyFailed => global::VPNRouter.Core.Localization.Strings.ApplyFailed;

    public static string AddCategory => global::VPNRouter.Core.Localization.Strings.AddCategory;
    public static string EnableWholeGroup => global::VPNRouter.Core.Localization.Strings.EnableWholeGroup;
    public static string CategoryNamePrompt => global::VPNRouter.Core.Localization.Strings.CategoryNamePrompt;
    public static string AddAppHint => global::VPNRouter.Core.Localization.Strings.AddAppHint;

    // ── App group display names ──
    // v2.30.4-r1 (UX-37/38 fix): all profile keys now have user-facing
    // display names. Pre-r1 only 5 of 9 categories were translated;
    // others leaked snake_case JSON keys ("AI_Tools", "Privacy_Shell",
    // "Messengers") into the UI.
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

    // ── Free Configs ──
    // v2.30.7-r2 — "Свободные" / "Free" was deemed unclear (user
    // feedback). Renamed to "Публичные" / "Public" — describes the
    // source (public free pools from 14 sources, server-side
    // pre-aggregated via GH Actions) without sounding like
    // "free trial" or "no-cost product". Fits narrow tab strip.
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

    // v2.28.5-r2/r4: batched fetch+test+verify status messages
    public static string FcStatusBatchedSearchStart(int target, int poolSize) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedSearchStart(target, poolSize);
    public static string FcStatusBatchedTcpTls(int found, int target, int batchNum, int totalBatches) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedTcpTls(found, target, batchNum, totalBatches);
    public static string FcStatusBatchedTcpTlsProgress(int found, int target, int batchNum, int totalBatches, int done, int total) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedTcpTlsProgress(found, target, batchNum, totalBatches, done, total);
    public static string FcStatusBatchedDeepVerify(int found, int target, int batchNum, int totalBatches, int candidates) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedDeepVerify(found, target, batchNum, totalBatches, candidates);
    public static string FcStatusBatchedFound(int found, int target) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedFound(found, target);
    /// <summary>v2.28.5-r6: per-probe status update so the UI doesn't appear
    /// frozen during the deep-verify phase. Each probe takes 3-5s, 5 in
    /// parallel → status flips every ~600 ms.</summary>
    public static string FcStatusBatchedProbing(int found, int target, string host, int port, string cc) => global::VPNRouter.Core.Localization.Strings.FcStatusBatchedProbing(found, target, host, port, cc);

    public static string FcDeepTargetLabel => global::VPNRouter.Core.Localization.Strings.FcDeepTargetLabel;
    public static string FcDeepExcludeRu => global::VPNRouter.Core.Localization.Strings.FcDeepExcludeRu;
    // v2.30.4-r1 (UX-66 fix): replaced literal "N" placeholder with
    // copy that doesn't pretend to know an exact count. Pre-r1 said
    // "Найдёт N рабочих" leaking the parameter symbol into the UI.
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
    // v2.31.0-r4 (F-24 / UX-63): tooltip explaining "—" rows.
    public static string FcSpeedColumnTooltip => global::VPNRouter.Core.Localization.Strings.FcSpeedColumnTooltip;
    // v2.31.0-r4 (F-26): inline confirmation toast after RunHealthCheck.
    public static string HealthCheckSavedToast => global::VPNRouter.Core.Localization.Strings.HealthCheckSavedToast;
    public static string FcColSni => global::VPNRouter.Core.Localization.Strings.FcColSni;
    public static string FcColTransport => global::VPNRouter.Core.Localization.Strings.FcColTransport;
    public static string FcEmptyHint => global::VPNRouter.Core.Localization.Strings.FcEmptyHint;
    public static string FcEmptyCtaTitle => global::VPNRouter.Core.Localization.Strings.FcEmptyCtaTitle;
    public static string FcEmptyCtaSubtitle => global::VPNRouter.Core.Localization.Strings.FcEmptyCtaSubtitle;
    public static string FcEmptyCtaButton => global::VPNRouter.Core.Localization.Strings.FcEmptyCtaButton;
    public static string FcFilteredEmpty => global::VPNRouter.Core.Localization.Strings.FcFilteredEmpty;
    public static string FcRefreshHint => global::VPNRouter.Core.Localization.Strings.FcRefreshHint;

    // v2.13.17 — Smart Refresh (latency goal)
    public static string FcSmartRefreshLabel => global::VPNRouter.Core.Localization.Strings.FcSmartRefreshLabel;
    public static string FcTargetNLabel => global::VPNRouter.Core.Localization.Strings.FcTargetNLabel;
    public static string FcConfigsWord => global::VPNRouter.Core.Localization.Strings.FcConfigsWord;
    public static string FcWithPingUnder => global::VPNRouter.Core.Localization.Strings.FcWithPingUnder;
    public static string FcMsUnit => global::VPNRouter.Core.Localization.Strings.FcMsUnit;
    public static string FcSmartRefreshHint => global::VPNRouter.Core.Localization.Strings.FcSmartRefreshHint;

    // v2.28.4-r2: Quickstart banner removed (single-button flow makes the 3-step lecture obsolete).

    // v2.14.7 — collapsible More Options
    public static string FcMoreOptions => global::VPNRouter.Core.Localization.Strings.FcMoreOptions;

    // v2.28.4-r1: 6-section nav removed (FreeConfigs is now single Simple page).
    public static string FcListHeader => global::VPNRouter.Core.Localization.Strings.FcListHeader;
    public static string FcListShown => global::VPNRouter.Core.Localization.Strings.FcListShown;

    // Stop button in the Free Configs search card
    public static string FcDeepStop => global::VPNRouter.Core.Localization.Strings.FcDeepStop;
    public static string FcDeepStopTooltip => global::VPNRouter.Core.Localization.Strings.FcDeepStopTooltip;

    // v2.28.4-r4 — Advanced settings expander label inside the green search card
    public static string FcAdvancedSettings => global::VPNRouter.Core.Localization.Strings.FcAdvancedSettings;

    // ── v2.28.6 — Free Configs tab strip (Search / Saved) + Saved-tab UI ──
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

    /// <summary>v2.28.6-r3: thin hint shown inside the empty search-tab
    /// list area. Replaces the v2.28.6-r1/r2 "no configs loaded" CTA card —
    /// the green search card right above the list IS the call-to-action,
    /// a second button below was redundant and broke the visual style of
    /// other pages (ServersPage / ToolsPage have no big empty-state CTA).</summary>
    public static string FcSearchListEmptyHint => global::VPNRouter.Core.Localization.Strings.FcSearchListEmptyHint;

    // v2.13.18 — Fast scan toggle
    public static string FcFastScanLabel => global::VPNRouter.Core.Localization.Strings.FcFastScanLabel;
    public static string FcFastScanHint => global::VPNRouter.Core.Localization.Strings.FcFastScanHint;

    // v2.14.3 — Deep Verify presets
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

    // v2.14.4 — User sources
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

    // v2.14.5 — Tooltips
    public static string FcRefreshTooltip => global::VPNRouter.Core.Localization.Strings.FcRefreshTooltip;
    public static string FcRetestTooltip => global::VPNRouter.Core.Localization.Strings.FcRetestTooltip;
    public static string FcDeepVerifyTooltip => global::VPNRouter.Core.Localization.Strings.FcDeepVerifyTooltip;

    // v2.13.19 — Privacy warning on first Connect from Free Configs
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

    // ── Service (Windows-only) ──
    public static string AutostartWithWindows => global::VPNRouter.Core.Localization.Strings.AutostartWithWindows;
    public static string RestartService => global::VPNRouter.Core.Localization.Strings.RestartService;
    public static string ReinstallService => global::VPNRouter.Core.Localization.Strings.ReinstallService;
    public static string InstallingService => global::VPNRouter.Core.Localization.Strings.InstallingService;
    public static string RemovingService => global::VPNRouter.Core.Localization.Strings.RemovingService;

    // ── v2.15.4 UI polish: hint texts + tooltips ──
    public static string ServerListHint => global::VPNRouter.Core.Localization.Strings.ServerListHint;
    public static string ZapretHostsHint => global::VPNRouter.Core.Localization.Strings.ZapretHostsHint;
    public static string AppsGroupEmpty => global::VPNRouter.Core.Localization.Strings.AppsGroupEmpty;

    // v2.29.0 — full-tunnel mode banner on the Apps page. Mac feedback
    // 2026-04-29: при RoutingMode=full весь content disabled без объяс-
    // нения; юзер думал что приложение сломано. Заменяем silent disable
    // на banner с объяснением + кнопка "Switch to split tunnel".
    // v2.30.3-r1: tunnel name localized to match SplitTunnelTitle/
    // FullTunnelTitle (Раздельный/Полный туннель).
    public static string AppsFullTunnelBanner => global::VPNRouter.Core.Localization.Strings.AppsFullTunnelBanner;
    public static string AppsFullTunnelBannerAction => global::VPNRouter.Core.Localization.Strings.AppsFullTunnelBannerAction;

    // ── app-only: not in Core/Localization/Strings.cs ──
    // v2.32 — Apps Include/Exclude 2-mode segmented toggle.
    // User feedback: "сделам 2 модм exclude и include". Default mode
    // (Include) = behaviour unchanged (selected apps → VPN, rest direct).
    // Exclude = inverse: selected apps → direct, rest → VPN.
    // Follow-up: lift to Core once Android exposes the same toggle.
    public static string AppsModeSectionTitle => Ru
        ? "Как применять списки"
        : "How lists are applied";
    public static string AppsModeInclude => Ru
        ? "Активен список «Через VPN»"
        : "Use the Through VPN list";
    public static string AppsModeExclude => Ru
        ? "Активен список «Мимо VPN»"
        : "Use the Bypass VPN list";
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

    // ── app-only: not in Core/Localization/Strings.cs ──
    // v2.32 — ServersPage marker for orphan vless.servers entries that
    // aren't in any active subscription. After F-A/B fixes the migrator
    // strips these on load, but for diagnostic clarity we still flag
    // any survivors in the UI.
    // Follow-up: lift to Core (Android may want the same diagnostic).
    public static string ServersOrphanBadge => Ru ? "Не из подписки" : "Not in subscription";
    public static string ServersOrphanTooltip => Ru
        ? "Этот сервер не входит в активные подписки — старая ручная запись. Если он вам не нужен, удалите его."
        : "This server isn't part of any active subscription — it's a legacy manual entry. If you don't need it, remove it.";

    // ── app-only: not in Core/Localization/Strings.cs ──
    // v2.32 — F-E auto-failover surfacing.
    // Follow-up: lift to Core (Android failover engine reuses the same flow).
    public static string AutoFailoverProbing => Ru
        ? "Проверяем подключение..."
        : "Probing connection...";
    public static string AutoFailoverSwitching(int n, int total, string serverName) => Ru
        ? $"Сервер недоступен. Переключаемся ({n}/{total}) → {serverName}"
        : $"Server unreachable. Switching ({n}/{total}) → {serverName}";
    public static string AutoFailoverExhausted => Ru
        ? "Все серверы подписки недоступны. Проверьте сеть или подписку."
        : "All subscription servers are unreachable. Check network or subscription.";
    public static string AutoFailoverCustomMode => Ru
        ? "Кастомный конфиг не отвечает. Проверьте JSON-конфигурацию."
        : "Custom config isn't responding. Check JSON configuration.";

    // v2.29.0 — Custom direct rules (Network → Routing → expander).
    // Mac tester request 2026-04-29: «хотелось бы расширенную настройку
    // конфига, у меня есть кейсы с wireguard где мне хотелось бы самому
    // прописывать direct правила».
    // v2.30.0 — full custom rules engine (direct/proxy/block actions).
    // Replaces v2.29.0-r4 CustomDirectRules* strings.
    public static string CustomRulesTitle => global::VPNRouter.Core.Localization.Strings.CustomRulesTitle;
    public static string CustomRulesDescription => global::VPNRouter.Core.Localization.Strings.CustomRulesDescription;
    // v2.30.3-r1 (BUG-15 fix): broke long lines so the example template
    // is readable at default ~510 px window width without horizontal
    // scrolling. The pre-r1 placeholder had a 132-char Types comment
    // line that was always cut off — users couldn't see the type list.
    // Now wrapped across 3 short lines.
    public static string CustomRulesPlaceholder => global::VPNRouter.Core.Localization.Strings.CustomRulesPlaceholder;
    public static string CustomRulesErrorHeader => global::VPNRouter.Core.Localization.Strings.CustomRulesErrorHeader;
    public static string CustomRulesConflictHeader => global::VPNRouter.Core.Localization.Strings.CustomRulesConflictHeader;

    // v2.30.0-r2: structured row-table editor strings (Network → Rules section).
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

    // v2.30.0-r3 — Import/Export 3 formats.
    public static string CustomRulesImport => global::VPNRouter.Core.Localization.Strings.CustomRulesImport;
    public static string CustomRulesExport => global::VPNRouter.Core.Localization.Strings.CustomRulesExport;
    public static string CustomRulesImportTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesImportTooltip;
    public static string CustomRulesExportTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesExportTooltip;

    // v2.30.0-r4 — search filter + bulk actions for large rule lists.
    public static string CustomRulesSearchPlaceholder => global::VPNRouter.Core.Localization.Strings.CustomRulesSearchPlaceholder;
    public static string CustomRulesClearAll => global::VPNRouter.Core.Localization.Strings.CustomRulesClearAll;
    public static string CustomRulesEnableAll => global::VPNRouter.Core.Localization.Strings.CustomRulesEnableAll;
    public static string CustomRulesDisableAll => global::VPNRouter.Core.Localization.Strings.CustomRulesDisableAll;
    public static string CustomRulesClearAllTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesClearAllTooltip;
    public static string CustomRulesEnableAllTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesEnableAllTooltip;
    public static string CustomRulesDisableAllTooltip => global::VPNRouter.Core.Localization.Strings.CustomRulesDisableAllTooltip;
    public static string CustomRulesNoMatchHint => global::VPNRouter.Core.Localization.Strings.CustomRulesNoMatchHint;

    public static string CustomRulesExistingHeader => global::VPNRouter.Core.Localization.Strings.CustomRulesExistingHeader;

    // ── v2.30.0-r7 — Cards / Edit view-mode toggle (RulesExplorations.html) ──
    // Power-user editable text mode replaces the old "Advanced" expander.
    // Cards view is the structured row-table editor (default, friendly).
    // Edit view is a full textarea with line-numbered gutter, per-line
    // errors, and explicit Apply / Revert (no auto-save while typing).
    public static string RulesViewCards => global::VPNRouter.Core.Localization.Strings.RulesViewCards;
    public static string RulesViewRead => global::VPNRouter.Core.Localization.Strings.RulesViewRead;
    public static string RulesViewEdit => global::VPNRouter.Core.Localization.Strings.RulesViewEdit;
    public static string RulesViewCardsTooltip => global::VPNRouter.Core.Localization.Strings.RulesViewCardsTooltip;
    public static string RulesViewReadTooltip => global::VPNRouter.Core.Localization.Strings.RulesViewReadTooltip;
    public static string RulesViewEditTooltip => global::VPNRouter.Core.Localization.Strings.RulesViewEditTooltip;

    public static string RulesEditorApply => global::VPNRouter.Core.Localization.Strings.RulesEditorApply;
    public static string RulesEditorRevert => global::VPNRouter.Core.Localization.Strings.RulesEditorRevert;
    public static string RulesEditorDirty => global::VPNRouter.Core.Localization.Strings.RulesEditorDirty;
    // v2.30.3-r1 (UX-16 fix): the parser uses '!' as the disable
    // prefix (CustomRulesParser line 85: StartsWith("!")), not "# off"
    // which was a misleading documentation. Brought hint in line with
    // the actual parser + the example placeholder ('!block port 53').
    public static string RulesEditorFormatHint => global::VPNRouter.Core.Localization.Strings.RulesEditorFormatHint;

    // Help banner — replaces the dense single-paragraph description.
    // Bullet points highlight the toggle precedence + LAN auto-direct +
    // order-doesn't-matter facts. Dismissable via X button.
    // v2.30.0-r11 — Filter chips + bulk-actions menu.
    public static string RulesFilterAll => global::VPNRouter.Core.Localization.Strings.RulesFilterAll;
    public static string RulesBulkActions => global::VPNRouter.Core.Localization.Strings.RulesBulkActions;

    // v2.30.0-r14 — Sort-by-type bulk action (per design `.bulk-pop`).
    public static string RulesSortByType => global::VPNRouter.Core.Localization.Strings.RulesSortByType;

    // v2.30.0-r18 — Clear All inline confirm bar (replaces broken
    // two-click-in-popover pattern). Also adds a generic Cancel string.
    public static string RulesClearAllHint => global::VPNRouter.Core.Localization.Strings.RulesClearAllHint;
    public static string RulesClearAllConfirm => global::VPNRouter.Core.Localization.Strings.RulesClearAllConfirm;
    public static string CommonCancel => global::VPNRouter.Core.Localization.Strings.CommonCancel;

    // v2.30.0-r17 — Custom-rules-priority CheckBox label + tooltip.
    public static string RulesCustomAboveToggles => global::VPNRouter.Core.Localization.Strings.RulesCustomAboveToggles;
    public static string RulesCustomAboveTogglesHint => global::VPNRouter.Core.Localization.Strings.RulesCustomAboveTogglesHint;

    // v2.30.0-r14 — Add-form mini-labels (uppercase, per design `.field .ftitle`).
    // Localized so the UI is single-language end-to-end (matches user's
    // "не использовать микс" rule).
    public static string RulesAddLabelAction => global::VPNRouter.Core.Localization.Strings.RulesAddLabelAction;
    public static string RulesAddLabelType => global::VPNRouter.Core.Localization.Strings.RulesAddLabelType;
    public static string RulesAddLabelValue => global::VPNRouter.Core.Localization.Strings.RulesAddLabelValue;
    public static string RulesAddLabelComment => global::VPNRouter.Core.Localization.Strings.RulesAddLabelComment;
    public static string RulesAddLabelOpt => global::VPNRouter.Core.Localization.Strings.RulesAddLabelOpt;

    // v2.30.0-r12 — Help banner restructured per design RulesPage.html
    // `.help` block: bold heading + 3 bullets with <code>-styled values
    // for technical terms (CIDR ranges, "direct" action). Each bullet is
    // split into prefix / emphasized-name / mid / emphasized-name / suffix
    // pieces so the XAML can apply per-Run styling (FontWeight=SemiBold for
    // names, FontFamily=mono for code values) without a markup parser.
    public static string RulesHelpHeader => global::VPNRouter.Core.Localization.Strings.RulesHelpHeader;

    // Bullet 1: «toggle1» and «toggle2» fire BEFORE your rules.
    public static string RulesHelpB1Pre => global::VPNRouter.Core.Localization.Strings.RulesHelpB1Pre;
    public static string RulesHelpB1T1 => global::VPNRouter.Core.Localization.Strings.RulesHelpB1T1;
    public static string RulesHelpB1Mid => global::VPNRouter.Core.Localization.Strings.RulesHelpB1Mid;
    public static string RulesHelpB1T2 => global::VPNRouter.Core.Localization.Strings.RulesHelpB1T2;
    public static string RulesHelpB1Suf => global::VPNRouter.Core.Localization.Strings.RulesHelpB1Suf;

    // Bullet 2: Private nets (10.0.0.0/8, ...) already go direct automatically.
    public static string RulesHelpB2Pre => global::VPNRouter.Core.Localization.Strings.RulesHelpB2Pre;
    public static string RulesHelpB2Mid => global::VPNRouter.Core.Localization.Strings.RulesHelpB2Mid;
    public static string RulesHelpB2Suf => global::VPNRouter.Core.Localization.Strings.RulesHelpB2Suf;

    // Bullet 3: Rule order DOES NOT matter — first match wins per address.
    public static string RulesHelpB3Pre => global::VPNRouter.Core.Localization.Strings.RulesHelpB3Pre;
    public static string RulesHelpB3Bold => global::VPNRouter.Core.Localization.Strings.RulesHelpB3Bold;
    public static string RulesHelpB3Suf => global::VPNRouter.Core.Localization.Strings.RulesHelpB3Suf;

    // Legacy single-string accessor (kept for any cached XAML still binding
    // to the pre-r12 RulesHelpBanner). New XAML uses the structured
    // RulesHelpHeader + RulesHelpB1..B3* set instead.
    public static string RulesHelpBanner => global::VPNRouter.Core.Localization.Strings.RulesHelpBanner;

    // ── Legacy v2.29.0-r4 names (kept for back-compat with cached XAML) ──
    public static string CustomDirectRulesTitle => global::VPNRouter.Core.Localization.Strings.CustomDirectRulesTitle;
    public static string CustomDirectRulesDescription => global::VPNRouter.Core.Localization.Strings.CustomDirectRulesDescription;
    public static string CustomDirectRulesPlaceholder => global::VPNRouter.Core.Localization.Strings.CustomDirectRulesPlaceholder;
    public static string CustomDirectRulesErrorHeader => global::VPNRouter.Core.Localization.Strings.CustomDirectRulesErrorHeader;
    public static string SelectCategoryHint => global::VPNRouter.Core.Localization.Strings.SelectCategoryHint;

    // Tooltips — Network tab
    public static string TipBypassRu => global::VPNRouter.Core.Localization.Strings.TipBypassRu;
    public static string TipLeakBlockOnFail => global::VPNRouter.Core.Localization.Strings.TipLeakBlockOnFail;
    public static string TipLeakStrictMode => global::VPNRouter.Core.Localization.Strings.TipLeakStrictMode;
    public static string TipLeakForceIpv4 => global::VPNRouter.Core.Localization.Strings.TipLeakForceIpv4;
    public static string TipLeakStrictDns => global::VPNRouter.Core.Localization.Strings.TipLeakStrictDns;
    public static string TipLeakFlushDns => global::VPNRouter.Core.Localization.Strings.TipLeakFlushDns;
    public static string TipBlockAds => global::VPNRouter.Core.Localization.Strings.TipBlockAds;

    // Tooltips — Zapret / DPI
    public static string TipZapretAutoUpdate => global::VPNRouter.Core.Localization.Strings.TipZapretAutoUpdate;

    // Tooltips — Free Configs controls
    public static string TipFcFastScan => global::VPNRouter.Core.Localization.Strings.TipFcFastScan;
    public static string TipFcSmartRefresh => global::VPNRouter.Core.Localization.Strings.TipFcSmartRefresh;
    public static string TipFcSkipRu => global::VPNRouter.Core.Localization.Strings.TipFcSkipRu;
    // ── Simple mode (v2.17+) ──

    // v2.30.7 — both toggles were hardcoded English in both languages.
    // RU users see "Advanced ▸" / "◂ Simple" inside an otherwise-Russian
    // UI. Now: localized with the full word ("Расширенный/Простой"
    // matches the UI mode names everywhere else).
    /// <summary>Header toggle button: Simple → Advanced.</summary>
    public static string SmpToggleToAdvanced => global::VPNRouter.Core.Localization.Strings.SmpToggleToAdvanced;
    /// <summary>Header toggle button: Advanced → Simple.</summary>
    public static string SmpToggleToSimple => global::VPNRouter.Core.Localization.Strings.SmpToggleToSimple;
    /// <summary>Tooltip for the header toggle button.</summary>
    public static string SmpToggleTooltip => global::VPNRouter.Core.Localization.Strings.SmpToggleTooltip;

    // v2.17.0 placeholder copy — replaced by the real skeleton in v2.17.1.
    public static string SmpPlaceholderTitle => global::VPNRouter.Core.Localization.Strings.SmpPlaceholderTitle;
    public static string SmpPlaceholderBody => global::VPNRouter.Core.Localization.Strings.SmpPlaceholderBody;
    public static string SmpPlaceholderSwitchToAdvanced => global::VPNRouter.Core.Localization.Strings.SmpPlaceholderSwitchToAdvanced;

    // v2.17.1 skeleton — section labels + control captions
    public static string SmpInputLabel => global::VPNRouter.Core.Localization.Strings.SmpInputLabel;
    public static string SmpInputWatermark => global::VPNRouter.Core.Localization.Strings.SmpInputWatermark;
    public static string SmpInputHint => global::VPNRouter.Core.Localization.Strings.SmpInputHint;
    public static string SmpTunnelModeLabel => global::VPNRouter.Core.Localization.Strings.SmpTunnelModeLabel;
    public static string SmpSplitOption => global::VPNRouter.Core.Localization.Strings.SmpSplitOption;
    // v2.30.6-r1 (UX-3 fix): old subtitle hardcoded specific apps ("Discord,
    // браузеры, мессенджеры, рабочие") which doesn't always match actual
    // selected profiles. Generic descriptor avoids the mismatch and lets
    // the Apps tab list be the source of truth.
    public static string SmpSplitHint => global::VPNRouter.Core.Localization.Strings.SmpSplitHint;
    public static string SmpFullOption => global::VPNRouter.Core.Localization.Strings.SmpFullOption;
    public static string SmpFullHint => global::VPNRouter.Core.Localization.Strings.SmpFullHint;
    public static string SmpAdvancedLink => global::VPNRouter.Core.Localization.Strings.SmpAdvancedLink;
    // v2.30.7-r4 — F-1 fix: was "Free Configs" in BOTH languages
    // (D1 violation in RU + inconsistent with the new "Публичные"
    // tab name shipped in r2). Aligned with the renamed tab.
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

    // ── v2.18.0 compact Simple-mode redesign (Variant A · Calm) ──
    // Status card titles (one word when possible).
    // v2.18.3: "Protected" → "Connected" — RU audience uses VPN for access
    // (bypassing blocks), not for security posture, so "Защищено" implied
    // the wrong mental model.
    public static string SmpStatusProtected => global::VPNRouter.Core.Localization.Strings.SmpStatusProtected;
    public static string SmpStatusConnecting => global::VPNRouter.Core.Localization.Strings.SmpStatusConnecting;
    public static string SmpStatusNotConnected => global::VPNRouter.Core.Localization.Strings.SmpStatusNotConnected;

    // Status card descriptions. v2.18.3: shortened the "via" prefix so the
    // full line reads "Connected" (title) + "via de-01 · 104.194.156.93"
    // (desc) instead of repeating "Connected" twice.
    public static string SmpStatusConnectedVia => global::VPNRouter.Core.Localization.Strings.SmpStatusConnectedVia;
    public static string SmpStatusConnectedNoDetails => global::VPNRouter.Core.Localization.Strings.SmpStatusConnectedNoDetails;
    public static string SmpStatusConnectingHint => global::VPNRouter.Core.Localization.Strings.SmpStatusConnectingHint;
    public static string SmpStatusDisconnectedHint => global::VPNRouter.Core.Localization.Strings.SmpStatusDisconnectedHint;

    // Config row — "Config · Mode" label + value parts ("subscribe · split")
    public static string SmpConfigRowLabel => global::VPNRouter.Core.Localization.Strings.SmpConfigRowLabel;
    public static string SmpCfgSubscribe => global::VPNRouter.Core.Localization.Strings.SmpCfgSubscribe;
    public static string SmpCfgManual => global::VPNRouter.Core.Localization.Strings.SmpCfgManual;
    public static string SmpCfgCustom => global::VPNRouter.Core.Localization.Strings.SmpCfgCustom;
    public static string SmpCfgSplit => global::VPNRouter.Core.Localization.Strings.SmpCfgSplit;
    public static string SmpCfgFull => global::VPNRouter.Core.Localization.Strings.SmpCfgFull;

    // CTA captions — Connect / Disconnect / Cancel (not destructive; accent-solid, not red)
    public static string SmpCtaConnect => global::VPNRouter.Core.Localization.Strings.SmpCtaConnect;
    public static string SmpCtaDisconnect => global::VPNRouter.Core.Localization.Strings.SmpCtaDisconnect;
    public static string SmpCtaCancel => global::VPNRouter.Core.Localization.Strings.SmpCtaCancel;

    // Advanced card — new wording listing the feature surface
    public static string SmpAdvCardTitle => global::VPNRouter.Core.Localization.Strings.SmpAdvCardTitle;
    // app-only: Core's SmpAdvCardSubtitle branches on OperatingSystem.IsAndroid()
    // (mobile lists Settings·Applications, desktop lists Zapret·Telegram proxy).
    // On Windows the two paths produce byte-identical output, but classified
    // as TEXT-DRIFT here (different IL) — preserve until we verify the
    // OS-branch helper can move to Core without breaking the desktop string.
    // v2.30.7-r4 — F-1 fix: align Simple-card subtitle with the new
    // "Публичные" tab name (was "Free Configs" hardcoded EN in both
    // languages, D1 + inconsistency).
    public static string SmpAdvCardSubtitle => Ru
        ? "Серверы · Подписки · Zapret · Telegram-прокси · Публичные"
        : "Servers · Subscriptions · Zapret · Telegram proxy · Public";

    // Mini-header menu items (⋯ flyout)
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
    // v2.24.4 troubleshooting items (Level 2/3 self-healing)
    public static string SmpMenuHealthCheck => global::VPNRouter.Core.Localization.Strings.SmpMenuHealthCheck;
    // v2.30.5-r1 (UX-68 fix): localize "Safe Mode" in Russian.
    public static string SmpMenuSafeMode => global::VPNRouter.Core.Localization.Strings.SmpMenuSafeMode;
    public static string SmpMenuResetConfig => global::VPNRouter.Core.Localization.Strings.SmpMenuResetConfig;
    public static string SmpMenuResetConfirm => global::VPNRouter.Core.Localization.Strings.SmpMenuResetConfirm;
    public static string TipSmpMenuHealthCheck => global::VPNRouter.Core.Localization.Strings.TipSmpMenuHealthCheck;
    public static string TipSmpMenuSafeMode => global::VPNRouter.Core.Localization.Strings.TipSmpMenuSafeMode;
    public static string TipSmpMenuResetConfig => global::VPNRouter.Core.Localization.Strings.TipSmpMenuResetConfig;

    // v2.25.0-r2 — Autostart is Windows-only (service + registry Run key).
    // On Linux/macOS this whole section is non-functional; replace the four
    // checkboxes with a notice so users don't flip disabled toggles.
    public static string AutostartPlatformNotice => global::VPNRouter.Core.Localization.Strings.AutostartPlatformNotice;

    // v2.25.2 — section labels inside the redesigned ⋯ popover menu.
    // Matches the Claude-Design handoff AdvancedMode.html section 1 layout.
    public static string SmpMenuViewSection => global::VPNRouter.Core.Localization.Strings.SmpMenuViewSection;
    public static string SmpMenuDiagnosticsSection => global::VPNRouter.Core.Localization.Strings.SmpMenuDiagnosticsSection;
    public static string SmpMenuTroubleshootingSection => global::VPNRouter.Core.Localization.Strings.SmpMenuTroubleshootingSection;
    public static string SmpSegLight => global::VPNRouter.Core.Localization.Strings.SmpSegLight;
    public static string SmpSegDark => global::VPNRouter.Core.Localization.Strings.SmpSegDark;
    public static string SmpSegSystem => global::VPNRouter.Core.Localization.Strings.SmpSegSystem;
    public static string SmpSegRu => global::VPNRouter.Core.Localization.Strings.SmpSegRu;
    public static string SmpSegEn => global::VPNRouter.Core.Localization.Strings.SmpSegEn;

    // v2.25.11 — shown briefly in the footer while the window rebuild
    // triggered by a language toggle is in flight, so the user can see
    // that their click was received (without this the flyout closes and
    // then the UI freezes for ~200-500 ms with no visible acknowledgement).
    public static string LanguageSwitching => global::VPNRouter.Core.Localization.Strings.LanguageSwitching;

    // v2.25.0 — "About" dialog (version / build info moved out of header).
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

    // ── v2.15.5 Localization pass: remaining hardcoded strings ──

    // Tooltips — MainWindow header buttons
    public static string TipOpenLogs => global::VPNRouter.Core.Localization.Strings.TipOpenLogs;
    public static string TipIpLeak => global::VPNRouter.Core.Localization.Strings.TipIpLeak;

    // Tooltips — Applications page
    public static string TipRemoveCategory => global::VPNRouter.Core.Localization.Strings.TipRemoveCategory;
    public static string TipRemoveApp => global::VPNRouter.Core.Localization.Strings.TipRemoveApp;

    // Tooltips — Free Configs cleanup
    public static string TipOpenFreeConfigLogs => global::VPNRouter.Core.Localization.Strings.TipOpenFreeConfigLogs;
    public static string TipClearFailed => global::VPNRouter.Core.Localization.Strings.TipClearFailed;
    public static string TipKeepVerifiedOnly => global::VPNRouter.Core.Localization.Strings.TipKeepVerifiedOnly;
    public static string TipClearAllCache => global::VPNRouter.Core.Localization.Strings.TipClearAllCache;

    // Tooltips — Servers / Subscriptions testing
    public static string TipTcpTlsPing => global::VPNRouter.Core.Localization.Strings.TipTcpTlsPing;
    public static string TipTestTcpTls => global::VPNRouter.Core.Localization.Strings.TipTestTcpTls;
    public static string TipCloseServerDetail => global::VPNRouter.Core.Localization.Strings.TipCloseServerDetail;
    public static string TipDeleteServer => global::VPNRouter.Core.Localization.Strings.TipDeleteServer;
    public static string TipTestAllServers => global::VPNRouter.Core.Localization.Strings.TipTestAllServers;
    public static string TipDeepVerifyServers => global::VPNRouter.Core.Localization.Strings.TipDeepVerifyServers;
    public static string TipRefreshSubscription => global::VPNRouter.Core.Localization.Strings.TipRefreshSubscription;
    public static string TipRemoveSubscription => global::VPNRouter.Core.Localization.Strings.TipRemoveSubscription;

    // Form field labels (Server detail editor)
    public static string LblName => global::VPNRouter.Core.Localization.Strings.LblName;
    public static string LblServer => global::VPNRouter.Core.Localization.Strings.LblServer;
    public static string LblPort => global::VPNRouter.Core.Localization.Strings.LblPort;
    public static string LblUuid => global::VPNRouter.Core.Localization.Strings.LblUuid;
    public static string LblPublicKey => global::VPNRouter.Core.Localization.Strings.LblPublicKey;
    public static string LblShortId => global::VPNRouter.Core.Localization.Strings.LblShortId;

    // Descriptive labels
    public static string LblRoutingMode => global::VPNRouter.Core.Localization.Strings.LblRoutingMode;
    public static string LblNoServers => global::VPNRouter.Core.Localization.Strings.LblNoServers;
    public static string LblAddSubscriptionHint => global::VPNRouter.Core.Localization.Strings.LblAddSubscriptionHint;

    // Badge
    public static string LblCustomBadge => global::VPNRouter.Core.Localization.Strings.LblCustomBadge;

    // Watermarks
    public static string WmZapretCustomArgs => global::VPNRouter.Core.Localization.Strings.WmZapretCustomArgs;
    // v2.30.4-r1 (UX-26 fix): expand placeholder to advertise multi-protocol
    // support shipped in v2.30.1 (vless/hysteria2/tuic/shadowsocks). Pre-r1
    // users had no way to discover from the UI that hy2://, tuic:// or ss://
    // are accepted in the same input.
    public static string WmVlessUri => global::VPNRouter.Core.Localization.Strings.WmVlessUri;
    public static string WmTgProxyPort => global::VPNRouter.Core.Localization.Strings.WmTgProxyPort;
    public static string WmTgProxySecret => global::VPNRouter.Core.Localization.Strings.WmTgProxySecret;

    // Status init values
    public static string StatusStopped => global::VPNRouter.Core.Localization.Strings.StatusStopped;
    public static string StatusRunning => global::VPNRouter.Core.Localization.Strings.StatusRunning;

    // v2.30.4-r1 (SUGGEST-22 fix): manual update check inside Settings →
    // Обновления tab.
    public static string CurrentVersion => global::VPNRouter.Core.Localization.Strings.CurrentVersion;

    // v2.30.5-r1 (UX-29 fix): empty-state hero for the Custom Config
    // (JSON) sub-tab. Pre-r1 was blank + a "Нажмите на конфиг для
    // активации" hint with nothing to click; now explains the feature.
    public static string CustomConfigsEmptyTitle => global::VPNRouter.Core.Localization.Strings.CustomConfigsEmptyTitle;
    public static string CustomConfigsEmptyHint => global::VPNRouter.Core.Localization.Strings.CustomConfigsEmptyHint;

    // v2.32.0 — recovery banner shown after SettingsValidator rejected a
    // structurally-valid but semantically-broken config.yaml (typoed
    // config_mode, port out of range, malformed subscription URL, etc.)
    // and the loader rewrote defaults. The backup path comes from
    // SettingsLoader.LastRecoveryNotice and is appended verbatim by the
    // VM, so the localized string is the prefix only.
    public static string SettingsRecoveredFromBadConfig(string backupPath) => global::VPNRouter.Core.Localization.Strings.SettingsRecoveredFromBadConfig(backupPath);

    // v2.32.3 (2026-05-17, Z:\kanareik incident) — placeholder-prune banner.
    // Shown once when SettingsMigrator.PruneKnownPlaceholders strips placeholder
    // Reality credentials from a user's config. {0} is the count of removed
    // entries. The "AllGone" branch fires when nothing healthy is left and the
    // user must add a real server to continue.
    public static string PlaceholderPruneBanner => global::VPNRouter.Core.Localization.Strings.PlaceholderPruneBanner;

    public static string PlaceholderPruneBannerAllGone => global::VPNRouter.Core.Localization.Strings.PlaceholderPruneBannerAllGone;

    // ── Bug-r9-E (2026-05-11) — third-party VPN conflict banner ──
    // Shown in the MainWindow header banner when StartAsync throws
    // ConflictingVpnException. See Bug-r9-E section in
    // plans/vpnrouter-android-r9-user-bug-batch.md. Stas's repro
    // (xraycore.exe from v2RayTun) surfaced the need for an explicit
    // process-named hint instead of the cryptic "Cannot create a file
    // when that file already exists" wintun error.

    public static string ConflictOtherVpnDetectedTitle => global::VPNRouter.Core.Localization.Strings.ConflictOtherVpnDetectedTitle;

    public static string ConflictOtherVpnDetectedMessage(string processName, int pid) => global::VPNRouter.Core.Localization.Strings.ConflictOtherVpnDetectedMessage(processName, pid);

    public static string ConflictRefreshButton => global::VPNRouter.Core.Localization.Strings.ConflictRefreshButton;

    // ── app-only: Conflict Kill/Ignore strings live here (not in Core) ──
    // Desktop-specific UX — Android relies on system VpnService and doesn't
    // ship a Conflict banner. Follow-up: keep here unless Android grows the
    // same banner.
    // ── v2.32.1-r4 (Bug-r10-A) — Kill conflicting VPN button ──
    // User report 2026-05-11: на основной Win машине app требовал убить
    // AmneziaVPN, но кнопки kill не было — пришлось через Task Manager.
    // Кнопка появляется в conflict banner, force-kill'ит обнаруженные
    // процессы. Если kill fail (protected process / отказ доступа) —
    // показывает partial-failure сообщение с подсказкой запустить от
    // имени админа.
    public static string ConflictKillButton => Ru ? "Завершить" : "Kill";

    public static string ConflictKillTooltip => Ru
        ? "Принудительно завершить конфликтующий VPN-процесс."
        : "Force-terminate the conflicting VPN process.";

    public static string ConflictKillPartialFailure(int killed, int failed) => Ru
        ? $"Завершено: {killed}. Не удалось: {failed}. " +
          "Запустите VPNRouter от имени администратора или закройте процесс вручную через Диспетчер задач."
        : $"Killed: {killed}. Failed: {failed}. " +
          "Run VPNRouter as administrator, or close the process manually via Task Manager.";

    // ── v2.32.1-r5 (Bug-r10-B) — Ignore conflict button ──
    // Session-scoped opt-out: bypass ConflictingVpnDetector on next
    // Connect attempt only. Use case: AmneziaVPN.exe sitting idle in
    // tray (process running but не держит wintun → false positive),
    // multi-adapter setups. Recoverable: если юзер ошибся, sing-box
    // упадёт с оригинальной wintun ошибкой downstream.
    public static string ConflictIgnoreButton => Ru ? "Игнорировать" : "Ignore";

    public static string ConflictIgnoreTooltip => Ru
        ? "Игнорировать предупреждение и попытаться подключиться. " +
          "Полезно если другой VPN запущен, но не подключён."
        : "Ignore the warning and try to connect anyway. " +
          "Useful when the other VPN is running but not connected.";

    // ── Bug-r9-G (2026-05-11) — Zapret AV-block toast ──
    // ZapretManager fires ImmediateExitDetected when winws.exe exits
    // within < 2 s with non-zero code (almost always Windows Defender
    // or third-party AV termination). The VM subscribes and shows the
    // toast on the Tools / DPI Bypass page.

    public static string ZapretAvBlockToast => global::VPNRouter.Core.Localization.Strings.ZapretAvBlockToast;

    public static string ZapretAvBlockCopyPath => global::VPNRouter.Core.Localization.Strings.ZapretAvBlockCopyPath;

    // ── app-only: Emergency Channel (wgturn) — not in Core/Strings.cs ──
    // Desktop-only feature currently. Follow-up: lift the whole block
    // to Core when Android grows the same card (wgturn-cli is bundled
    // in installer Phase 1 — Phase 2 will add Android surface).
    // ── v2.32.2 (W-4) — Emergency Channel (wgturn) card on Tools tab ──
    // Backup VPN that runs over VK Calls TURN servers. Surfaces a third
    // card on the Tools tab alongside Zapret and Telegram Proxy. Has
    // three visual states: install (not-installed), idle (installed +
    // disconnected), connected. Strings cover all three plus the helper
    // dropdown / button / status labels.

    public static string EmergencyChannelCardTitle => Ru
        ? "Экстренный канал (wgturn)"
        : "Emergency Channel (wgturn)";

    public static string EmergencyChannelCardTitleWithVersion(string version, string variant) => Ru
        ? $"Экстренный канал (wgturn {version}, {variant})"
        : $"Emergency Channel (wgturn {version}, {variant})";

    public static string EmergencyChannelDescription => Ru
        ? "Резервный VPN через VK Calls TURN. Используется когда основной канал заблокирован."
        : "Backup VPN via VK Calls TURN. Used when the primary channel is blocked.";

    public static string EmergencyChannelInstall => Ru ? "Установить (~10 MB)" : "Install (~10 MB)";

    public static string EmergencyChannelInstallEmbedded => Ru
        ? "Загрузить полную версию (~120 MB)"
        : "Download full version (~120 MB)";

    public static string EmergencyChannelConfigsLabel => Ru ? "Конфигурация:" : "Configuration:";

    public static string EmergencyChannelAddConfig => Ru ? "Добавить конфигурацию" : "Add configuration";

    // r10 r9+ (Bug-r10-I): inputs for the inline add-config form.
    public static string EmergencyChannelAddConfigNameWatermark => Ru
        ? "Имя (необязательно)"
        : "Name (optional)";
    public static string EmergencyChannelAddConfigUrlWatermark => Ru
        ? "wgturn://..."
        : "wgturn://...";
    public static string EmergencyChannelAddConfigBtn => Ru ? "Добавить" : "Add";

    public static string EmergencyChannelVkLinkLabel => Ru ? "VK-ссылка:" : "VK link:";

    public static string EmergencyChannelVkLinkHint => Ru
        ? "Получите из VK Calls → Поделиться ссылкой"
        : "Get from VK Calls → Share link";

    public static string EmergencyChannelConnect => Ru ? "Подключить" : "Connect";

    public static string EmergencyChannelDisconnect => Ru ? "Отключить" : "Disconnect";

    public static string EmergencyChannelRemove => Ru ? "Удалить" : "Remove";

    public static string EmergencyChannelUpdate => Ru ? "Обновить" : "Update";

    public static string EmergencyChannelOpenLog => Ru ? "Открыть лог" : "Open log";

    public static string EmergencyChannelDetails => Ru ? "Подробнее" : "Details";

    public static string EmergencyChannelStatusNotInstalled => Ru ? "Не установлен" : "Not installed";

    public static string EmergencyChannelStatusDisconnected => Ru ? "Отключён" : "Disconnected";

    public static string EmergencyChannelStatusConnecting => Ru ? "Подключение..." : "Connecting...";

    public static string EmergencyChannelStatusConnectedTo(string name) => Ru
        ? $"Подключено к {name}"
        : $"Connected to {name}";

    public static string EmergencyChannelStatusFailed(string reason) => Ru
        ? $"Сбой: {reason}"
        : $"Failed: {reason}";

    public static string EmergencyChannelPidLine(int pid) => Ru
        ? $"PID: {pid}"
        : $"PID: {pid}";

    public static string EmergencyChannelStatusLabel => Ru ? "Статус:" : "Status:";

    public static string EmergencyChannelVkLinkWatermark => Ru
        ? "Вставьте сюда ссылку из VK Calls..."
        : "Paste link from VK Calls...";
}
