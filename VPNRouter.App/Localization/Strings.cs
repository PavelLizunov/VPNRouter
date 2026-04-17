namespace VPNRouter.App.Localization;

/// <summary>
/// Bilingual string provider (EN/RU). Port of VPNRouter.GUI.Localization.Strings for Avalonia.
/// </summary>
public static class Strings
{
    public static string Lang { get; set; } = "en";
    private static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    // ── Tabs ──
    public static string TabServers => Ru ? "Серверы" : "Servers";
    public static string TabApps => Ru ? "Приложения" : "Applications";
    public static string TabNetwork => Ru ? "Сеть" : "Network";
    public static string TabSettings => Ru ? "Настройки" : "Settings";
    public static string TabZapret => "Zapret";
    public static string TabTgWsProxy => "TgProxy";

    // ── Config mode ──
    public static string VlessServers => Ru ? "VLESS Серверы" : "VLESS Servers";
    public static string CustomConfigJson => Ru ? "Свой конфиг (JSON)" : "Custom Config (JSON)";
    public static string ModeManual => Ru ? "Ручной" : "Manual";
    public static string ModeSubscribe => Ru ? "Подписка" : "Subscribe";
    public static string ModeCustomConfig => Ru ? "Свой конфиг" : "Custom Config";
    public static string SubscribeMode => Ru ? "Подписка" : "Subscribe";
    public static string SubscriptionUrlHint => Ru ? "URL подписки (subscription link)" : "Subscription URL";
    public static string SyncButton => Ru ? "Обновить" : "Sync";
    public static string Syncing => Ru ? "Синхронизация..." : "Syncing...";
    public static string SyncComplete(int count) => Ru ? $"Получено {count} серверов" : $"Fetched {count} servers";
    public static string SyncFailed(string err) => Ru ? $"Ошибка синхронизации: {err}" : $"Sync failed: {err}";
    public static string SyncEmpty => Ru ? "Подписка вернула 0 серверов" : "Subscription returned 0 servers";
    public static string PasteVlessUri => Ru ? "Вставьте VLESS URI:" : "Paste VLESS URI(s):";

    // ── Buttons ──
    public static string StartVPN => Ru ? "\u25b6  Запустить VPN" : "\u25b6  Start VPN";
    public static string StopVPN => Ru ? "\u2b1b  Остановить VPN" : "\u2b1b  Stop VPN";
    public static string AddServers => Ru ? "Добавить сервер(ы)" : "Add Server(s)";
    public static string Remove => Ru ? "Удалить" : "Remove";
    public static string AddConfig => Ru ? "Добавить конфиг..." : "Add Config...";
    public static string Apply => Ru ? "\u21bb  Применить" : "\u21bb  Apply";
    public static string BtnAdd => Ru ? "Добавить" : "Add";
    public static string RemoveChecked => Ru ? "Удалить выбранные" : "Remove checked";

    // ── Apps tab ──
    public static string SplitTunnel => Ru ? "Split Tunnel (выбранные приложения)" : "Split Tunnel (selected apps)";
    public static string FullTunnel => Ru ? "Full Tunnel (весь трафик)" : "Full Tunnel (all traffic)";
    public static string AppsHint => Ru
        ? "Выберите группы для маршрутизации через VPN:"
        : "Check groups to route through VPN:";
    public static string CustomAppLabel => Ru
        ? "Добавить приложение (имя процесса):"
        : "Add custom app (process name):";

    // ── Header ──
    public static string ThemeDark => Ru ? "\u25cf Тёмная" : "\u25cf Dark";
    public static string ThemeLight => Ru ? "\u25cb Светлая" : "\u25cb Light";

    // ── Status ──
    public static string NotConnected => Ru ? "Не подключено" : "Not connected";
    public static string Connected(string mode, string? serverName, string? serverIp)
    {
        var prefix = Ru ? $"Подключено [{mode}]" : $"Connected [{mode}]";
        if (string.IsNullOrEmpty(serverName) && string.IsNullOrEmpty(serverIp))
            return prefix;
        var name = serverName ?? "";
        var ip = string.IsNullOrEmpty(serverIp) ? "" : $" ({serverIp})";
        return $"{prefix} → {name}{ip}";
    }

    // ── Action states ──
    public static string Starting => Ru ? "Запуск..." : "Starting...";
    public static string Stopping => Ru ? "Остановка..." : "Stopping...";

    // ── Server list columns ──
    public static string ColName => Ru ? "Имя" : "Name";
    public static string ColServer => Ru ? "Сервер" : "Server";
    public static string ColPort => Ru ? "Порт" : "Port";
    public static string ColSecurity => Ru ? "Защита" : "Security";

    // ── Dialogs ──
    public static string FailedStartVpn => Ru ? "Не удалось запустить VPN:" : "Failed to start VPN:";
    public static string AddServerFirst => Ru
        ? "Сначала добавьте хотя бы один VLESS сервер."
        : "Add at least one VLESS server first.";
    public static string SelectSingBoxConfig => Ru ? "Выберите sing-box JSON конфиг" : "Select sing-box JSON config";
    public static string InvalidConfig => Ru ? "Некорректный конфиг:" : "Invalid config:";
    public static string ConfigExists(string name) => Ru
        ? $"Конфиг '{name}' уже существует."
        : $"Config '{name}' already exists.";

    // ── Tray ──
    public static string TrayStart => Ru ? "\u25b6 Запустить VPN" : "\u25b6 Start VPN";
    public static string TrayStop => Ru ? "\u2b1b Остановить VPN" : "\u2b1b Stop VPN";
    public static string TraySettings => Ru ? "Настройки..." : "Settings...";
    public static string TrayExit => Ru ? "Выход" : "Exit";

    // ── Server detail editor ──
    public static string FieldName => Ru ? "Имя:" : "Name:";
    public static string FieldServer => Ru ? "Сервер:" : "Server:";
    public static string FieldPort => Ru ? "Порт:" : "Port:";
    public static string FieldUuid => "UUID:";
    public static string FieldPublicKey => Ru ? "Public Key:" : "Public Key:";
    public static string FieldShortId => Ru ? "Short ID:" : "Short ID:";

    // ── Hints ──
    public static string DoubleClickEditServer => Ru
        ? "Двойной клик — редактировать сервер. Вставьте VLESS URI выше."
        : "Double-click to edit server. Paste VLESS URI(s) above.";
    public static string DoubleClickActiveConfig => Ru
        ? "Двойной клик — сделать активным. Поддерживается любой протокол."
        : "Double-click to set active config. Any protocol supported.";
    public static string AddCustomAppHint => Ru
        ? "Добавить приложение (имя процесса, например Discord, Chrome):"
        : "Add custom app (process name, e.g. Discord, Chrome):";
    public static string TcpUdpHint => Ru
        ? "VLESS+Reality маршрутизирует TCP. Для UDP (игры, QUIC) используйте Custom Config с TUIC или Hysteria2 outbound."
        : "VLESS+Reality routes TCP only. For UDP (games, QUIC) use Custom Config with a TUIC or Hysteria2 outbound.";

    // ── Bypass / Strict ──
    public static string BypassRussianTrafficLabel => Ru
        ? "Российский трафик через реальный IP"
        : "Russian traffic via real IP";
    public static string BypassRussianTrafficHint => Ru
        ? "Сайты и приложения с российскими доменами/IP идут напрямую, минуя VPN. Защищает VPN-сервер от блокировок российскими сервисами."
        : "Russian domains and IPs go directly, bypassing VPN. Protects the VPN server from being blocked by Russian services.";
    public static string CheckLeaks => Ru ? "Проверить утечки" : "Check leaks";
    public static string ShowLogs => Ru ? "Логи" : "Logs";

    public static string StrictModeLabel => Ru
        ? "Строгий режим (быстрая реакция на сбои)"
        : "Strict mode (faster crash detection)";
    public static string StrictModeHint => Ru
        ? "Health check каждые 5 секунд вместо 30. Уменьшает окно потенциальной утечки трафика при крахе sing-box."
        : "Health check every 5 seconds instead of 30. Reduces the leak window if sing-box silently hangs.";
    public static string ForceIpv4Label => Ru
        ? "Только IPv4 (защита от IPv6 leak)"
        : "Force IPv4 only (IPv6 leak protection)";
    public static string FlushDnsLabel => Ru
        ? "Очищать DNS кэш при подключении"
        : "Flush DNS cache on connect";
    public static string StrictDnsLabel => Ru
        ? "Строгий DNS (весь DNS через VPN, без утечек)"
        : "Strict DNS (all DNS via VPN, no leaks)";

    // ── Updates ──
    public static string CheckForUpdates => Ru ? "Проверить обновления" : "Check for updates";
    public static string Checking => Ru ? "Проверка..." : "Checking...";
    public static string UpToDate => Ru ? "Актуальная версия" : "Up to date";
    public static string CheckFailed => Ru ? "Ошибка проверки" : "Check failed";
    public static string UpdateAvailableShort => Ru ? "Обновление доступно" : "Update available";
    public static string UpdateAvailableMessage => Ru
        ? "Доступно обновление v{0} ({1:F1} МБ)"
        : "Update available: v{0} ({1:F1} MB)";
    public static string UpdateButton => Ru ? "Обновить" : "Update";
    public static string UpdateDownloading => Ru ? "Загрузка обновления..." : "Downloading update...";
    public static string UpdateApplying => Ru ? "Применение обновления..." : "Applying update...";
    public static string UpdateRestarting => Ru ? "Перезапуск..." : "Restarting...";
    public static string UpdateFailed => Ru ? "Ошибка обновления: {0}" : "Update failed: {0}";

    // ── Channel ──
    public static string ChannelStable => Ru ? "● Стабильная" : "● Stable";
    public static string ChannelExperimental => Ru ? "⚠ Эксперимент." : "⚠ Experimental";

    // ── Telegram Proxy ──
    public static string TabTelegram => "Telegram";
    public static string TgProxyDescription => Ru
        ? "MTProto прокси для обхода блокировки Telegram. Работает локально, трафик идёт напрямую к серверам Telegram через WebSocket."
        : "MTProto proxy to bypass Telegram blocking. Runs locally, traffic goes directly to Telegram servers via WebSocket.";
    public static string TgProxySetupHint => Ru
        ? "Настройка: Telegram \u2192 Настройки \u2192 Продвинутые \u2192 Тип соединения \u2192 MTProto Proxy"
        : "Setup: Telegram \u2192 Settings \u2192 Advanced \u2192 Connection type \u2192 MTProto Proxy";
    public static string TgProxyPort => Ru ? "Порт:" : "Port:";
    public static string TgProxySecret => Ru ? "Secret:" : "Secret:";
    public static string TgProxyLink => Ru ? "Ссылка для подключения:" : "Proxy link:";
    public static string TgProxyCopy => Ru ? "Копировать" : "Copy";
    public static string TgProxyCopied => Ru ? "Скопировано!" : "Copied!";
    public static string TgProxyRegenerate => Ru ? "Новый" : "New";
    public static string TgProxyStart => Ru ? "Запустить Telegram Proxy" : "Start Telegram Proxy";
    public static string TgProxyStop => Ru ? "Остановить Telegram Proxy" : "Stop Telegram Proxy";
    public static string TgProxyOpenInTelegram => Ru ? "Открыть в Telegram" : "Open in Telegram";
    public static string TgProxySetupOnce => Ru
        ? "Нажмите 'Открыть в Telegram' один раз для настройки прокси. После этого просто Start/Stop."
        : "Click 'Open in Telegram' once to set up the proxy. After that just Start/Stop.";
    public static string OpenFolder => Ru ? "Открыть папку" : "Open folder";
    public static string OpenGitHub => "GitHub";

    // ── Autostart ──
    // ── Subscriptions (multi) ──
    public static string SubscriptionsSection => Ru ? "Подписки" : "Subscriptions";
    public static string SubscriptionNameHint => Ru ? "Имя" : "Name";
    public static string AddSubscription => Ru ? "+ Добавить" : "+ Add";
    public static string RefreshAll => Ru ? "Обновить все" : "Refresh all";
    public static string NeverRefreshed => Ru ? "никогда" : "never";
    public static string SubUpdatedAt => Ru ? "Обновлено" : "Updated";

    // ── Zapret tools ──
    public static string ToolsSection => Ru ? "Инструменты" : "Tools";
    public static string RunDiagnostics => Ru ? "Запустить диагностику" : "Run diagnostics";
    public static string ClearDiscordCache => Ru ? "Очистить кэш Discord" : "Clear Discord cache";
    public static string UpdateHostsFile => Ru ? "Обновить hosts (Flowseal)" : "Update hosts (Flowseal)";
    public static string OpenServiceMenu => Ru
        ? "Открыть меню service.bat"
        : "Open service.bat menu";

    // ── Zapret sections (master-detail) ──
    public static string ZapretSecStatus       => Ru ? "Статус" : "Status";
    public static string ZapretSecStrategy     => Ru ? "Стратегия" : "Strategy";
    public static string ZapretSecHosts        => "Hosts";
    public static string ZapretSecFilters      => Ru ? "Фильтры" : "Filters";
    public static string ZapretSecUpdates      => Ru ? "Обновления" : "Updates";
    public static string ZapretSecDiagnostics  => Ru ? "Диагностика" : "Diagnostics";
    public static string ZapretSecAdvanced     => Ru ? "Дополнительно" : "Advanced";

    // Filters
    public static string GameFilter => Ru ? "Игровой фильтр (диапазон 1024-65535)" : "Game filter (port range 1024-65535)";
    public static string GameFilterOff => Ru ? "Выкл" : "Off";
    public static string GameFilterAll => Ru ? "TCP + UDP" : "TCP + UDP";
    public static string GameFilterTcp => "TCP";
    public static string GameFilterUdp => "UDP";

    public static string IpSetFilter => Ru ? "IPSet фильтр" : "IPSet filter";
    public static string IpSetAny => Ru ? "Any (весь трафик)" : "Any (all traffic)";
    public static string IpSetLoaded => Ru ? "Loaded (список из файла)" : "Loaded (from list file)";
    public static string IpSetNone => Ru ? "None (отключено)" : "None (disabled)";

    // Updates
    public static string UpdateIpSet => Ru ? "Обновить IPSet список" : "Update IPSet list";
    public static string AutoUpdateCheckLabel => Ru
        ? "Авто-проверка обновлений zapret"
        : "Auto-check zapret updates";

    // Advanced
    public static string RunTestsLabel => Ru ? "Запустить тесты сети" : "Run network tests";
    public static string RemoveServiceLabel => Ru ? "Удалить службу zapret" : "Remove zapret service";

    public static string ApplyChanges => Ru ? "↻  Применить изменения" : "↻  Apply changes";
    public static string ChangesApplied => Ru ? "Изменения применены" : "Changes applied";
    public static string ApplyFailed => Ru ? "Не удалось применить" : "Apply failed";

    public static string AddCategory => Ru ? "+ Новая категория" : "+ New category";
    public static string EnableWholeGroup => Ru ? "Включить всю группу" : "Enable whole group";
    public static string CategoryNamePrompt => Ru ? "Имя категории:" : "Category name:";
    public static string AddAppHint => Ru ? "имя процесса (например Discord)" : "process name (e.g. Discord)";

    // ── App group display names ──
    public static string GroupDisplayName(string internalName) => internalName switch
    {
        "Discord_Privacy" => "Discord",
        "Work_Suite"      => Ru ? "Работа" : "Work",
        "Browsers"        => Ru ? "Браузеры" : "Browsers",
        "Terminal"        => Ru ? "Терминал" : "Terminal",
        "Custom Apps"     => Ru ? "Свои" : "Custom",
        _                 => internalName
    };

    public static string SectionRouting => Ru ? "Маршрутизация" : "Routing";
    public static string SectionLeakProtection => Ru ? "Защита от утечек" : "Leak Protection";
    public static string SectionContent => Ru ? "Контент" : "Content";
    public static string SectionUpdates => Ru ? "Обновления" : "Updates";
    public static string AutostartSection => Ru ? "Автозапуск" : "Autostart";
    public static string AutostartVpn => Ru
        ? "Запускать VPN при старте системы"
        : "Start VPN on system boot";
    public static string AutostartZapret => Ru
        ? "Запускать Zapret при старте системы"
        : "Start Zapret on system boot";
    public static string AutostartTgProxy => Ru
        ? "Запускать TgProxy при старте системы"
        : "Start TgProxy on system boot";
    public static string AutostartUi => Ru
        ? "Запускать интерфейс при входе в Windows"
        : "Start UI on Windows logon";

    // ── Free Configs ──
    public static string TabFreeConfigs => Ru ? "Free" : "Free";
    public static string FcDashboardTotal     => Ru ? "Всего"         : "Total";
    public static string FcDashboardWorking   => Ru ? "Работают"      : "Working";
    public static string FcDashboardTimeout   => Ru ? "Timeout"       : "Timeout";
    public static string FcDashboardUnreach   => Ru ? "Недоступны"    : "Unreachable";
    public static string FcDashboardTlsFail   => Ru ? "TLS провал"    : "TLS failed";
    public static string FcDashboardVerified  => Ru ? "Проверено"     : "Verified";
    public static string FcDashboardFake      => Ru ? "Подозр."       : "Fake";
    public static string FcDeepVerify         => Ru ? "✓✓ Глубокая проверка" : "✓✓ Deep verify";
    public static string FcStatusNoDeepCandidates => Ru
        ? "Нет кандидатов для глубокой проверки — сначала «Обновить список»."
        : "No candidates to deep-verify — click 'Refresh list' first.";
    public static string FcStatusDeepVerifyStart(int target) => Ru
        ? $"Ищу {target} реально рабочих конфигов..."
        : $"Hunting for {target} truly working configs...";
    public static string FcStatusDeepVerifyProbe(int found, int target, int tested, string host) => Ru
        ? $"Найдено {found}/{target} · проверяю {host}..."
        : $"Found {found}/{target} · probing {host}...";
    public static string FcStatusDeepVerifyProgress(int found, int target, int tested, int totalQueue) => Ru
        ? $"Найдено {found}/{target} · проверено {tested}/{totalQueue}"
        : $"Found {found}/{target} · tested {tested}/{totalQueue}";
    public static string FcStatusDeepVerifyDone(int verified) => Ru
        ? $"Готово: найдено {verified} реально рабочих (✓✓)"
        : $"Done: {verified} truly working found (✓✓)";
    public static string FcStatusDeepVerifyExhausted(int verified, int tested) => Ru
        ? $"Список исчерпан — протестировано {tested}, найдено {verified} рабочих"
        : $"Queue exhausted — tested {tested}, found {verified} working";

    public static string FcDeepTargetLabel => Ru ? "Цель:" : "Target:";
    public static string FcDeepExcludeRu   => Ru ? "Пропускать RU" : "Skip RU servers";
    public static string FcDeepHint        => Ru
        ? "Глубокая проверка: временный sing-box + реальный HTTP. Ищет пока не найдёт N рабочих или не кончатся кандидаты. Может идти часами — это норм."
        : "Deep verify: spins up a temporary sing-box + real HTTP. Runs until N working configs are found or candidates exhausted. May take hours — that's fine.";
    public static string FcCountryFilter      => Ru ? "Страна:"       : "Country:";
    public static string FcOnlyWorking        => Ru ? "Только рабочие" : "Only working";
    public static string FcRefreshSources     => Ru ? "↻ Обновить список"    : "↻ Refresh list";
    public static string FcRetestAll          => Ru ? "▶ Перепроверить"      : "▶ Retest all";
    public static string FcConnectHint        => Ru ? "Выберите строку ↑ и нажмите Connect (или двойной клик)"
                                                    : "Select a row ↑ and click Connect (or double-click)";
    public static string FcTipVpnActive       => Ru
        ? "⚠ VPN активен — результаты пинга проходят через туннель и могут быть недостоверны. Для точного теста отключите VPN."
        : "⚠ VPN is active — ping results go through the tunnel and may be inaccurate. Disconnect VPN for accurate tests.";
    public static string FcCancel             => Ru ? "Отмена"        : "Cancel";
    public static string FcApplySelected      => Ru ? "Подключить"    : "Connect";
    public static string FcCountryAll         => Ru ? "Все страны"    : "All countries";
    public static string FcColCountry         => Ru ? "Страна"        : "Country";
    public static string FcColEndpoint        => Ru ? "Адрес"         : "Endpoint";
    public static string FcColLatency         => Ru ? "Пинг"          : "Latency";
    public static string FcColSni             => "SNI";
    public static string FcColTransport       => Ru ? "Транспорт"     : "Transport";
    public static string FcEmptyHint          => Ru
        ? "Нажмите «Обновить источники», чтобы загрузить список публичных VLESS-конфигов."
        : "Click 'Refresh sources' to load the list of public VLESS configs.";
    public static string FcEmptyCtaTitle      => Ru
        ? "Нет загруженных конфигов"
        : "No configs loaded yet";
    public static string FcEmptyCtaSubtitle   => Ru
        ? "Скачайте публичные VLESS-конфиги и узнайте какие из них работают прямо сейчас."
        : "Download public VLESS configs and see which ones are working right now.";
    public static string FcEmptyCtaButton     => Ru
        ? "⚡ Загрузить список конфигов"
        : "⚡ Load configs list";
    public static string FcFilteredEmpty      => Ru
        ? "Ничего не найдено по фильтру. Снимите «Только рабочие» или выберите «Все страны»."
        : "No results for current filter. Uncheck 'Only working' or choose 'All countries'.";
    public static string FcRefreshHint        => Ru
        ? "Первый запуск ≈1 мин. Тестируется до 500 серверов за раз — повторяйте для более полных данных."
        : "First run ≈1 min. Tests up to 500 servers at a time — repeat for fuller coverage.";
    public static string FcPageDescription    => Ru
        ? "Публичные VLESS-конфиги. Проверка: TCP + TLS handshake с валидацией сертификата. ✓ = сервер живой и TLS-валидный."
        : "Public VLESS configs. Tests: TCP + TLS handshake with cert validation. ✓ = server alive and TLS-valid.";
    public static string FcStatusEmpty        => Ru ? "Кэш пуст — нажмите «Обновить»" : "Cache is empty — click 'Refresh'";
    public static string FcStatusCancelled    => Ru ? "Отменено" : "Cancelled";
    public static string FcStatusApplyFailed  => Ru ? "Не удалось подключиться" : "Apply failed";
    public static string FcStatusCacheAge(string age) => Ru
        ? $"Обновлено {age}"
        : $"Updated {age}";
    public static string FcStatusRefreshed(int n) => Ru
        ? $"Загружено {n} конфигов"
        : $"Loaded {n} configs";
    public static string FcStatusTested(int n) => Ru
        ? $"Протестировано {n} конфигов"
        : $"Tested {n} configs";
    public static string FcStatusFailed(string err) => Ru
        ? $"Ошибка: {err}"
        : $"Error: {err}";
    public static string FcStatusApplying(string ep) => Ru
        ? $"Подключение к {ep}..."
        : $"Connecting to {ep}...";
    public static string FcStatusApplied(string ep) => Ru
        ? $"Подключено: {ep}"
        : $"Connected: {ep}";

    // ── Service (Windows-only) ──
    public static string AutostartWithWindows => Ru ? "Автозапуск с Windows" : "Autostart with Windows";
    public static string RestartService => Ru ? "Перезапустить службу" : "Restart Service";
    public static string ReinstallService => Ru ? "Переустановить" : "Reinstall";
    public static string InstallingService => Ru ? "Установка службы..." : "Installing service...";
    public static string RemovingService => Ru ? "Удаление службы..." : "Removing service...";
}
