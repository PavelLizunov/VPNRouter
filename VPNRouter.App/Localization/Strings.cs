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

    // ── Service (Windows-only) ──
    public static string AutostartWithWindows => Ru ? "Автозапуск с Windows" : "Autostart with Windows";
    public static string RestartService => Ru ? "Перезапустить службу" : "Restart Service";
    public static string ReinstallService => Ru ? "Переустановить" : "Reinstall";
    public static string InstallingService => Ru ? "Установка службы..." : "Installing service...";
    public static string RemovingService => Ru ? "Удаление службы..." : "Removing service...";
}
