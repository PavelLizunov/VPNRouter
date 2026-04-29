namespace VPNRouter.App.Localization;

/// <summary>
/// Bilingual string provider (EN/RU). Port of VPNRouter.GUI.Localization.Strings for Avalonia.
/// </summary>
public static class Strings
{
    public static string Lang { get; set; } = "en";
    private static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    // v2.29.0: dynamic OS name shown in user-facing autostart copy. Mac
    // users were seeing "Windows" hardcoded in Simple-mode autostart card
    // and Network → Autostart labels (reported 2026-04-29). Now Strings
    // detect runtime platform and substitute "macOS" / "Linux" / "Windows"
    // into RU+EN templates. Does NOT change Windows-Service-tech labels
    // (those reference an actual Windows-only API surface).
    public static string OsDisplayName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS() ? "macOS" : "Linux";

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
    // v2.25.3 — extra column labels for the redesigned Servers / Subscribe rows
    public static string ColIp => "IP";
    public static string ColPing => "Ping";

    // v2.25.4 — Settings/Routing radio-card descriptions (Phase 4 redesign).
    // Each tunnel mode gets a one-line subtitle under the title so the user
    // understands the choice without hovering for a tooltip.
    public static string RoutingDescription => Ru
        ? "Определяет, какой трафик пойдёт через VPN."
        : "Determines which traffic goes through the VPN.";
    public static string SplitTunnelTitle => Ru ? "Split Tunnel" : "Split Tunnel";
    public static string SplitTunnelSubtitle => Ru
        ? "Только выбранные приложения. Остальное идёт напрямую."
        : "Only selected apps. Everything else goes direct.";
    public static string FullTunnelTitle => Ru ? "Full Tunnel" : "Full Tunnel";
    public static string FullTunnelSubtitle => Ru
        ? "Весь трафик ОС через VPN, включая игры и банки."
        : "All OS traffic through VPN — games and banks included.";

    // Service actions in Settings → Autostart (moved here from the footer
    // when MainWindow compacted its footer in v2.25.0).
    public static string ServiceStatusLabel => Ru ? "Служба VPN" : "VPN Service";
    public static string ServiceRunningText => Ru ? "Работает" : "Running";
    public static string ServiceStoppedText => Ru ? "Не запущена" : "Not running";
    public static string ServiceInstalledText => Ru ? "Установлена" : "Installed";
    public static string ServiceNotInstalledText => Ru ? "Не установлена" : "Not installed";

    // v2.26.0 — master service toggle + grouping labels for the refactored
    // Autostart panel (single source of truth for the install state +
    // clearly-named sub-groups for the two categories of autostart).
    public static string ServiceMasterTitle => Ru
        ? "Фоновая служба Windows"
        : "Windows background service";
    public static string ServiceMasterSubtitle => Ru
        ? "Запускает VPN / Zapret / Telegram-прокси при загрузке ОС до входа в систему. Требует прав администратора."
        : "Starts VPN / Zapret / Telegram proxy at OS boot before you log in. Requires administrator privileges.";
    public static string ServiceEnableLabel => Ru
        ? "Включить фоновую службу"
        : "Enable background service";
    public static string ServiceInstalling => Ru ? "Установка..." : "Installing...";
    public static string ServiceRemoving => Ru ? "Удаление..." : "Removing...";
    public static string ServiceComponentsHeader => Ru
        ? "Запускать при старте службы"
        : "Start automatically with the service";
    public static string ServiceComponentsDisabledHint => Ru
        ? "Флаги применятся после включения службы выше."
        : "Flags take effect once the service above is enabled.";
    public static string AutostartUiSessionHeader => Ru
        ? "Пользовательский сеанс"
        : "User session";

    // v2.27 Bug C — two-section layout for the Autostart panel, grouping
    // controls by WHEN the autostart happens rather than by which Windows
    // mechanism it's wired to. Makes "I want VPN on boot" actionable via a
    // single checkbox instead of forcing users to understand service vs.
    // Run-key vs. yaml flag.
    public static string AutostartBootSectionTitle => Ru
        ? "На старте Windows (до логина)"
        : "At Windows startup (before sign-in)";
    public static string AutostartBootSectionSub => Ru
        ? "Нужна служба Windows для запуска VPN, Zapret или Telegram-прокси до входа пользователя"
        : "Needs the Windows service to start VPN, Zapret or Telegram proxy before a user signs in";
    public static string AutostartComponentsInfoHint => Ru
        ? "Эти флаги читает служба при boot. Требуется установленная служба."
        : "These flags are read by the service at boot. Requires the service to be installed.";
    public static string AutostartLoginSectionTitle => Ru
        ? "При входе пользователя"
        : "At user sign-in";
    public static string AutostartLoginAppDescription => Ru
        ? "Запускает приложение VPNRouter после входа. VPN придётся стартануть вручную или включить «на старте Windows» выше."
        : "Launches VPNRouter after you sign in. VPN itself must be started manually, or enable \u201Cat Windows startup\u201D above.";

    /// <summary>Prominent running-state line with PID, e.g. "● Running — PID 1234".
    /// Replaces the tiny pill that was easy to miss in v2.26.x.</summary>
    public static string ServiceRunningLine(int pid) => Ru
        ? $"\u25CF Запущена \u2014 PID {pid}"
        : $"\u25CF Running \u2014 PID {pid}";
    public static string ServiceStoppedLine => Ru
        ? "\u25CB Остановлена"
        : "\u25CB Stopped";

    // v2.27.0-r2 — Simple-mode autostart link-card. Replaces the old
    // SmpAutostartChecked checkbox whose computed-state UX caused the
    // "how do I disable it?" confusion in r1 testing. The card now just
    // navigates into Advanced → Network → Autostart where the full flow
    // (install / configure / uninstall) lives.
    public static string SmpAutostartCardTitle => Ru
        ? "Автозапуск"
        : "Autostart";
    public static string SmpAutostartCardOn => Ru
        ? "Служба установлена и запущена"
        : "Service installed and running";
    public static string SmpAutostartCardOff => Ru
        ? $"Настроить автозапуск VPN при старте {OsDisplayName}"
        : $"Configure VPN autostart at {OsDisplayName} boot";

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
    public static string SettingsAutosaved => Ru
        ? "✓ Настройки сохраняются автоматически при изменении"
        : "✓ Settings are auto-saved on every change";
    public static string ApplyNowReloadVpn => Ru
        ? "↻ Применить сейчас (перезапустить VPN)"
        : "↻ Apply now (reload VPN)";
    public static string ApplyNowHint => Ru
        ? "Переприменить настройки к работающему VPN без переподключения (hot-reload через Clash API)"
        : "Re-apply settings to the running VPN without a reconnect (hot-reload via Clash API)";

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
        ? $"Запускать интерфейс при входе в {OsDisplayName}"
        : $"Start UI on {OsDisplayName} logon";

    // ── Free Configs ──
    public static string TabFreeConfigs => Ru ? "Free" : "Free";
    public static string FcDashboardTotal     => Ru ? "Всего"         : "Total";
    public static string FcDashboardWorking   => Ru ? "Работают"      : "Working";
    public static string FcDashboardTimeout   => Ru ? "Timeout"       : "Timeout";
    public static string FcDashboardUnreach   => Ru ? "Недоступны"    : "Unreachable";
    public static string FcDashboardTlsFail   => Ru ? "TLS провал"    : "TLS failed";
    public static string FcDashboardVerified  => Ru ? "Проверено"     : "Verified";
    public static string FcDashboardFake      => Ru ? "Подозр."       : "Fake";
    public static string FcDeepVerify         => Ru ? "✓✓ Найти рабочие конфиги" : "✓✓ Find working configs";
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

    // v2.28.5-r2/r4: batched fetch+test+verify status messages
    public static string FcStatusBatchedSearchStart(int target, int poolSize) => Ru
        ? $"Поиск {target} рабочих конфигов из пула {poolSize}..."
        : $"Searching {target} working configs from pool of {poolSize}...";
    public static string FcStatusBatchedTcpTls(int found, int target, int batchNum, int totalBatches) => Ru
        ? $"Найдено {found}/{target} · батч {batchNum}/{totalBatches} (TCP+TLS)..."
        : $"Found {found}/{target} · batch {batchNum}/{totalBatches} (TCP+TLS)...";
    public static string FcStatusBatchedTcpTlsProgress(int found, int target, int batchNum, int totalBatches, int done, int total) => Ru
        ? $"Найдено {found}/{target} · батч {batchNum}/{totalBatches} · проверено {done}/{total}"
        : $"Found {found}/{target} · batch {batchNum}/{totalBatches} · tested {done}/{total}";
    public static string FcStatusBatchedDeepVerify(int found, int target, int batchNum, int totalBatches, int candidates) => Ru
        ? $"Найдено {found}/{target} · батч {batchNum}/{totalBatches} · глубокая проверка {candidates} кандидатов..."
        : $"Found {found}/{target} · batch {batchNum}/{totalBatches} · deep-verifying {candidates} candidates...";
    public static string FcStatusBatchedFound(int found, int target) => Ru
        ? $"Найдено {found}/{target} рабочих конфигов..."
        : $"Found {found}/{target} working configs...";
    /// <summary>v2.28.5-r6: per-probe status update so the UI doesn't appear
    /// frozen during the deep-verify phase. Each probe takes 3-5s, 5 in
    /// parallel → status flips every ~600 ms.</summary>
    public static string FcStatusBatchedProbing(int found, int target, string host, int port, string cc) => Ru
        ? $"Найдено {found}/{target} · проверяю {host}:{port} [{cc}]..."
        : $"Found {found}/{target} · probing {host}:{port} [{cc}]...";

    public static string FcDeepTargetLabel => Ru ? "Цель:" : "Target:";
    public static string FcDeepExcludeRu   => Ru ? "Пропускать RU" : "Skip RU servers";
    public static string FcDeepHint        => Ru
        ? "Скачает публичные VLESS-конфиги и проверит каждый реальной попыткой подключения. Найдёт N рабочих с пингом ниже порога."
        : "Downloads public VLESS configs and tries each one with a real connection. Stops when N working ones meet your ping threshold.";
    public static string FcStatusMainVpnActive => Ru
        ? "⚠ Основной VPN активен — результаты проверки могут быть недостоверны. Отключите VPN перед глубокой проверкой."
        : "⚠ Main VPN is active — verification results may be unreliable. Disconnect VPN first.";
    public static string FcOpenLogs         => Ru ? "📁 Логи"                : "📁 Logs";
    public static string FcClearFailed      => Ru ? "🧹 Убрать мусор"        : "🧹 Clear dead";
    public static string FcKeepVerified     => Ru ? "⭐ Только ✓✓"           : "⭐ Keep ✓✓ only";
    public static string FcKeepVerifiedOnly => FcKeepVerified;
    public static string FcClearAll         => Ru ? "💥 Очистить всё"        : "💥 Clear all";
    public static string FcCleanupHint      => Ru
        ? "Очистка: убери мусорные записи, чтобы поиск работал быстрее. При следующем обновлении всё перезагрузится из источников."
        : "Cleanup: remove dead entries to speed up Refresh. Next Refresh re-fetches from sources.";
    public static string FcStatusCleared(int removed, int kept) => Ru
        ? $"Удалено {removed} · осталось {kept}"
        : $"Removed {removed} · kept {kept}";
    public static string FcCountryFilter      => Ru ? "Страна:"       : "Country:";
    public static string FcRefreshSources     => Ru ? "↻ Обновить список"    : "↻ Refresh list";
    public static string FcRetestAll          => Ru ? "▶ Перепроверить"      : "▶ Retest all";
    public static string FcConnectHint        => Ru ? "Выберите строку ↑ и нажмите «Подключить» (или двойной клик)"
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
    public static string FcColBandwidth       => Ru ? "Скорость"      : "Speed";
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
        ? "Ничего не найдено по фильтру. Снимите «Только рабочие», увеличьте порог пинга или выберите «Все страны»."
        : "No results for current filter. Uncheck 'Only working', raise the ping threshold, or choose 'All countries'.";
    public static string FcRefreshHint        => Ru
        ? "Первый запуск ≈1 мин. Тестируется до 500 серверов за раз — повторяйте для более полных данных."
        : "First run ≈1 min. Tests up to 500 servers at a time — repeat for fuller coverage.";

    // v2.13.17 — Smart Refresh (latency goal)
    public static string FcSmartRefreshLabel => Ru ? "🎯 Smart Refresh (стоп при достижении цели)" : "🎯 Smart Refresh (stop when goal reached)";
    public static string FcTargetNLabel      => Ru ? "Найти:" : "Find:";
    public static string FcConfigsWord       => Ru ? "конфигов" : "configs";
    public static string FcWithPingUnder     => Ru ? "с пингом <" : "with ping <";
    public static string FcMsUnit            => "ms";
    public static string FcSmartRefreshHint  => Ru
        ? "Ускоряет Refresh: остановка как только накопится N конфигов с низким пингом. Отключите для полного сканирования."
        : "Speeds up Refresh: stops once N low-ping configs are found. Uncheck for full scan.";

    // v2.28.4-r2: Quickstart banner removed (single-button flow makes the 3-step lecture obsolete).

    // v2.14.7 — collapsible More Options
    public static string FcMoreOptions => Ru ? "⚙ Больше опций (фильтры, очистка, свои источники)" : "⚙ More options (filters, cleanup, user sources)";

    // v2.28.4-r1: 6-section nav removed (FreeConfigs is now single Simple page).
    public static string FcListHeader    => Ru ? "📋 Конфиги"       : "📋 Configs";
    public static string FcListShown     => Ru ? "показано"         : "shown";

    // Stop button in the Free Configs search card
    public static string FcDeepStop        => Ru ? "⏹ Остановить поиск" : "⏹ Stop search";
    public static string FcDeepStopTooltip => Ru
        ? "Прекратить текущий поиск. Найденные до отмены конфиги сохранены в кэше."
        : "Abort the current search. Configs found before cancel are preserved in cache.";

    // v2.28.4-r4 — Advanced settings expander label inside the green search card
    public static string FcAdvancedSettings => Ru ? "▾ Настройки" : "▾ Settings";

    // ── v2.28.6 — Free Configs tab strip (Search / Saved) + Saved-tab UI ──
    public static string FcTabSearch                 => Ru ? "▶ Поиск"     : "▶ Search";
    public static string FcTabSaved                  => Ru ? "★ Сохранённые" : "★ Saved";
    public static string FcTabSavedWithCount(int n)  => Ru ? $"★ Сохранённые ({n})" : $"★ Saved ({n})";
    public static string FcSavedTabHint              => Ru
        ? "Конфиги, найденные в прошлых поисках. Они могут перестать работать со временем — нажмите ↻ чтобы перепроверить."
        : "Configs you've found in past searches. They may stop working over time — click ↻ to recheck.";
    public static string FcSavedRecheckStaleBtn(int n) => Ru
        ? $"↻ Перепроверить ({n})"
        : $"↻ Recheck ({n})";
    public static string FcSavedRecheckAllBtn        => Ru ? "↻ Перепроверить всё" : "↻ Recheck all";
    public static string FcSavedClearAllBtn          => Ru ? "✕ Удалить всё"     : "✕ Clear all";
    public static string FcSavedColStatus            => Ru ? "Статус"            : "Status";
    public static string FcSavedEmpty                => Ru
        ? "Здесь появятся ваши рабочие конфиги. Нажмите «Поиск» чтобы найти первые."
        : "Your working configs will appear here. Click \"Search\" to find your first ones.";
    public static string FcSavedRecheckOneTooltip    => Ru
        ? "Перепроверить этот конфиг (полная глубокая проверка)"
        : "Recheck this config (full deep verify)";
    public static string FcSavedRemoveOneTooltip     => Ru
        ? "Удалить из сохранённых"
        : "Remove from saved";
    public static string FcFreshnessFresh            => Ru ? "свежий"           : "fresh";
    public static string FcFreshnessAgeingDays(int d) => Ru ? $"{d}д назад" : $"{d}d ago";
    public static string FcFreshnessStale            => Ru ? "устарел"          : "stale";
    public static string FcFreshnessFailed           => Ru ? "не работает"      : "failed";
    public static string FcStatusRecheckOne(string host, int port, string cc) => Ru
        ? $"Перепроверка {host}:{port} [{cc}]..."
        : $"Rechecking {host}:{port} [{cc}]...";
    public static string FcStatusRecheckAllStart(int total) => Ru
        ? $"Перепроверка {total} конфигов..."
        : $"Rechecking {total} configs...";
    public static string FcStatusRecheckAllProgress(int done, int total) => Ru
        ? $"Перепроверка {done}/{total}..."
        : $"Rechecking {done}/{total}...";
    public static string FcStatusRecheckAllDone(int verified, int failed) => Ru
        ? $"Перепроверено · {verified} работают, {failed} не работают"
        : $"Rechecked · {verified} working, {failed} failed";

    /// <summary>v2.28.6-r3: thin hint shown inside the empty search-tab
    /// list area. Replaces the v2.28.6-r1/r2 "no configs loaded" CTA card —
    /// the green search card right above the list IS the call-to-action,
    /// a second button below was redundant and broke the visual style of
    /// other pages (ServersPage / ToolsPage have no big empty-state CTA).</summary>
    public static string FcSearchListEmptyHint => Ru
        ? "Нажмите кнопку выше, чтобы найти конфиги."
        : "Click the button above to find configs.";

    // v2.13.18 — Fast scan toggle
    public static string FcFastScanLabel => Ru ? "⚡ Fast scan (только TCP, без TLS)" : "⚡ Fast scan (TCP only, no TLS)";
    public static string FcFastScanHint  => Ru
        ? "В 3 раза быстрее, но помечает как 'рабочие' даже honeypot-ы (открытый порт ≠ VLESS). Используйте только если Deep Verify отфильтрует дальше."
        : "3× faster but marks as 'working' even honeypots (open port ≠ VLESS). Use only if Deep Verify filters further.";

    // v2.14.3 — Deep Verify presets
    public static string FcPresetLabel    => Ru ? "Пресет:" : "Preset:";
    public static string FcPresetGaming   => Ru ? "⚡ Gaming (пинг<60ms, bw>2 Mbps)" : "⚡ Gaming (ping<60ms, bw>2 Mbps)";
    public static string FcPresetStream   => Ru ? "📺 Streaming (пинг<250ms, bw>10 Mbps)" : "📺 Streaming (ping<250ms, bw>10 Mbps)";
    public static string FcPresetChat     => Ru ? "💬 Chat/web (пинг<300ms, bw>1 Mbps)" : "💬 Chat/web (ping<300ms, bw>1 Mbps)";
    public static string FcPresetBest     => Ru ? "🚀 Best effort (любой рабочий)" : "🚀 Best effort (any verified)";
    public static string FcPresetCustom   => Ru ? "⚙ Custom" : "⚙ Custom";
    public static string FcCustomPing     => Ru ? "Макс пинг:" : "Max ping:";
    public static string FcCustomBw       => Ru ? "Мин bw:" : "Min bw:";
    public static string FcMbpsUnit       => "Mbps";
    public static string FcBandwidthHint  => Ru
        ? "Замер bandwidth скачивает ~5 MB через прокси (~150 MB для 30 кандидатов). OK на wifi, осторожно на мобильном."
        : "Bandwidth test downloads ~5 MB per config via proxy (~150 MB for 30 candidates). OK on wifi, mind mobile data.";

    // v2.14.4 — User sources
    public static string FcUserSrcSection      => Ru ? "👤 Мои источники" : "👤 My sources";
    public static string FcUserSrcNamePlaceholder => Ru ? "Имя (опционально)" : "Name (optional)";
    public static string FcUserSrcUrlPlaceholder  => Ru ? "URL подписки (https://...)" : "Subscription URL (https://...)";
    public static string FcUserSrcAdd          => Ru ? "+ Добавить" : "+ Add";
    public static string FcUserSrcHint         => Ru
        ? "Ваши собственные VLESS-подписки (raw или base64). Объединяются с 14 встроенными источниками при Refresh."
        : "Your own VLESS subscription URLs (raw or base64). Merged with the 14 built-in sources during Refresh.";
    public static string FcUserSrcEmpty        => Ru ? "Пусто. Добавьте URL выше." : "Empty. Add a URL above.";
    public static string FcUserSrcAdded        => Ru ? "Источник добавлен" : "Source added";
    public static string FcUserSrcRemoved      => Ru ? "Источник удалён" : "Source removed";
    public static string FcUserSrcDuplicate    => Ru ? "Этот URL уже добавлен" : "URL already in list";
    public static string FcUserSrcInvalidUrl   => Ru ? "Невалидный URL" : "Invalid URL";
    public static string FcUserSrcEmptyUrl     => Ru ? "Введите URL" : "Enter a URL";

    // v2.14.5 — Tooltips
    public static string FcRefreshTooltip => Ru
        ? "Загрузить конфиги из всех источников (или pool.json с сервера), проверить TCP+TLS. ~2-15 мин в зависимости от настроек."
        : "Fetch configs from all sources (or server-side pool.json), test TCP+TLS. ~2-15 min depending on settings.";
    public static string FcRetestTooltip => Ru
        ? "Перепроверить все ранее найденные конфиги (игнорирует skip-recent). ~15 мин для 25k."
        : "Re-test every cached config (ignores skip-recent filter). ~15 min for 25k.";
    public static string FcDeepVerifyTooltip => Ru
        ? "Скачивает свежие VLESS-конфиги из 14 источников и проверяет каждый реальным HTTPS-запросом через временный sing-box. Останавливается когда найдёт N рабочих с пингом ниже порога. ~1-3 минуты."
        : "Fetches fresh VLESS configs from 14 sources and tests each with a real HTTPS request via a temporary sing-box. Stops when N working configs match the ping threshold. ~1-3 minutes.";

    // v2.13.19 — Privacy warning on first Connect from Free Configs
    public static string FcSecWarnTitle => Ru
        ? "Публичный прокси — предупреждение"
        : "Public proxy — privacy warning";
    public static string FcSecWarnHeader => Ru
        ? "Вы подключаетесь к публичному прокси-серверу"
        : "You're connecting to a public proxy operator";
    public static string FcSecWarnBody => Ru
        ? "Оператор этого конфига может видеть метаданные вашего трафика — к каким сайтам вы обращаетесь, когда, как часто. Содержимое HTTPS-сайтов (логины, пароли, сообщения) защищено TLS и недоступно оператору."
        : "The operator of this config can see your traffic metadata — which sites you visit, when, how often. HTTPS content (logins, passwords, messages) is protected by TLS and invisible to the operator.";
    public static string FcSecWarnDontUseList => Ru
        ? "🚫 НЕ используйте для:\n  • банковских приложений / онлайн-банков\n  • входа в почту (Gmail, Яндекс.Почта, Mail.ru)\n  • Госуслуги, налоговая, банки\n  • 2FA / SMS-коды / криптокошельки\n  • любых паролей, которые вы цените"
        : "🚫 DO NOT use for:\n  • banking apps / online banking\n  • email logins (Gmail, Outlook, etc.)\n  • government services, tax sites\n  • 2FA / SMS codes / crypto wallets\n  • any passwords you care about";
    public static string FcSecWarnGoodFor => Ru
        ? "✅ Подходит для: YouTube, новостей, Wikipedia, Discord, Telegram, публичного веба"
        : "✅ Good for: YouTube, news, Wikipedia, Discord, Telegram, public web browsing";
    public static string FcSecWarnProceed => Ru ? "Понял, подключить" : "Understood, connect";
    public static string FcSecWarnCancel  => Ru ? "Отмена" : "Cancel";
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
    public static string AutostartWithWindows => Ru
        ? $"Автозапуск с {OsDisplayName}"
        : $"Autostart with {OsDisplayName}";
    public static string RestartService => Ru ? "Перезапустить службу" : "Restart Service";
    public static string ReinstallService => Ru ? "Переустановить" : "Reinstall";
    public static string InstallingService => Ru ? "Установка службы..." : "Installing service...";
    public static string RemovingService => Ru ? "Удаление службы..." : "Removing service...";

    // ── v2.15.4 UI polish: hint texts + tooltips ──
    public static string ServerListHint => Ru
        ? "Левый клик — выбрать активный. Правый клик — редактировать."
        : "Left click = select active. Right click = edit details.";
    public static string ZapretHostsHint => Ru
        ? "Записи Flowseal в hosts для доступа к YouTube/Discord и т.п."
        : "Flowseal entries in the hosts file for YouTube/Discord/etc.";
    public static string AppsGroupEmpty => Ru
        ? "В этой группе пока нет приложений."
        : "No apps in this group yet.";

    // v2.29.0 — full-tunnel mode banner on the Apps page. Mac feedback
    // 2026-04-29: при RoutingMode=full весь content disabled без объяс-
    // нения; юзер думал что приложение сломано. Заменяем silent disable
    // на banner с объяснением + кнопка "Switch to split tunnel".
    public static string AppsFullTunnelBanner => Ru
        ? "Активен Full-tunnel — выбор приложений игнорируется, весь трафик идёт через VPN."
        : "Full-tunnel mode is active. App selection is ignored — all traffic goes through VPN.";
    public static string AppsFullTunnelBannerAction => Ru
        ? "Переключить на Split tunnel"
        : "Switch to split tunnel";
    public static string SelectCategoryHint => Ru
        ? "← Выберите категорию"
        : "← Select a category";

    // Tooltips — Network tab
    public static string TipBypassRu => Ru
        ? "RU-диапазоны обходят VPN и идут напрямую через ISP"
        : "RU IP ranges bypass the VPN and go direct via ISP";
    public static string TipLeakBlockOnFail => Ru
        ? "Если VPN упал — firewall блокирует выбранные приложения, чтобы трафик не утёк мимо туннеля"
        : "If VPN drops, firewall blocks selected apps so traffic can't leak outside the tunnel";
    public static string TipLeakStrictMode => Ru
        ? "Жёсткий режим — нет fallback на direct при проблемах VPN"
        : "Strict mode — no direct fallback when VPN has issues";
    public static string TipLeakForceIpv4 => Ru
        ? "Отключить IPv6 на маршруте VPN (избегает DNS-утечек через IPv6)"
        : "Disable IPv6 on the VPN route (avoids DNS leaks via IPv6)";
    public static string TipLeakStrictDns => Ru
        ? "Перехватывать весь DNS, включая системный"
        : "Hijack all DNS including system resolvers";
    public static string TipLeakFlushDns => Ru
        ? "Очищать кэш DNS при старте VPN"
        : "Flush DNS cache when VPN starts";
    public static string TipBlockAds => Ru
        ? "Блокировать известные рекламные/трекинг домены на уровне VPN DNS"
        : "Block known ad/tracker domains at the VPN DNS layer";

    // Tooltips — Zapret / DPI
    public static string TipZapretAutoUpdate => Ru
        ? "Каждые 24 часа проверять обновление zapret от Flowseal"
        : "Check for zapret updates from Flowseal every 24 hours";

    // Tooltips — Free Configs controls
    public static string TipFcFastScan => Ru
        ? "Только TCP-проверка (без TLS) — быстрее, но больше ложных «Ok»"
        : "TCP-only probe (skips TLS) — faster but more false 'Ok' hits";
    public static string TipFcSmartRefresh => Ru
        ? "Остановить скан, как только найдётся нужное число «быстрых» конфигов"
        : "Stop scan as soon as enough 'fast' configs are found";
    public static string TipFcSkipRu => Ru
        ? "Пропускать сервера в RU при deep verify"
        : "Skip servers located in RU during deep verify";
    // ── Simple mode (v2.17+) ──

    /// <summary>Header toggle button: Simple → Advanced.</summary>
    public static string SmpToggleToAdvanced => Ru ? "Advanced ▸" : "Advanced ▸";
    /// <summary>Header toggle button: Advanced → Simple.</summary>
    public static string SmpToggleToSimple   => Ru ? "◂ Simple"   : "◂ Simple";
    /// <summary>Tooltip for the header toggle button.</summary>
    public static string SmpToggleTooltip => Ru
        ? "Переключить между упрощённым и полным интерфейсом"
        : "Switch between Simple and Advanced UI";

    // v2.17.0 placeholder copy — replaced by the real skeleton in v2.17.1.
    public static string SmpPlaceholderTitle => Ru
        ? "Упрощённый интерфейс скоро появится"
        : "Simple mode is on the way";
    public static string SmpPlaceholderBody => Ru
        ? "В v2.17 будет одностраничный онбординг: вставил конфиг или ссылку, нажал Start — готово. А пока переключайся в полный интерфейс."
        : "v2.17 will bring a one-page onboarding: paste a config or subscription URL, hit Start, done. Switch to the full Advanced UI for now.";
    public static string SmpPlaceholderSwitchToAdvanced => Ru
        ? "Переключить на Advanced"
        : "Switch to Advanced";

    // v2.17.1 skeleton — section labels + control captions
    public static string SmpInputLabel => Ru ? "Конфиг VPN" : "VPN config";
    public static string SmpInputWatermark => Ru
        ? "vless://... или https://..."
        : "vless://... or https://...";
    public static string SmpInputHint => Ru
        ? "Приму vless://-ссылку или URL подписки (http/https)."
        : "Accepts a vless:// link or a subscription URL (http/https).";
    public static string SmpTunnelModeLabel => Ru ? "Что идёт через VPN" : "Route through VPN";
    public static string SmpSplitOption => Ru
        ? "Выбранные приложения"
        : "Selected apps";
    public static string SmpSplitHint => Ru
        ? "Discord, браузеры, мессенджеры, рабочие"
        : "Discord, browsers, messengers, work apps";
    public static string SmpFullOption => Ru ? "Весь трафик" : "All traffic";
    public static string SmpFullHint => Ru
        ? "Включая игры и банки"
        : "Includes games and banking";
    public static string SmpAdvancedLink => Ru ? "Расширенные настройки ▸" : "Advanced settings ▸";
    public static string SmpAdvancedHint => Ru
        ? "Все вкладки: серверы, подписки, Zapret, Telegram-прокси, Free Configs и пр."
        : "All tabs: servers, subscriptions, Zapret, Telegram proxy, Free Configs and more.";
    public static string SmpChangeConfig => Ru ? "Сменить конфиг или режим ▾" : "Change config or mode ▾";
    public static string SmpConnectedTitle => Ru ? "VPN работает" : "VPN is running";
    public static string SmpDisconnectedTitle => Ru ? "VPN не запущен" : "VPN is off";
    public static string SmpTipSplit => Ru
        ? "Chrome, Firefox, Edge, Brave, Discord, Telegram, Slack, Zoom, VS Code и Cursor идут через VPN. Игры, Steam, банк — мимо."
        : "Chrome, Firefox, Edge, Brave, Discord, Telegram, Slack, Zoom, VS Code and Cursor go through the VPN. Games, Steam, banking — direct.";
    public static string SmpTipFull => Ru
        ? "Весь трафик компьютера идёт через VPN. Включая игры и банки."
        : "All traffic on this computer goes through the VPN — including games and banking.";
    public static string SmpAutostartLabel => Ru
        ? $"Запускать вместе с {OsDisplayName}"
        : $"Start with {OsDisplayName}";
    public static string SmpTipAutostart => Ru
        ? "Установит VPNRouter как службу Windows — VPN поднимется при старте системы, до входа пользователя."
        : "Installs VPNRouter as a Windows Service so the VPN comes up at boot, before you log in.";
    public static string SmpStartVpn => Ru ? "▶  Запустить VPN" : "▶  Start VPN";
    public static string SmpStopVpn => Ru ? "⏹  Остановить VPN" : "⏹  Stop VPN";
    public static string SmpActiveThrough => Ru ? "Через:" : "Through:";

    // ── v2.18.0 compact Simple-mode redesign (Variant A · Calm) ──
    // Status card titles (one word when possible).
    // v2.18.3: "Protected" → "Connected" — RU audience uses VPN for access
    // (bypassing blocks), not for security posture, so "Защищено" implied
    // the wrong mental model.
    public static string SmpStatusProtected    => Ru ? "Подключено"     : "Connected";
    public static string SmpStatusConnecting   => Ru ? "Подключение…"   : "Connecting…";
    public static string SmpStatusNotConnected => Ru ? "Не подключено"  : "Not connected";

    // Status card descriptions. v2.18.3: shortened the "via" prefix so the
    // full line reads "Connected" (title) + "via de-01 · 104.194.156.93"
    // (desc) instead of repeating "Connected" twice.
    public static string SmpStatusConnectedVia      => Ru ? "через" : "via";
    public static string SmpStatusConnectedNoDetails=> Ru ? "Туннель активен." : "Tunnel is active.";
    public static string SmpStatusConnectingHint    => Ru
        ? "Рукопожатие с сервером — пара секунд."
        : "Handshaking with the server — a moment.";
    public static string SmpStatusDisconnectedHint  => Ru
        ? "Трафик идёт напрямую — выбери конфиг и запусти туннель."
        : "Traffic goes straight — pick a config and start the tunnel.";

    // Config row — "Config · Mode" label + value parts ("subscribe · split")
    public static string SmpConfigRowLabel => Ru ? "Конфиг · Режим" : "Config · Mode";
    public static string SmpCfgSubscribe   => Ru ? "подписка"       : "subscribe";
    public static string SmpCfgManual      => Ru ? "вручную"        : "manual";
    public static string SmpCfgCustom      => Ru ? "custom"         : "custom";
    public static string SmpCfgSplit       => Ru ? "сплит"          : "split";
    public static string SmpCfgFull        => Ru ? "полный"         : "full";

    // CTA captions — Connect / Disconnect / Cancel (not destructive; accent-solid, not red)
    public static string SmpCtaConnect    => Ru ? "Подключить"   : "Connect";
    public static string SmpCtaDisconnect => Ru ? "Отключить"    : "Disconnect";
    public static string SmpCtaCancel     => Ru ? "Отменить"     : "Cancel";

    // Advanced card — new wording listing the feature surface
    public static string SmpAdvCardTitle    => Ru ? "Расширенные настройки" : "Advanced settings";
    public static string SmpAdvCardSubtitle => Ru
        ? "Серверы · Подписки · Zapret · Telegram-прокси · Free Configs"
        : "Servers · Subscriptions · Zapret · Telegram proxy · Free configs";

    // Mini-header menu items (⋯ flyout)
    public static string SmpMenuTheme         => Ru ? "Тема"                   : "Theme";
    public static string SmpMenuLanguage      => Ru ? "Язык"                   : "Language";
    public static string SmpMenuOpenLogs      => Ru ? "Открыть логи"           : "Open logs";
    public static string SmpMenuCheckLeaks    => Ru ? "Проверить утечку IP"    : "Check IP leak";
    public static string SmpMenuCheckUpdates  => Ru ? "Проверить обновления"   : "Check for updates";
    public static string SmpMenuSwitchToAdv   => Ru ? "Перейти в Advanced"     : "Switch to Advanced";
    // v2.24.4 troubleshooting items (Level 2/3 self-healing)
    public static string SmpMenuHealthCheck   => Ru ? "Проверить состояние"    : "Run Health Check";
    public static string SmpMenuSafeMode      => Ru ? "Перезапустить в Safe Mode" : "Restart in Safe Mode";
    public static string SmpMenuResetConfig   => Ru ? "Сбросить настройки"     : "Reset config to defaults";
    public static string SmpMenuResetConfirm  => Ru ? "Нажмите ещё раз для сброса" : "Click again to confirm reset";
    public static string TipSmpMenuHealthCheck => Ru
        ? "Запустить диагностику и сохранить отчёт в текстовый файл."
        : "Run diagnostic checks, save results to a text file and open it. Safe to run at any time.";
    public static string TipSmpMenuSafeMode => Ru
        ? "Перезапустить без пользовательских настроек. Force Full tunnel, bundled каталог."
        : "Restart ignoring user config overrides. Forces Full tunnel, uses bundled catalogue only.";
    public static string TipSmpMenuResetConfig => Ru
        ? "Сохранить резервную копию конфига и перезапустить с заводскими настройками. Нажмите дважды для подтверждения."
        : "Backup current config and restart with factory defaults. Click twice to confirm.";

    // v2.25.0-r2 — Autostart is Windows-only (service + registry Run key).
    // On Linux/macOS this whole section is non-functional; replace the four
    // checkboxes with a notice so users don't flip disabled toggles.
    public static string AutostartPlatformNotice => Ru
        ? "Автозапуск пока поддерживается только на Windows. Поддержка Linux (systemd) и macOS (launchd) появится в будущих версиях."
        : "Autostart is currently available on Windows only. Linux (systemd) and macOS (launchd) support is planned for future releases.";

    // v2.25.2 — section labels inside the redesigned ⋯ popover menu.
    // Matches the Claude-Design handoff AdvancedMode.html section 1 layout.
    public static string SmpMenuViewSection           => Ru ? "Вид"                : "View";
    public static string SmpMenuDiagnosticsSection    => Ru ? "Диагностика"        : "Diagnostics";
    public static string SmpMenuTroubleshootingSection => Ru ? "Устранение неполадок" : "Troubleshooting";
    public static string SmpSegLight                  => Ru ? "Светлая"            : "Light";
    public static string SmpSegDark                   => Ru ? "Тёмная"             : "Dark";
    public static string SmpSegRu                     => "RU";
    public static string SmpSegEn                     => "EN";

    // v2.25.11 — shown briefly in the footer while the window rebuild
    // triggered by a language toggle is in flight, so the user can see
    // that their click was received (without this the flyout closes and
    // then the UI freezes for ~200-500 ms with no visible acknowledgement).
    public static string LanguageSwitching            => Ru
        ? "Переключение языка…"
        : "Switching language…";

    // v2.25.0 — "About" dialog (version / build info moved out of header).
    public static string SmpMenuAbout        => Ru ? "О приложении"              : "About";
    public static string TipSmpMenuAbout     => Ru
        ? "Информация о версии, билде и авторе."
        : "Version, build, and author information.";
    public static string AboutTitle          => Ru ? "О приложении"              : "About";
    public static string AboutBrandName      => "Virtual Penguin Network";
    public static string AboutTagline        => Ru
        ? "Процесс-VPN роутер с поддержкой обхода DPI."
        : "Process-based VPN router with DPI bypass support.";
    public static string AboutVersionLabel   => Ru ? "Версия"                    : "Version";
    public static string AboutSingBoxLabel   => Ru ? "sing-box"                  : "sing-box";
    public static string AboutCreatorLabel   => Ru ? "Автор"                     : "Author";
    public static string AboutRepoLabel      => Ru ? "Репозиторий"               : "Repository";
    public static string AboutCloseBtn       => Ru ? "Закрыть"                   : "Close";

    // ── v2.15.5 Localization pass: remaining hardcoded strings ──

    // Tooltips — MainWindow header buttons
    public static string TipOpenLogs => Ru ? "Открыть папку логов" : "Open logs folder";
    public static string TipIpLeak   => Ru ? "ipleak.net — проверка утечки" : "ipleak.net — leak test";

    // Tooltips — Applications page
    public static string TipRemoveCategory => Ru ? "Удалить категорию" : "Remove category";
    public static string TipRemoveApp      => Ru ? "Удалить приложение" : "Remove app";

    // Tooltips — Free Configs cleanup
    public static string TipOpenFreeConfigLogs => Ru
        ? "Открыть папку логов VPNRouter"
        : "Open VPNRouter logs folder";
    public static string TipClearFailed => Ru
        ? "Удалить записи Timeout/Unreachable/TlsFailed/Implausible"
        : "Remove Timeout / Unreachable / TlsFailed / Implausible entries";
    public static string TipKeepVerifiedOnly => Ru
        ? "Оставить только Verified, всё остальное удалить"
        : "Drop everything except Verified entries";
    public static string TipClearAllCache => Ru
        ? "Стереть весь кэш Free Configs"
        : "Wipe the entire Free Configs cache";

    // Tooltips — Servers / Subscriptions testing
    public static string TipTcpTlsPing       => Ru ? "Пинг через TCP + TLS" : "TCP + TLS ping";
    public static string TipTestTcpTls       => Ru ? "Проверить TCP + TLS" : "Test TCP + TLS";
    public static string TipCloseServerDetail => Ru ? "Закрыть" : "Close";
    public static string TipTestAllServers   => Ru
        ? "TCP + TLS проверка всех серверов"
        : "TCP + TLS probe to all servers";
    public static string TipDeepVerifyServers => Ru
        ? "Spawn sing-box + HTTP trace + 5MB download"
        : "Spawn sing-box + HTTP trace + 5MB download";
    public static string TipRefreshSubscription => Ru ? "Обновить подписку" : "Refresh subscription";
    public static string TipRemoveSubscription  => Ru ? "Удалить подписку" : "Remove subscription";

    // Form field labels (Server detail editor)
    public static string LblName      => Ru ? "Имя:"     : "Name:";
    public static string LblServer    => Ru ? "Сервер:"  : "Server:";
    public static string LblPort      => Ru ? "Порт:"    : "Port:";
    public static string LblUuid      => Ru ? "UUID:"    : "UUID:";
    public static string LblPublicKey => Ru ? "Pub Key:" : "Pub Key:";
    public static string LblShortId   => Ru ? "Short ID:" : "Short ID:";

    // Descriptive labels
    public static string LblRoutingMode          => Ru ? "Режим маршрутизации" : "Routing mode";
    public static string LblNoServers            => Ru ? "Серверов нет" : "No servers";
    public static string LblAddSubscriptionHint  => Ru
        ? "Добавьте подписку ниже"
        : "Add a subscription below";

    // Badge
    public static string LblCustomBadge => Ru ? "custom" : "custom";

    // Watermarks
    public static string WmZapretCustomArgs => "--wf-tcp=443 --dpi-desync=…";
    public static string WmVlessUri         => "vless://uuid@server:443?…#name";
    public static string WmTgProxyPort      => "1443";
    public static string WmTgProxySecret    => Ru ? "автоген" : "auto-generated";

    // Status init values
    public static string StatusStopped => Ru ? "Остановлен" : "Stopped";
    public static string StatusRunning => Ru ? "Работает"   : "Running";
}
