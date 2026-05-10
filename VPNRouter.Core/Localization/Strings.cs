namespace VPNRouter.Core.Localization;

/// <summary>
/// Bilingual string provider (EN/RU). Single source of truth for all UI labels
/// shared across <c>VPNRouter.App</c> (desktop Avalonia) and
/// <c>VPNRouter.Android</c> (Android Avalonia). Both projects expose thin
/// wrapper classes (<c>VPNRouter.App.Localization.Strings</c> and
/// <c>VPNRouter.Android.Localization</c>) that delegate every member here so
/// the legacy public surface stays compatible while the actual translations
/// live in one file. Migration log: F-01 in <c>parity-audit/findings.md</c>.
/// </summary>
public static class Strings
{
    public static string Lang { get; set; } = "en";
    internal static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

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
    // v2.30.6-r1 (UX-46 fix): sub-tab + every reference elsewhere in app
    // ("Telegram-прокси" in Simple-mode hints, ServiceMasterSubtitle,
    // AutostartBootSectionSub, etc.) used the user-friendly name. Sub-tab
    // and all VM labels (LblTabTelegram / LblToolTgProxy) read from this.
    public static string TabTgWsProxy => Ru ? "Telegram-прокси" : "Telegram proxy";

    // ── Advanced shell tab labels (AND-ADV-CHROME 2026-05-10) ──
    // Six-tab parity with desktop MainWindow.axaml v2.32.0 ListBoxItem
    // bindings (LblTabManual / LblTabSubscribe / LblTabNetwork / LblTabApps /
    // LblTabTools / LblTabFreeConfigs). Defined as their own keys (rather
    // than reusing the older TabServers / ModeSubscribe / TabSettings /
    // TabApps / TabFreeConfigs strings) so future Android-specific copy
    // tweaks don't bleed into Simple-mode placeholders. AdvSimpleToggle is
    // the "+ Simple" link button that returns to Simple mode.
    public static string TabAdvServers => Ru ? "Серверы" : "Servers";
    public static string TabAdvSubscribe => Ru ? "Подписка" : "Subscribe";
    public static string TabAdvSettings => Ru ? "Настройки" : "Settings";
    public static string TabAdvApplications => Ru ? "Приложения" : "Applications";
    public static string TabAdvTools => Ru ? "Инструменты" : "Tools";
    public static string TabAdvPublic => Ru ? "Публичные" : "Public";
    public static string AdvSimpleToggle => Ru ? "+ Simple" : "+ Simple";

    // ── Config mode ──
    // v2.30.1-r3: was "VLESS Серверы" / "VLESS Servers". Renamed to plain
    // "Серверы" / "Servers" — the sub-tab is no longer VLESS-specific
    // conceptually; future protocol support (Hysteria2, TUIC, SS2022)
    // would also live here, so the VLESS prefix would be misleading.
    public static string VlessServers => Ru ? "Серверы" : "Servers";
    public static string CustomConfigJson => Ru ? "Свой конфиг (JSON)" : "Custom Config (JSON)";
    public static string ModeManual => Ru ? "Ручной" : "Manual";
    public static string ModeSubscribe => Ru ? "Подписка" : "Subscribe";
    public static string ModeCustomConfig => Ru ? "Свой конфиг" : "Custom Config";
    public static string SubscribeMode => Ru ? "Подписка" : "Subscribe";
    // v2.30.5-r1 (UX-34 fix): drop the EN duplicate inside RU placeholder.
    // Pre-r1 was "URL подписки (subscription link)" — same translation
    // shown twice. Now just "URL подписки".
    public static string SubscriptionUrlHint => Ru ? "URL подписки" : "Subscription URL";
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
    // v2.30.6-r1 (UX-41 fix): bilingual button label for the Apps tab
    // "+ Add" button. Pre-r1 was hardcoded EN string in
    // ApplicationsPage.axaml (D1 rule violation).
    public static string AddCustomAppBtn => Ru ? "+ Добавить" : "+ Add";

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
    // v2.30.6-r1 (UX-23/32 fix): tooltip on Ping column header — explains
    // the "—" placeholder users see before any test has been run.
    public static string ColPingTooltip => Ru
        ? "Задержка в мс. «—» означает «не запускалось» — нажмите «Проверить все»."
        : "Latency in ms. \"—\" means not measured — click \"Check all\".";

    // v2.25.4 — Settings/Routing radio-card descriptions (Phase 4 redesign).
    // Each tunnel mode gets a one-line subtitle under the title so the user
    // understands the choice without hovering for a tooltip.
    public static string RoutingDescription => Ru
        ? "Определяет, какой трафик пойдёт через VPN."
        : "Determines which traffic goes through the VPN.";
    // v2.30.3-r1 (UX-9 D1 rule): localize tunnel mode titles. Previous
    // pre-r1 used hardcoded English in both locales which violated the
    // "no English in RU UI" project rule.
    public static string SplitTunnelTitle => Ru ? "Раздельный туннель" : "Split Tunnel";
    public static string SplitTunnelSubtitle => Ru
        ? "Только выбранные приложения. Остальное идёт напрямую."
        : "Only selected apps. Everything else goes direct.";
    public static string FullTunnelTitle => Ru ? "Полный туннель" : "Full Tunnel";
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
    public static string AutostartStatusBoot => Ru
        ? "✓ Через службу Windows (на старте ОС)"
        : "✓ Via Windows Service (at boot)";
    public static string AutostartStatusLoginFallback => Ru
        ? "⚠ Служба не установлена — сработает после входа в приложение"
        : "⚠ Service not installed — will fire after App login";
    public static string AutostartStatusNoBoot => Ru
        ? "⛔ Не сработает без службы Windows"
        : "⛔ Will not fire without the Windows service";
    // v2.31.1-r1 (F-4 / UX-6): inline CTA below the warning hint when the
    // service isn't installed — pre-fix the only way to install was scrolling
    // up to the master toggle, which wasn't obvious.
    public static string BtnInstallServiceInlineCta => Ru
        ? "Установить службу"
        : "Install service";
    public static string TipInstallServiceInlineCta => Ru
        ? "Установит службу VPNRouter и активирует мастер-тумблер автозапуска выше."
        : "Installs the VPNRouter Windows service and turns on the master autostart toggle above.";
    // v2.31.1-r1 (F-6 / UX-33): tooltip explaining the subscription card
    // metadata format `URL · Ns · refreshed-time`. Pre-fix users wondered
    // what "7s · –" meant — the "s" plural marker on server count read as
    // a time unit and the "–" was opaque.
    public static string TipSubscriptionMetadata => Ru
        ? "URL · число серверов в последнем обновлении · когда был последний рефреш. «—» если ни разу не обновлялась."
        : "URL · server count from last refresh · time since last refresh. \"—\" means it has never been refreshed.";
    public static string AutostartLoginSectionTitle => Ru
        ? "При входе пользователя"
        : "At user sign-in";
    // v2.29.0: section A "At system startup (before sign-in)" only renders
    // on Windows (Service-based, no Mac/Linux equivalent yet); section B
    // description should not reference it on Mac/Linux. Pre-r2 EN+RU
    // pointed at "above" assuming Section A was visible, which broke on
    // Mac. Now branches by OS.
    public static string AutostartLoginAppDescription =>
        OperatingSystem.IsWindows()
            ? AutostartLoginAppDescriptionWindows
            : AutostartLoginAppDescriptionUnix;

    private static string AutostartLoginAppDescriptionUnix => Ru
        ? "Запускает приложение VPNRouter в трей после входа в систему. VPN придётся стартануть вручную."
        : "Launches VPNRouter into the tray after you sign in. VPN itself must be started manually.";

    private static string AutostartLoginAppDescriptionWindows => Ru
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
    // Generic subtitle used on Android inline card (no Service-installed/stopped
    // distinction yet — Android lifecycle differs from Windows Service).
    public static string SmpAutostartCardSubtitle => Ru
        ? "Настроить автозапуск VPN при старте Windows"
        : "Configure VPN autostart at Windows boot";

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
    // v2.30.6-r1 (UX-25 fix): drop EN "Custom Config" + "outbound" inside
    // the otherwise-Russian hint. Use natural RU "своим конфигом" +
    // "исходящим" so the sentence reads cleanly in both languages.
    public static string TcpUdpHint => Ru
        ? "VLESS+Reality маршрутизирует TCP. Для UDP (игры, QUIC) используйте свой конфиг с TUIC- или Hysteria2-исходящим."
        : "VLESS+Reality routes TCP only. For UDP (games, QUIC) use a custom config with a TUIC or Hysteria2 outbound.";

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
        // v2.30.5-r1 (UX-19 fix): drop the EN-RU mix "IPv6 leak" inside
        // a Russian sentence. Use natural RU "IPv6-утечек".
        ? "Только IPv4 (защита от IPv6-утечек)"
        : "Force IPv4 only (IPv6 leak protection)";
    public static string FlushDnsLabel => Ru
        ? "Очищать DNS кэш при подключении"
        : "Flush DNS cache on connect";
    // v2.31.6-r18: hint expanded — user feedback iter#7 audit asked
    // why ISP DNS sometimes appears in browserleaks.com / ipleak.net.
    // Default split-tunnel sends non-routed apps' DNS through Cloudflare
    // DoH on the real NIC (not ISP, but leak-tests interpret "Cloudflare
    // DoH client = real IP" as a leak). Strict DNS forces all DNS through
    // the VPN tunnel for that perfect-on-tests outcome.
    public static string StrictDnsLabel => Ru
        ? "Строгий DNS (весь DNS через VPN — рекомендуется при leak-тестах)"
        : "Strict DNS (all DNS via VPN — recommended for leak tests)";

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
    // v2.30.3-r1 (BUG-7 fix): footer text shortened so it fits next to
    // the Apply button at narrow window widths (510 px) without
    // overlapping. Pre-r1 the auto-save hint was 44 chars + 38-char
    // button = visible truncation behind the button background.
    public static string SettingsAutosaved => Ru
        ? "Авто-сохранение"
        : "Auto-saved";
    public static string ApplyNowReloadVpn => Ru
        ? "↻ Применить"
        : "↻ Apply";
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
    // v2.30.7-r4 — F-16 fix: was "Запустить Telegram Proxy" / "Остановить Telegram Proxy"
    // — mixed-case "Telegram Proxy" inside RU sentence (D1 violation) +
    // inconsistent with the sub-tab name "Telegram-прокси" (with hyphen, lowercase).
    // Aligned both labels with the canonical sub-tab name.
    public static string TgProxyStart => Ru ? "Запустить Telegram-прокси" : "Start Telegram proxy";
    public static string TgProxyStop  => Ru ? "Остановить Telegram-прокси" : "Stop Telegram proxy";
    public static string TgProxyOpenInTelegram => Ru ? "Открыть в Telegram" : "Open in Telegram";

    // v2.31.6-r5 (TG-2) — unified footer action label per user feedback
    // 2026-05-03 night: «запуск прокси и открыть телеграм нужно объединить,
    // сейчас они очень далеко». Footer becomes the primary CTA on first run
    // (download → start → open-in-Telegram in one click) so the user no
    // longer plays "click body button + click footer button" two-step.
    // The body button demotes to a secondary "re-pair" fallback for
    // sessions where Telegram client lost the proxy entry.
    public static string TgProxyStartAndOpen => Ru
        ? "Запустить и открыть Telegram"
        : "Start & open Telegram";
    public static string TgProxySetupOnce => Ru
        // v2.30.5-r1 (UX-55 fix): EN "Start/Stop" inside RU sentence.
        ? "Нажмите 'Открыть в Telegram' один раз для настройки прокси. После этого просто Запуск/Остановка."
        : "Click 'Open in Telegram' once to set up the proxy. After that just Start/Stop.";

    // v2.31.6-r9 — purged 5 unused TgProxySetup* + TgProxyClientAutoHint
    // + TgProxyAdvanced strings that were added in v2.31.6-r1's two-state
    // setup-cascade but dropped in r3 (full redo per design handoff cell 6).
    // The XAML referenced them via `L_TgProxySetupCta` etc. only in r1/r2;
    // r3+ pages no longer bind them. Iter#4 audit confirmed zero XAML hits.
    // TgProxyReopenInTelegram below is the only string from that batch
    // still in use (TelegramPage body button label).
    public static string TgProxyReopenInTelegram => Ru
        ? "Открыть в Telegram повторно"
        : "Reopen in Telegram";

    // v2.31.6-r9 — A11y: full-sentence announcements for short
    // button labels («Copy» / «New») that screen readers can't
    // disambiguate without context. Used in
    // <c>AutomationProperties.Name</c> bindings on the secret-row
    // buttons in TelegramPage. Visible button text stays short
    // («Copy» / «New») per the design — only Narrator/VoiceOver
    // hears the longer phrase.
    public static string TgProxyCopySecretA11y => Ru
        ? "Скопировать MTProto secret в буфер обмена"
        : "Copy MTProto secret to clipboard";
    public static string TgProxyRegenerateSecretA11y => Ru
        ? "Сгенерировать новый MTProto secret"
        : "Generate new MTProto secret";

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
    // v2.31.0-r4 (F-15): tooltip on the service.bat menu button.
    public static string TipOpenServiceMenu => Ru
        ? "Открыть служебное меню Zapret (winws.exe service.bat) — установка/удаление службы и переключение стратегии."
        : "Open the Zapret service menu (winws.exe service.bat) — install/remove service and switch strategy.";

    // ── Zapret sections (master-detail) ──
    public static string ZapretSecStatus       => Ru ? "Статус" : "Status";
    public static string ZapretSecStrategy     => Ru ? "Стратегия" : "Strategy";
    public static string ZapretSecHosts        => "Hosts";
    public static string ZapretSecFilters      => Ru ? "Фильтры" : "Filters";
    public static string ZapretSecUpdates      => Ru ? "Обновления" : "Updates";
    public static string ZapretSecDiagnostics  => Ru ? "Диагностика" : "Diagnostics";
    public static string ZapretSecAdvanced     => Ru ? "Дополнительно" : "Advanced";

    // v2.31.6-r7 — section descriptions for the Zapret master-detail.
    // Iter#3 audit (2026-05-04) flagged the page as «возможно слишком
    // сложная» — 5 unlabelled sections looked intimidating to first-run
    // users who only wanted to click Start DPI Bypass. Adding a 1-line
    // hint under each section header so first-time visitors understand
    // each section's purpose at a glance and can ignore power-user
    // sections without feeling they're missing something. Status keeps
    // its existing LblDpiDescription which already serves this role.
    public static string ZapretSecStrategyDesc => Ru
        ? "Технология обхода DPI. Если одна не работает — попробуйте другую."
        : "DPI bypass technique. If one doesn't work — try another.";
    public static string ZapretSecHostsDesc => Ru
        ? "Правки файла hosts: Discord voice + Flowseal-список."
        : "Hosts-file overrides: Discord voice + Flowseal list.";
    public static string ZapretSecFiltersDesc => Ru
        ? "Какой трафик пропускать через обход DPI."
        : "Which traffic to route through DPI bypass.";
    public static string ZapretSecAdvancedDesc => Ru
        ? "Диагностика и управление службой. Большинству не нужно."
        : "Diagnostics and service controls. Not needed for most users.";

    // Filters
    public static string GameFilter => Ru ? "Игровой фильтр (диапазон 1024-65535)" : "Game filter (port range 1024-65535)";
    public static string GameFilterOff => Ru ? "Выкл" : "Off";
    public static string GameFilterAll => Ru ? "TCP + UDP" : "TCP + UDP";
    public static string GameFilterTcp => "TCP";
    public static string GameFilterUdp => "UDP";

    public static string IpSetFilter => Ru ? "IPSet фильтр" : "IPSet filter";
    // v2.30.7-r4 — F-13 fix: "Any" / "Loaded" were left as English in the
    // RU dropdown, mixing inside an otherwise-Russian sub-section
    // (D1 violation). Localized while keeping the parenthetical
    // explainers intact.
    public static string IpSetAny => Ru ? "Все (весь трафик)" : "Any (all traffic)";
    public static string IpSetLoaded => Ru ? "Из файла (список загружен)" : "Loaded (from list file)";
    // v2.30.4-r1 (UX-51 fix): align off-state copy with GameFilterOff
    // ("Выкл" / "Off"). Pre-r1 had "None (отключено)" inconsistent with
    // the dropdown sibling.
    public static string IpSetNone => Ru ? "Выкл" : "Off";

    // Updates
    public static string UpdateIpSet => Ru ? "Обновить IPSet список" : "Update IPSet list";
    // v2.30.4-r1 (UX-52 fix): align case with the sub-tab name "Zapret"
    // (capitalized). Pre-r1 had "zapret" lowercase here while everywhere
    // else it's "Zapret" — inconsistent.
    public static string AutoUpdateCheckLabel => Ru
        ? "Авто-проверка обновлений Zapret"
        : "Auto-check Zapret updates";

    // Advanced
    public static string RunTestsLabel => Ru ? "Запустить тесты сети" : "Run network tests";
    public static string RemoveServiceLabel => Ru ? "Удалить службу Zapret" : "Remove Zapret service";

    public static string ApplyChanges => Ru ? "↻  Применить изменения" : "↻  Apply changes";
    public static string ChangesApplied => Ru ? "Изменения применены" : "Changes applied";
    public static string ApplyFailed => Ru ? "Не удалось применить" : "Apply failed";

    public static string AddCategory => Ru ? "+ Новая категория" : "+ New category";
    public static string EnableWholeGroup => Ru ? "Включить всю группу" : "Enable whole group";
    public static string CategoryNamePrompt => Ru ? "Имя категории:" : "Category name:";
    public static string AddAppHint => Ru ? "имя процесса (например Discord)" : "process name (e.g. Discord)";

    // ── App group display names ──
    // v2.30.4-r1 (UX-37/38 fix): all profile keys now have user-facing
    // display names. Pre-r1 only 5 of 9 categories were translated;
    // others leaked snake_case JSON keys ("AI_Tools", "Privacy_Shell",
    // "Messengers") into the UI.
    public static string GroupDisplayName(string internalName) => internalName switch
    {
        "Discord_Privacy" => "Discord",
        "Messengers"      => Ru ? "Мессенджеры" : "Messengers",
        "AI_Tools"        => Ru ? "AI-инструменты" : "AI tools",
        "Browsers"        => Ru ? "Браузеры" : "Browsers",
        "Work_Suite"      => Ru ? "Работа" : "Work",
        "Streaming"       => Ru ? "Стриминг" : "Streaming",
        "Gaming"          => Ru ? "Игры" : "Gaming",
        "Virtualization"  => Ru ? "Виртуализация" : "Virtualization",
        "Privacy_Shell"   => Ru ? "Приватность" : "Privacy",
        "Terminal"        => Ru ? "Терминал" : "Terminal",
        "Custom Apps"     => Ru ? "Свои" : "Custom",
        _                 => internalName
    };

    public static string SectionRouting => Ru ? "Маршрутизация" : "Routing";
    public static string SectionRules => Ru ? "Правила" : "Rules";
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
    // v2.30.7-r2 — "Свободные" / "Free" was deemed unclear (user
    // feedback). Renamed to "Публичные" / "Public" — describes the
    // source (public free pools from 14 sources, server-side
    // pre-aggregated via GH Actions) without sounding like
    // "free trial" or "no-cost product". Fits narrow tab strip.
    public static string TabFreeConfigs => Ru ? "Публичные" : "Public";
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
    // v2.30.4-r1 (UX-66 fix): replaced literal "N" placeholder with
    // copy that doesn't pretend to know an exact count. Pre-r1 said
    // "Найдёт N рабочих" leaking the parameter symbol into the UI.
    public static string FcDeepHint        => Ru
        ? "Скачает публичные VLESS-конфиги и проверит каждый реальной попыткой подключения. Остановится когда наберётся достаточно рабочих с пингом ниже порога."
        : "Downloads public VLESS configs and tries each one with a real connection. Stops once enough working ones meet your ping threshold.";
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
    // v2.31.0-r4 (F-24 / UX-63): tooltip explaining "—" rows.
    public static string FcSpeedColumnTooltip => Ru
        ? "Скорость измеряется во время Глубокой проверки. «—» означает, что замер не запускался — нажмите ↻ или «Глубоко проверить» чтобы получить значение."
        : "Speed is measured during Deep verify. \"—\" means it wasn't measured — click ↻ or 'Deep verify' to get a number.";
    // v2.31.0-r4 (F-26): inline confirmation toast after RunHealthCheck.
    public static string HealthCheckSavedToast => Ru
        ? "Отчёт сохранён и открыт в Блокноте"
        : "Report saved and opened in Notepad";
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
        ? "Скачивает свежие VLESS-конфиги из 14 источников и проверяет каждый реальным HTTPS-запросом через временный sing-box. Останавливается когда наберётся достаточно рабочих с пингом ниже порога. ~1-3 минуты."
        : "Fetches fresh VLESS configs from 14 sources and tests each with a real HTTPS request via a temporary sing-box. Stops once enough working configs match the ping threshold. ~1-3 minutes.";

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
    // v2.30.3-r1: tunnel name localized to match SplitTunnelTitle/
    // FullTunnelTitle (Раздельный/Полный туннель).
    public static string AppsFullTunnelBanner => Ru
        ? "Активен Полный туннель — выбор приложений игнорируется, весь трафик идёт через VPN."
        : "Full-tunnel mode is active. App selection is ignored — all traffic goes through VPN.";
    public static string AppsFullTunnelBannerAction => Ru
        ? "Переключить на Раздельный туннель"
        : "Switch to split tunnel";

    // v2.29.0 — Custom direct rules (Network → Routing → expander).
    // Mac tester request 2026-04-29: «хотелось бы расширенную настройку
    // конфига, у меня есть кейсы с wireguard где мне хотелось бы самому
    // прописывать direct правила».
    // v2.30.0 — full custom rules engine (direct/proxy/block actions).
    // Replaces v2.29.0-r4 CustomDirectRules* strings.
    public static string CustomRulesTitle => Ru
        ? "Свои правила маршрутизации (расширенно)"
        : "Custom routing rules (advanced)";
    public static string CustomRulesDescription => Ru
        ? "Свои правила для определённых доменов / IP / портов / процессов. Действия: direct (мимо VPN), proxy (через VPN), block (блокировать). ⓘ Тумблеры «Российский трафик через реальный IP» и «Блокировать рекламу» имеют ВЫСШИЙ приоритет — если они включены, их правила сработают раньше ваших. Локальные сети (10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12) уже идут direct автоматически."
        : "Custom rules for specific domains / IPs / ports / processes. Actions: direct (bypass VPN), proxy (force through VPN), block (drop). ⓘ The toggles «Russian traffic via real IP» and «Block ads» have HIGHEST priority — if enabled, their rules fire before yours. Private network ranges (10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12) already go direct automatically.";
    // v2.30.3-r1 (BUG-15 fix): broke long lines so the example template
    // is readable at default ~510 px window width without horizontal
    // scrolling. The pre-r1 placeholder had a 132-char Types comment
    // line that was always cut off — users couldn't see the type list.
    // Now wrapped across 3 short lines.
    public static string CustomRulesPlaceholder => Ru
        ? "# Одно правило на строку.\n# Формат: <action> <type> <value> [# комментарий]\n# Actions: direct / proxy / block\n# Types: domain · domain_suffix · domain_keyword\n#        ip_cidr · port · port_range · network\n#        process_name · geosite · geoip\n# Несколько значений через запятую.\n# Отключить — '!' в начале строки.\n\ndirect ip_cidr 10.0.0.0/8, 192.168.0.0/16  # LAN\nproxy domain_suffix .corp.example          # через VPN\nblock geosite ads                          # реклама\n!block port 53                             # отключено"
        : "# One rule per line.\n# Format: <action> <type> <value> [# comment]\n# Actions: direct / proxy / block\n# Types: domain · domain_suffix · domain_keyword\n#        ip_cidr · port · port_range · network\n#        process_name · geosite · geoip\n# Multi-value: comma-separated.\n# Disable: prefix '!'.\n\ndirect ip_cidr 10.0.0.0/8, 192.168.0.0/16  # LAN\nproxy domain_suffix .corp.example          # via VPN\nblock geosite ads                          # ads\n!block port 53                             # disabled";
    public static string CustomRulesErrorHeader => Ru
        ? "Ошибки парсинга:"
        : "Parse errors:";
    public static string CustomRulesConflictHeader => Ru
        ? "Предупреждения о конфликтах:"
        : "Conflict warnings:";

    // v2.30.0-r2: structured row-table editor strings (Network → Rules section).
    public static string CustomRulesPageDescription => Ru
        ? "Свои правила маршрутизации для определённых доменов / IP / портов / процессов. ⓘ Тумблеры «Российский трафик через реальный IP» и «Блокировать рекламу» имеют ВЫСШИЙ приоритет — если включены, их правила сработают раньше ваших. Локальные сети (10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12) уже идут direct автоматически."
        : "Custom routing rules for specific domains / IPs / ports / processes. ⓘ The toggles «Russian traffic via real IP» and «Block ads» have HIGHEST priority — if enabled, their rules fire before yours. Private network ranges (10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12) already go direct automatically.";

    public static string CustomRulesEmpty => Ru
        ? "Нет правил. Добавьте через форму ниже или раскройте «Расширенный режим» для редактирования через текст."
        : "No rules yet. Add via the form below or expand «Advanced mode» for text editing.";

    public static string CustomRulesAddTitle => Ru ? "Добавить правило:" : "Add rule:";
    public static string CustomRulesAddBtn => Ru ? "+ Добавить" : "+ Add";
    public static string CustomRulesActionLabel => Ru ? "Действие" : "Action";
    public static string CustomRulesTypeLabel => Ru ? "Тип" : "Type";
    public static string CustomRulesValueLabel => Ru ? "Значение" : "Value";
    public static string CustomRulesCommentLabel => Ru ? "Комментарий" : "Comment";

    public static string CustomRulesActionDirect => "direct";
    public static string CustomRulesActionProxy => "proxy";
    public static string CustomRulesActionBlock => "block";

    public static string CustomRulesValuePlaceholder => Ru
        ? "напр. 10.0.0.0/8 или .corp.example"
        : "e.g. 10.0.0.0/8 or .corp.example";

    public static string CustomRulesAdvancedMode => Ru
        ? "Расширенный режим (текстовый формат)"
        : "Advanced mode (text format)";

    public static string CustomRulesValidationFailed => Ru
        ? "Ошибка валидации:"
        : "Validation failed:";

    public static string CustomRulesActionDirectLabel => Ru
        ? "direct (мимо VPN)"
        : "direct (bypass VPN)";
    public static string CustomRulesActionProxyLabel => Ru
        ? "proxy (через VPN)"
        : "proxy (force through VPN)";
    public static string CustomRulesActionBlockLabel => Ru
        ? "block (заблокировать)"
        : "block (drop)";

    public static string CustomRulesDelete => Ru ? "Удалить" : "Delete";
    public static string CustomRulesEdit => Ru ? "Редактировать" : "Edit";
    public static string CustomRulesMoveUp => Ru ? "Выше" : "Move up";
    public static string CustomRulesMoveDown => Ru ? "Ниже" : "Move down";

    // v2.30.0-r3 — Import/Export 3 formats.
    public static string CustomRulesImport => Ru ? "Импорт..." : "Import...";
    public static string CustomRulesExport => Ru ? "Экспорт..." : "Export...";
    public static string CustomRulesImportTooltip => Ru
        ? "Импорт из CSV / JSON / sing-box JSON (NekoBox, Hiddify)"
        : "Import from CSV / JSON / sing-box JSON (NekoBox, Hiddify)";
    public static string CustomRulesExportTooltip => Ru
        ? "Экспорт в CSV / JSON / sing-box JSON"
        : "Export to CSV / JSON / sing-box JSON";

    // v2.30.0-r4 — search filter + bulk actions for large rule lists.
    public static string CustomRulesSearchPlaceholder => Ru
        ? "Поиск по action / type / value / комментарию..."
        : "Search across action / type / value / comment...";
    public static string CustomRulesClearAll => Ru ? "Очистить всё" : "Clear all";
    public static string CustomRulesEnableAll => Ru ? "Вкл. все" : "Enable all";
    public static string CustomRulesDisableAll => Ru ? "Выкл. все" : "Disable all";
    public static string CustomRulesClearAllTooltip => Ru
        ? "Удалить все правила (нажмите дважды для подтверждения)"
        : "Delete all rules (click twice to confirm)";
    public static string CustomRulesEnableAllTooltip => Ru
        ? "Включить все правила"
        : "Enable all rules";
    public static string CustomRulesDisableAllTooltip => Ru
        ? "Выключить все правила (без удаления)"
        : "Disable all rules (without deleting)";
    public static string CustomRulesNoMatchHint => Ru
        ? "По запросу ничего не найдено."
        : "No rules match the search.";

    public static string CustomRulesExistingHeader => Ru
        ? "Существующие правила"
        : "Existing rules";

    // ── v2.30.0-r7 — Cards / Edit view-mode toggle (RulesExplorations.html) ──
    // Power-user editable text mode replaces the old "Advanced" expander.
    // Cards view is the structured row-table editor (default, friendly).
    // Edit view is a full textarea with line-numbered gutter, per-line
    // errors, and explicit Apply / Revert (no auto-save while typing).
    public static string RulesViewCards => Ru ? "Карточки" : "Cards";
    public static string RulesViewRead => Ru ? "Список" : "Read";
    public static string RulesViewEdit => Ru ? "Текст" : "Edit";
    public static string RulesViewCardsTooltip => Ru
        ? "Структурированный список правил с цветными чипами, тумблерами и инлайн-удалением"
        : "Structured rule list with colored chips, toggles, and inline delete";
    public static string RulesViewReadTooltip => Ru
        ? "Сгруппированный read-only вид (моноспейс): direct / proxy / block по секциям"
        : "Grouped read-only view (monospace): direct / proxy / block sections";
    public static string RulesViewEditTooltip => Ru
        ? "Полностью редактируемый текстовый режим: одно правило на строку"
        : "Fully editable text mode: one rule per line";

    public static string RulesEditorApply => Ru ? "Применить" : "Apply";
    public static string RulesEditorRevert => Ru ? "Откатить" : "Revert";
    public static string RulesEditorDirty => Ru
        ? "● несохранённые изменения"
        : "● unsaved changes";
    // v2.30.3-r1 (UX-16 fix): the parser uses '!' as the disable
    // prefix (CustomRulesParser line 85: StartsWith("!")), not "# off"
    // which was a misleading documentation. Brought hint in line with
    // the actual parser + the example placeholder ('!block port 53').
    public static string RulesEditorFormatHint => Ru
        ? "Формат: action  type  value  # comment.   Выключить правило: '!' в начале строки.   Пустые строки игнорируются."
        : "Format: action  type  value  # comment.   Disable a rule: '!' at start of line.   Empty lines are ignored.";

    // Help banner — replaces the dense single-paragraph description.
    // Bullet points highlight the toggle precedence + LAN auto-direct +
    // order-doesn't-matter facts. Dismissable via X button.
    // v2.30.0-r11 — Filter chips + bulk-actions menu.
    public static string RulesFilterAll => Ru ? "Все" : "All";
    public static string RulesBulkActions => Ru ? "Массовые действия" : "Bulk actions";

    // v2.30.0-r14 — Sort-by-type bulk action (per design `.bulk-pop`).
    public static string RulesSortByType => Ru ? "Сортировать по типу" : "Sort by type";

    // v2.30.0-r18 — Clear All inline confirm bar (replaces broken
    // two-click-in-popover pattern). Also adds a generic Cancel string.
    public static string RulesClearAllHint => Ru
        ? "Это действие нельзя отменить."
        : "This action cannot be undone.";
    public static string RulesClearAllConfirm => Ru ? "Удалить" : "Delete";
    public static string CommonCancel => Ru ? "Отмена" : "Cancel";

    // v2.30.0-r17 — Custom-rules-priority CheckBox label + tooltip.
    public static string RulesCustomAboveToggles => Ru
        ? "Свои правила важнее тумблеров"
        : "Custom rules above toggles";
    public static string RulesCustomAboveTogglesHint => Ru
        ? "По умолчанию «Российский трафик» и «Блокировать рекламу» срабатывают раньше ваших правил. Включите чтобы ваши правила побеждали."
        : "By default «Russian traffic» and «Block ads» fire before your rules. Enable to make your rules win.";

    // v2.30.0-r14 — Add-form mini-labels (uppercase, per design `.field .ftitle`).
    // Localized so the UI is single-language end-to-end (matches user's
    // "не использовать микс" rule).
    public static string RulesAddLabelAction  => Ru ? "ДЕЙСТВИЕ"    : "ACTION";
    public static string RulesAddLabelType    => Ru ? "ТИП"         : "TYPE";
    public static string RulesAddLabelValue   => Ru ? "ЗНАЧЕНИЕ"    : "VALUE";
    public static string RulesAddLabelComment => Ru ? "КОММЕНТАРИЙ" : "COMMENT";
    public static string RulesAddLabelOpt     => Ru ? "(опц.)"      : "(opt.)";

    // v2.30.0-r12 — Help banner restructured per design RulesPage.html
    // `.help` block: bold heading + 3 bullets with <code>-styled values
    // for technical terms (CIDR ranges, "direct" action). Each bullet is
    // split into prefix / emphasized-name / mid / emphasized-name / suffix
    // pieces so the XAML can apply per-Run styling (FontWeight=SemiBold for
    // names, FontFamily=mono for code values) without a markup parser.
    public static string RulesHelpHeader => Ru
        ? "Как работают правила."
        : "How rules work.";

    // Bullet 1: «toggle1» and «toggle2» fire BEFORE your rules.
    public static string RulesHelpB1Pre  => Ru ? "Тумблеры " : "The toggles ";
    public static string RulesHelpB1T1   => Ru
        ? "«Российский трафик через реальный IP»"
        : "«Russian traffic via real IP»";
    public static string RulesHelpB1Mid  => Ru ? " и " : " and ";
    public static string RulesHelpB1T2   => Ru ? "«Блокировать рекламу»" : "«Block ads»";
    public static string RulesHelpB1Suf  => Ru
        ? " срабатывают раньше ваших правил."
        : " fire before your rules.";

    // Bullet 2: Private nets (10.0.0.0/8, ...) already go direct automatically.
    public static string RulesHelpB2Pre  => Ru ? "Локальные сети (" : "Private networks (";
    public static string RulesHelpB2Mid  => Ru ? ") уже идут " : ") already go ";
    public static string RulesHelpB2Suf  => Ru ? " автоматически." : " automatically.";

    // Bullet 3: Rule order DOES NOT matter — first match wins per address.
    public static string RulesHelpB3Pre  => Ru ? "Порядок правил " : "Rule order ";
    public static string RulesHelpB3Bold => Ru ? "не важен" : "does not matter";
    public static string RulesHelpB3Suf  => Ru
        ? " — для каждого адреса выбирается первое совпавшее."
        : " — first match wins per address.";

    // Legacy single-string accessor (kept for any cached XAML still binding
    // to the pre-r12 RulesHelpBanner). New XAML uses the structured
    // RulesHelpHeader + RulesHelpB1..B3* set instead.
    public static string RulesHelpBanner => Ru
        ? "Тумблеры «Российский трафик через реальный IP» и «Блокировать рекламу» срабатывают РАНЬШЕ ваших правил.   Локальные сети (10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12) уже идут direct автоматически.   Порядок правил не важен — для каждого адреса выбирается первое совпавшее."
        : "The toggles «Russian traffic via real IP» and «Block ads» fire BEFORE your rules.   Private network ranges (10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12) already go direct automatically.   Rule order does not matter — first match wins per address.";

    // ── Legacy v2.29.0-r4 names (kept for back-compat with cached XAML) ──
    public static string CustomDirectRulesTitle => CustomRulesTitle;
    public static string CustomDirectRulesDescription => CustomRulesDescription;
    public static string CustomDirectRulesPlaceholder => CustomRulesPlaceholder;
    public static string CustomDirectRulesErrorHeader => CustomRulesErrorHeader;
    public static string SelectCategoryHint => Ru
        ? "← Выберите категорию"
        : "← Select a category";

    // ── Phase D (AND-ADV-APPS-CATEGORIES, 2026-05-10) — Applications tab on
    // Android. The tab now mirrors desktop ApplicationsPage with a left
    // category sidebar + right per-category app list. These three keys are
    // surface text the desktop already had implicit equivalents for (the
    // "← Select a category" hint maps to SelectCategoryHint above; these
    // are the picker-mode + bottom-row shells specific to Android's
    // package-based picker).
    public static string AdvAppsCategoryNamePlaceholder => Ru
        ? "Имя категории"
        : "Category name";
    public static string AdvAppsAddCategoryButton => Ru
        ? "+ Новая категория"
        : "+ New category";
    public static string AdvAppsSelectCategoryHint => SelectCategoryHint;
    /// <summary>Android-only catch-all (no built-in profile maps to it).
    /// Shown at the bottom of the sidebar, scope = all installed apps.</summary>
    public static string AdvAppsCategoryCustom => Ru ? "Свои" : "Custom";

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
        ? "Включи если на browserleaks.com / ipleak.net видишь свой ISP DNS или DNS-сервер не из VPN. По умолчанию приложения вне списка маршрутизации идут через Cloudflare DoH на реальном NIC — leak-тесты могут это засчитать как утечку. Strict DNS отправляет ВЕСЬ DNS-трафик через туннель."
        : "Enable if browserleaks.com / ipleak.net shows your ISP DNS or a non-VPN resolver. By default, apps not in the routing list use Cloudflare DoH on the real NIC — leak tests may flag this as a leak. Strict DNS routes ALL DNS through the tunnel.";
    public static string TipLeakFlushDns => Ru
        ? "Очищать кэш DNS при старте VPN"
        : "Flush DNS cache when VPN starts";
    public static string TipBlockAds => Ru
        ? "Блокировать известные рекламные/трекинг домены на уровне VPN DNS"
        : "Block known ad/tracker domains at the VPN DNS layer";

    // Tooltips — Zapret / DPI
    public static string TipZapretAutoUpdate => Ru
        ? "Каждые 24 часа проверять обновление Zapret"
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

    // v2.30.7 — both toggles were hardcoded English in both languages.
    // RU users see "Advanced ▸" / "◂ Simple" inside an otherwise-Russian
    // UI. Now: localized with the full word ("Расширенный/Простой"
    // matches the UI mode names everywhere else).
    /// <summary>Header toggle button: Simple → Advanced.</summary>
    public static string SmpToggleToAdvanced => Ru ? "Расширенный ▸" : "Advanced ▸";
    /// <summary>Header toggle button: Advanced → Simple.</summary>
    public static string SmpToggleToSimple   => Ru ? "◂ Простой"     : "◂ Simple";
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
    // v2.30.6-r1 (UX-3 fix): old subtitle hardcoded specific apps ("Discord,
    // браузеры, мессенджеры, рабочие") which doesn't always match actual
    // selected profiles. Generic descriptor avoids the mismatch and lets
    // the Apps tab list be the source of truth.
    public static string SmpSplitHint => Ru
        ? "По списку выбранных приложений"
        : "Based on your selected apps";
    public static string SmpFullOption => Ru ? "Весь трафик" : "All traffic";
    public static string SmpFullHint => Ru
        ? "Включая игры и банки"
        : "Includes games and banking";
    public static string SmpAdvancedLink => Ru ? "Расширенные настройки ▸" : "Advanced settings ▸";
    // v2.30.7-r4 — F-1 fix: was "Free Configs" in BOTH languages
    // (D1 violation in RU + inconsistent with the new "Публичные"
    // tab name shipped in r2). Aligned with the renamed tab.
    public static string SmpAdvancedHint => Ru
        ? "Все вкладки: серверы, подписки, Zapret, Telegram-прокси, публичные конфиги и пр."
        : "All tabs: servers, subscriptions, Zapret, Telegram proxy, public configs and more.";
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
    public static string SmpSaveButton    => Ru ? "Сохранить"    : "Save";
    public static string SmpRefreshButton => Ru ? "Обновить"     : "Refresh";
    public static string SmpActiveThrough => Ru ? "Через:" : "Through:";

    // v2.32.0 parity audit F-11 (2026-05-09): inline auto-detect feedback
    // shown below the SimplePage VPN-config TextBox. Mirrors Android's
    // sub-tab pattern (Subscription / Server / Custom JSON) — desktop
    // doesn't ship the segmented selector yet (P3 chip), so we surface
    // the detection result as a hint line + gate Save/Refresh/Connect on
    // <see cref="SmpInputKind"/> classification. "Detected" wording chosen
    // to match the Avalonia-shared Tools page status patterns.
    public static string SmpInputDetectedServer        => Ru
        ? "Распознано: ссылка на сервер"
        : "Detected: server link";
    public static string SmpInputDetectedSubscription  => Ru
        ? "Распознано: URL подписки"
        : "Detected: subscription URL";
    // Toast strings for the SimplePage Save/Refresh action row. Empty toast
    // hides the floating bubble — see <c>HasSmpToast</c> binding.
    public static string SmpSavedAsServerToast       => Ru
        ? "Сохранено как сервер"
        : "Saved as server";
    public static string SmpSavedAsSubscriptionToast => Ru
        ? "Сохранено как подписка"
        : "Saved as subscription";
    public static string SmpRefreshDoneToast         => Ru
        ? "Подписка обновлена"
        : "Subscription refreshed";

    // ── Android-only QR scan flow (lucid-pike, 2026-05-09) ───────────────
    // Mobile-only feature: tap the QR button on the Simple page, point the
    // camera at a VLESS / subscription QR, decoded text drops into the VPN
    // config TextBox. Desktop has no camera — these strings live here as
    // single-source-of-truth, but only the Android Localization wrapper
    // exposes them to UI code.
    public static string SmpScanQrButton => Ru ? "Сканировать QR" : "Scan QR";
    public static string SmpQrPermissionDenied => Ru
        ? "Камера недоступна — разреши доступ в настройках Android"
        : "Camera permission denied — grant in Android Settings";
    public static string SmpQrNotRecognized => Ru
        ? "QR не распознан, попробуй ещё раз"
        : "QR not recognized, try again";
    public static string SmpQrScannedToast => Ru ? "QR распознан" : "QR recognized";

    // ── F-12 (parity audit P0, 2026-05-09) — silent ConfigMode flip guard ──
    // SmpToggleConnectAsync surfaces these when the user has typed a non-empty
    // share-link / subscription URL into the input field but has not yet
    // pressed Save. Pre-fix the Connect button silently overwrote settings +
    // flipped ConfigMode (manual·full → subscribe·full) with no feedback —
    // same failure class as v2.28.2 silent leak. Now Connect blocks and asks
    // the user to commit the input via Save first; that explicit step makes
    // the ConfigMode change visible (toast + log line in SaveSettings).
    public static string SmpSaveFirstSubscription => Ru
        ? "Сначала нажми «Сохранить», потом «Подключить» — иначе подписочный URL не запишется в конфиг."
        : "Tap Save first, then Connect — otherwise the subscription URL won't be persisted.";

    public static string SmpSaveFirstServer => Ru
        ? "Сначала нажми «Сохранить», потом «Подключить» — иначе ссылка не запишется в конфиг."
        : "Tap Save first, then Connect — otherwise the share-link won't be persisted.";

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
    // v2.30.7-r4 — F-1 fix: align Simple-card subtitle with the new
    // "Публичные" tab name (was "Free Configs" hardcoded EN in both
    // languages, D1 + inconsistency).
    public static string SmpAdvCardSubtitle => Ru
        ? "Серверы · Подписки · Zapret · Telegram-прокси · Публичные"
        : "Servers · Subscriptions · Zapret · Telegram proxy · Public";

    // Mini-header menu items (⋯ flyout)
    public static string SmpMenuTheme         => Ru ? "Тема"                   : "Theme";
    public static string SmpMenuLanguage      => Ru ? "Язык"                   : "Language";
    public static string SmpMenuOpenLogs      => Ru ? "Открыть логи"           : "Open logs";
    public static string SmpMenuCheckLeaks    => Ru ? "Проверить утечку IP"    : "Check IP leak";
    public static string SmpMenuCheckUpdates  => Ru ? "Проверить обновления"   : "Check for updates";
    public static string SmpMenuSwitchToAdv   => Ru ? "Перейти в Advanced"     : "Switch to Advanced";
    // v2.24.4 troubleshooting items (Level 2/3 self-healing)
    public static string SmpMenuHealthCheck   => Ru ? "Проверить состояние"    : "Run Health Check";
    // v2.30.5-r1 (UX-68 fix): localize "Safe Mode" in Russian.
    public static string SmpMenuSafeMode      => Ru ? "Перезапустить в безопасном режиме" : "Restart in Safe Mode";
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
    public static string TipDeleteServer     => Ru ? "Удалить сервер" : "Delete server";
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
    // v2.30.4-r1 (UX-26 fix): expand placeholder to advertise multi-protocol
    // support shipped in v2.30.1 (vless/hysteria2/tuic/shadowsocks). Pre-r1
    // users had no way to discover from the UI that hy2://, tuic:// or ss://
    // are accepted in the same input.
    public static string WmVlessUri         => "vless:// / hy2:// / tuic:// / ss://...#name";
    public static string WmTgProxyPort      => "1443";
    public static string WmTgProxySecret    => Ru ? "автоген" : "auto-generated";

    // Status init values
    public static string StatusStopped => Ru ? "Остановлен" : "Stopped";
    public static string StatusRunning => Ru ? "Работает"   : "Running";

    // v2.30.4-r1 (SUGGEST-22 fix): manual update check inside Settings →
    // Обновления tab.
    public static string CurrentVersion => Ru ? "Текущая версия" : "Current version";

    // v2.30.5-r1 (UX-29 fix): empty-state hero for the Custom Config
    // (JSON) sub-tab. Pre-r1 was blank + a "Нажмите на конфиг для
    // активации" hint with nothing to click; now explains the feature.
    public static string CustomConfigsEmptyTitle => Ru
        ? "У тебя пока нет своих конфигов"
        : "No custom configs yet";
    public static string CustomConfigsEmptyHint => Ru
        ? "Свой конфиг — это готовый JSON-файл sing-box для нестандартных протоколов (TUIC, Hysteria2, Reality+gRPC и др.). Нажми «Добавить конфиг…» внизу чтобы импортировать."
        : "A custom config is a ready sing-box JSON file for non-standard protocols (TUIC, Hysteria2, Reality+gRPC, etc.). Click «Add config…» below to import.";

    // v2.32.0 — recovery banner shown after SettingsValidator rejected a
    // structurally-valid but semantically-broken config.yaml (typoed
    // config_mode, port out of range, malformed subscription URL, etc.)
    // and the loader rewrote defaults. The backup path comes from
    // SettingsLoader.LastRecoveryNotice and is appended verbatim by the
    // VM, so the localized string is the prefix only.
    public static string SettingsRecoveredFromBadConfig(string backupPath) => Ru
        ? string.IsNullOrEmpty(backupPath)
            ? "Config был повреждён, восстановлен default."
            : $"Config был повреждён, восстановлен default. Backup: {backupPath}"
        : string.IsNullOrEmpty(backupPath)
            ? "Config was invalid; defaults restored."
            : $"Config was invalid; defaults restored. Backup: {backupPath}";

    // ════════════════════════════════════════════════════════════════════
    // Android-only keys merged in from VPNRouter.Android/Localization.cs
    // (parity audit F-01, 2026-05-09). The 253 keys below have no desktop
    // counterpart yet — they cover Android-specific UI surfaces (kebab
    // menu sections, server list overlay, profiles overlay, reliability
    // section, custom config segment, AndroidUpdater flow, etc.). When
    // a desktop screen needs the same affordance it can bind directly to
    // these keys without code duplication.
    // ════════════════════════════════════════════════════════════════════

    public static string Title => "VPNRouter v3.0";

    public static string Subtitle => Ru
        ? "Android · вставь VLESS-URI или подписочный URL и подключись"
        : "Android · paste a VLESS URI or subscription URL and connect";

    public static string LangToggleLabel => Ru ? "EN" : "RU";

    // v3.0 Phase 7.3 (2026-05-04) — segmented control labels for the
    // kebab menu's "Вид" / "Appearance" section, mirroring desktop's
    // SmpSegLight / SmpSegDark / SmpSegRu / SmpSegEn (see
    // VPNRouter.App/Localization/Strings.cs:1280-1283). RU/EN labels
    // for the language segments stay locale-independent (the segment
    // shows what the user is switching TO, not the current language).
    public static string MenuSegLight => Ru ? "Светлая" : "Light";

    public static string MenuSegDark  => Ru ? "Тёмная"  : "Dark";

    public static string MenuSegRu    => "RU";

    public static string MenuSegEn    => "EN";

    public static string BrandTitle => Ru ? "Virtual Penguin Network" : "Virtual Penguin Network";

    public static string MenuLanguageLabel => Ru ? "Язык: Русский" : "Language: English";

    public static string MenuThemeLabel => Ru ? "Тема: переключить" : "Theme: toggle";

    public static string StatusConnected => Ru ? "Подключено" : "Connected";

    public static string StatusDisconnected => Ru ? "Отключено" : "Disconnected";

    public static string ButtonConnect => Ru ? "Подключить" : "Connect";

    public static string ButtonDisconnect => Ru ? "Отключить" : "Disconnect";

    public static string ButtonConnecting => Ru ? "Подключение…" : "Connecting…";

    public static string SimpleStatusTitleOn => Ru ? "Подключено" : "Connected";

    public static string SimpleStatusTitleOff => Ru ? "Не подключено" : "Not connected";

    public static string SimpleStatusDescOn => Ru
        ? "Трафик идёт через VPN-туннель."
        : "Traffic is routed through the VPN tunnel.";

    public static string SimpleStatusDescOff => Ru
        ? "Трафик идёт напрямую — выбери конфиг и запусти туннель."
        : "Traffic goes straight — pick a config and start the tunnel.";

    /// <summary>Title format when connected — args: {0}=uptime ("0:23" or "1:23:45").</summary>
    public static string SimpleStatusTitleOnWithUptime => Ru
        ? "Подключено · {0}"
        : "Connected · {0}";

    /// <summary>Healthy log probe — args: {0}=seconds since last successful probe.</summary>
    public static string DiagHealthCheckOk => Ru
        ? "✓ Проверка {0} с назад"
        : "✓ Last check {0}s ago";

    /// <summary>Stale log probe — sing-box hasn't written for &gt;60 s.</summary>
    public static string DiagHealthCheckStale => Ru
        ? "⚠ Проверка не отвечает"
        : "⚠ Stale check";

    /// <summary>Pending first probe — shown for the first 30 s after connect.</summary>
    public static string DiagHealthCheckPending => Ru
        ? "· Ожидаю первую проверку…"
        : "· Awaiting first check…";

    /// <summary>Error one-liner — args: {0}=raw error message from EXTRA_ERROR_MESSAGE.</summary>
    public static string DiagErrorOneLiner => Ru
        ? "Ошибка: {0}"
        : "Error: {0}";

    public static string SmpSourceManual => Ru ? "вручную" : "manual";

    public static string SmpSourceSubscription => Ru ? "подписка" : "subscription";

    public static string SimpleConfigSummary => Ru ? "вручную · полный" : "manual · full";

    public static string ServerHeader => Ru ? "Сервер" : "Server";

    public static string ServerInputWatermark => Ru
        ? "vless://… или https://…/sub"
        : "vless://… or https://…/sub";

    public static string ButtonSave => Ru ? "Сохранить" : "Save";

    public static string ButtonRefresh => Ru ? "Обновить" : "Refresh";

    public static string ServerInputHintInitial => Ru
        ? "Введи vless://-ссылку или подписочный URL и нажми «Сохранить». Для подписки потом «Обновить»."
        : "Paste a vless:// share-link or a subscription URL, then tap Save. For a subscription URL, tap Refresh after.";

    public static string SaveStatusCleared => Ru
        ? "Сервер очищен. Будет использован встроенный placeholder."
        : "Server cleared. The built-in placeholder will be used.";

    public static string SaveStatusUriBadHost => Ru
        ? "URI распарсен, но не хватает host или порта. Проверь."
        : "Parsed but missing host or port — please double-check.";

    public static string SaveStatusUriOk => Ru
        ? "Сохранено. Сервер: {0}:{1}. Жми «Подключить»."
        : "Saved. Server: {0}:{1}. Tap Connect.";

    public static string SaveStatusUriInvalid => Ru
        ? "Невалидный VLESS URI: {0}"
        : "Invalid VLESS URI: {0}";

    public static string SaveStatusSubStored => Ru
        ? "URL подписки сохранён. Жми «Обновить» чтобы скачать список серверов."
        : "Subscription URL saved. Tap Refresh to fetch the server list.";

    public static string SaveStatusUnknown => Ru
        ? "Не похоже ни на vless://, ни на http(s):// — проверь ввод."
        : "Doesn't look like vless:// or http(s):// — check the input.";

    public static string RefreshNeedsUrl => Ru
        ? "Сначала сохрани подписочный URL (https://…)."
        : "Save a subscription URL first (https://…).";

    public static string RefreshFetching => Ru ? "Скачиваю…" : "Fetching…";

    public static string RefreshOk => Ru
        ? "Получено серверов: {0}. Выбери из списка ниже."
        : "Fetched {0} servers. Pick one below.";

    public static string RefreshFailed => Ru
        ? "Не удалось скачать: {0}"
        : "Refresh failed: {0}";

    public static string AvailableServers => Ru ? "Доступные серверы" : "Available servers";

    public static string ServerSelected => Ru
        ? "Выбран: {0} ({1}:{2})"
        : "Selected: {0} ({1}:{2})";

    public static string QrComingSoon => Ru
        ? "QR-сканер появится в следующем апдейте — пока вставляй URI вручную."
        : "QR scanner is coming in the next update — paste the URI manually for now.";

    public static string HintTunnel => Ru
        ? "Состояние туннеля повторяет иконку 🔑 в строке состояния."
        : "Tunnel state mirrors the system VPN-key icon in the status bar.";

    // Section headers
    public static string MenuSectionView => Ru ? "Вид" : "Appearance";

    public static string MenuSectionDiagnostics => Ru ? "Диагностика" : "Diagnostics";

    public static string MenuSectionTroubleshooting => Ru ? "Устранение неполадок" : "Troubleshooting";

    public static string MenuSectionAbout => Ru ? "О приложении" : "About";

    // Diagnostics items
    public static string MenuItemOpenLogs => Ru ? "Открыть лог" : "Open log";

    public static string MenuItemCopyLogPath => Ru ? "Скопировать путь к логу" : "Copy log path";

    public static string MenuItemViewCrashLog => Ru ? "Журнал сбоев" : "View crash log";

    public static string CrashLogEmpty => Ru
        ? "Сбоев нет — это хорошо."
        : "No crashes recorded — that's good.";

    public static string MenuItemUpdateCheck => Ru ? "Проверить обновления" : "Check for updates";

    public static string MenuItemUpdateComingSoon => Ru
        ? "Авто-обновление появится в следующем апдейте."
        : "Auto-update is coming in the next release.";

    public static string UpdateCheckChecking => Ru ? "Проверяю…" : "Checking…";

    public static string UpdateCheckUpToDate => Ru
        ? "У вас последняя версия."
        : "You're on the latest version.";

    public static string UpdateCheckFailed => Ru
        ? "Не удалось проверить обновления: {0}"
        : "Failed to check for updates: {0}";

    /// <summary>Banner title — args: {0}=version, {1}=size in MB.</summary>
    public static string UpdateBannerTitle => Ru
        ? "Доступна v{0} · {1:F1} МБ"
        : "v{0} available · {1:F1} MB";

    public static string UpdateBannerSubtitle => Ru
        ? "Нажми «Скачать» — установка запросит разрешение системы."
        : "Tap Download — install will ask for system permission.";

    public static string UpdateButtonDownload => Ru ? "Скачать" : "Download";

    public static string UpdateButtonInstall => Ru ? "Установить" : "Install";

    public static string UpdateButtonDismiss => Ru ? "Позже" : "Later";

    public static string UpdateButtonRetry => Ru ? "Повторить" : "Retry";

    public static string UpdateButtonGrantPermission => Ru ? "Разрешить" : "Allow";

    public static string UpdateDownloadDone => Ru
        ? "Скачано. Жми «Установить»."
        : "Downloaded. Tap Install.";

    public static string UpdateDownloadFailed => Ru
        ? "Скачивание не удалось: {0}"
        : "Download failed: {0}";

    public static string UpdateInstallPermissionNeeded => Ru
        ? "Чтобы установить APK из приложения, нужно разрешить «Установка из неизвестных источников» для VPNRouter."
        : "To install the APK from inside the app, allow \"Install from unknown sources\" for VPNRouter.";

    public static string UpdateInstallPermissionGranted => Ru
        ? "Разрешение получено — жми «Установить» снова."
        : "Permission granted — tap Install again.";

    public static string UpdateInstallLaunchFailed => Ru
        ? "Не удалось запустить установщик."
        : "Failed to launch installer.";

    // Troubleshooting items
    public static string MenuItemResetSettings => Ru ? "Сбросить настройки" : "Reset settings";

    public static string MenuItemResetConfirm => Ru
        ? "Все настройки будут удалены. Продолжить?"
        : "All settings will be cleared. Continue?";

    public static string MenuItemResetDone => Ru
        ? "Настройки сброшены. Перезапусти приложение."
        : "Settings cleared. Restart the app.";

    public static string MenuItemExportConfig => Ru ? "Экспорт конфига" : "Export config";

    public static string MenuItemImportConfig => Ru ? "Импорт конфига" : "Import config";

    public static string MenuItemShareQr => Ru ? "Поделиться по QR" : "Share via QR";

    public static string ExportTitle => Ru ? "Экспорт конфига" : "Export config";

    public static string ExportDescription => Ru
        ? "Сохраним подписки, ручной URI или custom JSON в один файл .json. Файл можно перенести на другое устройство и импортировать."
        : "Save subscriptions, manual URI or custom JSON into a single .json file. Move the file to another device and import there.";

    public static string ExportIncludeSettings => Ru
        ? "Включить настройки (тема, язык, маршрутизация)"
        : "Include settings (theme, language, routing)";

    public static string ExportIncludePerApp => Ru
        ? "Включить per-app фильтр"
        : "Include per-app filter";

    public static string ExportSecretBanner => Ru
        ? "VLESS URI / token внутри файла = пароль. Не делитесь экспортом в открытых каналах."
        : "VLESS URI / token inside the file = password. Don't share the export over public channels.";

    public static string ExportSaveButton => Ru ? "Сохранить файл…" : "Save file…";

    public static string ExportCloseButton => Ru ? "Закрыть" : "Close";

    public static string ExportSuccess => Ru
        ? "Сохранено: {0}"
        : "Saved: {0}";

    public static string ExportFailed => Ru
        ? "Не удалось сохранить: {0}"
        : "Save failed: {0}";

    public static string ExportPickerCancelled => Ru
        ? "Сохранение отменено."
        : "Save cancelled.";

    public static string ImportTitle => Ru ? "Импорт конфига" : "Import config";

    public static string ImportDescription => Ru
        ? "Выбери файл, ранее сохранённый через «Экспорт конфига». Покажем что внутри и спросим подтверждение."
        : "Pick a file previously saved via Export config. We'll show what's inside and ask for confirmation.";

    public static string ImportPickButton => Ru ? "Выбрать файл…" : "Pick a file…";

    public static string ImportPreviewLabel => Ru ? "В файле:" : "Inside the file:";

    public static string ImportApplySettings => Ru
        ? "Применить настройки (если есть в файле)"
        : "Apply settings (if present in the file)";

    public static string ImportApplyPerApp => Ru
        ? "Применить per-app фильтр (если есть в файле)"
        : "Apply per-app filter (if present)";

    public static string ImportConfirmReplace => Ru
        ? "Текущие подписки и активный конфиг будут заменены. Перед заменой сохранится резервная копия."
        : "Current subscriptions and active config will be replaced. A backup is saved before applying.";

    public static string ImportApplyButton => Ru ? "Импортировать" : "Import";

    public static string ImportCancelButton => Ru ? "Отмена" : "Cancel";

    public static string ImportCloseButton => Ru ? "Закрыть" : "Close";

    public static string ImportPickerCancelled => Ru
        ? "Импорт отменён."
        : "Import cancelled.";

    public static string ImportFailedRead => Ru
        ? "Не удалось прочитать файл: {0}"
        : "Failed to read the file: {0}";

    public static string ImportFailedParse => Ru
        ? "Файл повреждён или не от VPNRouter: {0}"
        : "File is corrupt or not a VPNRouter export: {0}";

    public static string ImportSuccess => Ru
        ? "Импорт завершён. Бэкап сохранён в {0}."
        : "Import done. Backup saved at {0}.";

    public static string ImportPartial => Ru
        ? "Импорт прошёл частично: {0}"
        : "Import partially applied: {0}";

    public static string ImportFailed => Ru
        ? "Импорт не удался: {0}"
        : "Import failed: {0}";

    public static string QrShareTitle => Ru ? "Поделиться VLESS" : "Share VLESS";

    public static string QrShareNoActiveServer => Ru
        ? "Нет активного сервера — выбери в подписке или сохрани ручной URI, потом возвращайся."
        : "No active server — pick one in a subscription or save a manual URI, then come back.";

    public static string QrShareSecretBanner => Ru
        ? "URI = пароль. Делись только лично и в защищённом канале."
        : "URI = password. Share only privately and over a secure channel.";

    public static string QrShareCopyUriButton => Ru ? "Скопировать URI" : "Copy URI";

    public static string QrShareCopiedToast => Ru
        ? "URI скопирован в буфер."
        : "URI copied to clipboard.";

    public static string QrShareScanFromClipboardLabel => Ru
        ? "Или вставь URI с другого устройства:"
        : "Or paste a URI from another device:";

    public static string QrShareScanHint => Ru
        ? "Подсказка: открой системную «Камеру» и наведи на QR — Android распознает URL и предложит скопировать."
        : "Tip: open the system Camera app and point at a QR — Android recognises the URL and offers a copy action.";

    public static string QrSharePasteButton => Ru ? "Применить URI" : "Apply URI";

    public static string QrShareApplyFailed => Ru
        ? "Не удалось распознать URI: {0}"
        : "Could not parse URI: {0}";

    public static string QrShareApplyOk => Ru
        ? "URI сохранён. Подключайся."
        : "URI saved. You can connect now.";

    public static string QrShareCloseButton => Ru ? "Закрыть" : "Close";

    public static string ConfigShareNotImplementedToast => Ru
        ? "Эта функция требует Android 4.4+ Storage Access Framework."
        : "This feature requires Android 4.4+ Storage Access Framework.";

    // About items
    public static string MenuItemVersion => Ru ? "Версия" : "Version";

    public static string MenuItemRepoLink => Ru ? "GitHub репозиторий" : "GitHub repository";

    public static string LogViewerEmpty => Ru
        ? "Лог пуст. Подключи туннель — sing-box начнёт писать сюда."
        : "Log is empty. Connect the tunnel — sing-box will start writing here.";

    public static string LogViewerError => Ru
        ? "Не удалось прочитать лог: {0}: {1}"
        : "Failed to read log: {0}: {1}";

    public static string PerAppTitle => Ru ? "Фильтр по приложениям" : "Per-app filter";

    public static string PerAppModeOff => Ru ? "Выключен" : "Off";

    public static string PerAppModeInclude => Ru ? "Только выбранные" : "Selected only";

    public static string PerAppModeExclude => Ru ? "Кроме выбранных" : "Exclude selected";

    public static string PerAppPickButton => Ru ? "Выбрать приложения…" : "Choose apps…";

    public static string PerAppCount => Ru ? "Выбрано: {0}" : "Selected: {0}";

    public static string PerAppLoading => Ru ? "Загружаю список приложений…" : "Loading app list…";

    public static string PerAppSaveButton => Ru ? "Готово" : "Done";

    public static string PerAppSearchHint => Ru ? "Поиск" : "Search";

    public static string PerAppSystemAppsToggle => Ru ? "Системные приложения" : "System apps";

    public static string PerAppEmptyHint => Ru
        ? "Ничего не выбрано. Если режим — «Только выбранные», то весь трафик пойдёт мимо туннеля."
        : "Nothing selected. If mode is \"Selected only\", all traffic bypasses the tunnel.";

    public static string PerAppPickerModeLabel => Ru ? "Режим" : "Mode";

    public static string PerAppHintInclude => Ru
        ? "Только выбранные приложения пойдут через VPN."
        : "Only the selected apps go via VPN.";

    public static string PerAppHintExclude => Ru
        ? "Выбранные приложения пойдут мимо VPN, остальные — через."
        : "The selected apps bypass VPN; everything else routes through it.";

    public static string PerAppCountInclude => Ru
        ? "Выбрано: {0} · через VPN"
        : "Selected: {0} · via VPN";

    public static string PerAppCountExclude => Ru
        ? "Выбрано: {0} · мимо VPN"
        : "Selected: {0} · bypass VPN";

    public static string PerAppGroupSelected => Ru ? "Выбранные" : "Selected";

    public static string PerAppGroupAvailable => Ru ? "Доступные" : "Available";

    public static string TipEditSubscription => Ru ? "Изменить URL" : "Edit URL";

    public static string LblNoSubscriptions => Ru ? "Подписок нет" : "No subscriptions";

    public static string SubsRemoveConfirm => Ru ? "Точно? Ещё раз — удалю" : "Sure? Tap again to delete";

    public static string SubsNeverRefreshed => Ru ? "никогда" : "never";

    public static string SubsServersFormat => Ru ? "{0} серверов" : "{0} servers";

    public static string SubsRefreshing => Ru ? "Обновляю…" : "Refreshing…";

    public static string SubsRefreshFailed => Ru ? "Ошибка: {0}" : "Failed: {0}";

    public static string SubsRefreshAllDone => Ru
        ? "Готово. Серверов: {0}"
        : "Done. Servers: {0}";

    public static string SubsCancelEdit => Ru ? "Отмена" : "Cancel";

    public static string SubsSaveEdit => Ru ? "Сохранить" : "Save";

    public static string ServerListTitleFmt => Ru
        ? "Серверы · {0}"
        : "Servers · {0}";

    public static string SrvTestAll => Ru ? "Тест все" : "Test all";

    public static string SrvTestOne => Ru ? "Тест" : "Test";

    public static string SrvTesting => Ru ? "Тестирую…" : "Testing…";

    public static string SrvSortByLatencyAsc => Ru ? "по пингу ↑" : "by ping ↑";

    public static string SrvSortByOriginal => Ru ? "по списку" : "as listed";

    public static string SrvSortToggleHint => Ru ? "Сортировка" : "Sort";

    public static string SrvProgressFmt => Ru
        ? "Протестировано {0}/{1}"
        : "Tested {0}/{1}";

    public static string SrvProgressDoneFmt => Ru
        ? "{0} рабочих из {1}"
        : "{0} reachable of {1}";

    public static string SrvEmptyHint => Ru
        ? "В этой подписке пока нет серверов. Обнови подписку (↻) чтобы получить список."
        : "This subscription has no servers yet. Refresh (↻) to fetch the list.";

    public static string SrvNeverTested => "—";

    public static string SrvUnreachable => "×";

    public static string SrvTlsFailed => Ru ? "TLS×" : "TLS×";

    public static string SrvImplausible => Ru ? "<5ms?" : "<5ms?";

    public static string SrvTipTestRow => Ru
        ? "Проверить TCP+TLS до сервера"
        : "Probe TCP+TLS to this server";

    public static string SrvTipTestAll => Ru
        ? "Параллельно проверить все серверы (4 потока)"
        : "Probe all servers in parallel (4 threads)";

    public static string SrvTipSelectServer => Ru
        ? "Выбрать как активный сервер"
        : "Set as active server";

    public static string SrvActiveBadge => Ru ? "активный" : "active";

    /// <summary>Kebab menu item that opens the Free Configs overlay.</summary>
    public static string MenuSectionFreeConfigs => Ru ? "Бесплатные конфиги" : "Free configs";

    public static string MenuItemOpenFreeConfigs => Ru ? "Найти сервер" : "Find a server";

    public static string FcOverlayTitle => Ru ? "Бесплатные конфиги" : "Free configs";

    public static string FcSearchHint => Ru
        ? "Соберём список ниже из публичных источников и проверим TCP+TLS до каждого. Жми «Найти» — выберем самые быстрые. Рабочие конфиги сохраняются автоматически — открой вкладку «★ Сохранённые», чтобы их увидеть."
        : "We'll pull the list below from public sources and run TCP+TLS to each. Tap Find — we'll pick the fastest. Verified configs are saved automatically — open the ★ Saved tab to see them.";

    public static string FcFindButton => Ru ? "✓✓ Найти рабочие конфиги" : "✓✓ Find working configs";

    public static string FcStopButton => Ru ? "✕ Остановить" : "✕ Stop";

    public static string FcExcludeRu => Ru
        ? "Исключить серверы в России"
        : "Skip servers in Russia";

    public static string FcColStatus => Ru ? "Статус" : "Status";

    public static string FcSavedEmptyHint => Ru
        ? "Сохранённых конфигов пока нет. Запусти «Найти» — найденные сохранятся здесь."
        : "No saved configs yet. Run «Find» — results will be saved here.";

    public static string FcSavedClearAll => Ru ? "✕ Удалить всё" : "✕ Clear all";

    public static string FcSavedRemoveOne => "✕";

    public static string FcStatusFetchingPool => Ru ? "Скачиваю pool.json…" : "Downloading pool.json…";

    public static string FcStatusPoolLoaded => Ru
        ? "В пуле {0} серверов. Тестирую первые {1}…"
        : "Pool has {0} servers. Testing first {1}…";

    public static string FcStatusPoolEmpty => Ru
        ? "Pool пуст или недоступен. Проверь интернет."
        : "Pool empty or unreachable. Check internet.";

    public static string FcStatusTesting => Ru
        ? "Найдено {0}/{1} · протестировано {2}/{3}"
        : "Found {0}/{1} · tested {2}/{3}";

    public static string FcStatusFound => Ru
        ? "Найдено {0}/{1} рабочих."
        : "Found {0}/{1} working.";

    /// <summary>
    /// Android Bug&#x202F;#1 status line — Deep Verify pass progress shown
    /// after TCP+TLS finishes. <c>{0}</c> = entries deep-verified so far,
    /// <c>{1}</c> = total to verify (typically the user's target N).
    /// Desktop status flow doesn't need this string because Deep Verify
    /// there is interleaved with TCP+TLS and reuses FcStatusTesting.
    /// </summary>
    public static string FcStatusDeepVerifying => Ru
        ? "Deep verify · {0}/{1}…"
        : "Deep verify · {0}/{1}…";

    public static string FcStatusDoneOk => Ru
        ? "Готово. Найдено {0} конфигов."
        : "Done. Found {0} working configs.";

    public static string FcStatusDoneExhausted => Ru
        ? "Список источников исчерпан. Найдено {0} из {1}."
        : "Sources exhausted. Found {0} of {1}.";

    public static string FcUseSelected => Ru ? "Подключить к выбранному" : "Connect to selected";

    public static string FcUsedToast => Ru
        ? "Сервер сохранён. Подключаюсь…"
        : "Server saved. Connecting…";

    public static string MenuItemSettings => Ru ? "Настройки" : "Settings";

    public static string SettingsTitle => Ru ? "Настройки" : "Settings";

    // Sub-section headers (Strings.SectionRouting / SectionLeakProtection /
    // SectionUpdates / AutostartSection in desktop)
    public static string SettingsSectionRouting => Ru ? "Маршрутизация" : "Routing";

    public static string SettingsSectionLeak => Ru ? "Защита от утечек" : "Leak Protection";

    public static string SettingsSectionUpdates => Ru ? "Обновления" : "Updates";

    public static string SettingsSectionAutostart => Ru ? "Автозапуск" : "Autostart";

    // Content section (mirrors desktop NetworkPage "Content" section).
    public static string SettingsSectionContent => Ru ? "Контент" : "Content";

    // BlockAds card (mirrors desktop MainWindowViewModel.BlockAdsLabel/Hint).
    public static string SettingsBlockAdsLabel => Ru
        ? "Блокировать рекламу и трекеры"
        : "Block ads & trackers";

    public static string SettingsBlockAdsHint => Ru
        ? "Маршрутизирует AdGuard DNS через VPN. Блокирует рекламные домены, трекеры и malware."
        : "Routes AdGuard DNS through VPN. Blocks ad domains, trackers, and malware.";

    // ── v2.32.0 AND-NETRES (2026-05-07) Reliability section ────────────
    //
    // No desktop equivalent — Android-only features (Always-on VPN, Doze
    // mode, battery optimization) live in this dedicated section. The
    // section sits between Autostart and Updates so the kebab menu user
    // who's looking for "stay connected at all times" plumbing finds it
    // before they hit the Updates / Diagnostics tail.
    public static string SettingsSectionReliability => Ru ? "Резервирование" : "Reliability";

    public static string SettingsReliabilityIntro => Ru
        ? "Чтобы VPN держался даже при перезагрузке телефона, в режиме энергосбережения и при смене Wi-Fi на мобильную сеть."
        : "Keep VPN up across reboots, in battery-saver / Doze mode, and when switching Wi-Fi ↔ cellular.";

    // Always-on VPN row
    public static string ReliabilityAlwaysOnTitle => Ru
        ? "Always-on VPN"
        : "Always-on VPN";

    public static string ReliabilityAlwaysOnHint => Ru
        ? "В системных настройках Android: VPN → шестерёнка рядом с VPNRouter → «Always-on VPN». После включения туннель поднимется сам после перезагрузки и при подключении к новой сети."
        : "In Android Settings: VPN → gear next to VPNRouter → «Always-on VPN». Once enabled, the tunnel comes up on its own after reboot and when joining a new network.";

    public static string ReliabilityAlwaysOnButton => Ru
        ? "Открыть настройки VPN"
        : "Open VPN settings";

    // Battery optimization row
    public static string ReliabilityBatteryOptTitle => Ru
        ? "Энергосбережение"
        : "Battery optimization";

    public static string ReliabilityBatteryOptStatusExempt => Ru
        ? "✓ VPNRouter исключён из энергосбережения"
        : "✓ VPNRouter is excluded from battery optimization";

    public static string ReliabilityBatteryOptStatusOptimized => Ru
        ? "⚠ VPNRouter в обычном энергосбережении — Android может прибить туннель в Doze"
        : "⚠ VPNRouter is under standard battery optimization — Android may kill the tunnel in Doze";

    public static string ReliabilityBatteryOptHint => Ru
        ? "Android в Doze (экран выключен 30+ минут) урезает CPU фоновым процессам. Если VPNRouter не исключён, sing-box может застрять между ретрансляциями и потерять трафик."
        : "Android Doze (screen-off for 30+ min) throttles background CPU. Without an exclusion, sing-box can stall between retransmissions and drop packets.";

    public static string ReliabilityBatteryOptButtonGrant => Ru
        ? "Запросить исключение"
        : "Request exclusion";

    public static string ReliabilityBatteryOptButtonOpen => Ru
        ? "Открыть настройки энергосбережения"
        : "Open battery settings";

    // Auto-reconnect toggle row
    public static string ReliabilityAutoReconnectTitle => Ru
        ? "Авто-переподключение при смене сети"
        : "Auto-reconnect on network change";

    public static string ReliabilityAutoReconnectHint => Ru
        ? "При переключении Wi-Fi ↔ мобильная sing-box сам пересвяжет upstream-сокеты с новым интерфейсом. Отключи только если подозреваешь конфликт с внутренним монитором интерфейсов libbox."
        : "On Wi-Fi ↔ cellular handoff, sing-box re-binds upstream sockets to the new interface. Disable only if you suspect a conflict with libbox's own interface monitor.";

    // Leak protection — block_on_vpn_fail toggle + DNS strategy ComboBox
    public static string BlockOnVpnFailLabel => Ru
        ? "Блокировать трафик при падении VPN"
        : "Block traffic if VPN drops";

    public static string BlockOnVpnFailHint => Ru
        ? "На Android делается через VpnService.Builder.setBlocking(true) — пакеты не уходят с устройства, пока туннель не поднимется заново."
        : "On Android this maps to VpnService.Builder.setBlocking(true) — packets stay on device until the tunnel is back up.";

    public static string DnsStrategyHeader => Ru ? "DNS-стратегия" : "DNS strategy";

    public static string DnsStrategyIpv4Only => Ru ? "Только IPv4" : "IPv4 only";

    public static string DnsStrategyPreferIpv4 => Ru ? "Предпочитать IPv4" : "Prefer IPv4";

    public static string DnsStrategyPreferIpv6 => Ru ? "Предпочитать IPv6" : "Prefer IPv6";

    public static string DnsStrategyHint => Ru
        ? "IPv4-only защищает от IPv6-утечек, если у провайдера или Wi-Fi включён IPv6, а у VPN-сервера — нет."
        : "IPv4-only protects against IPv6 leaks when the carrier/Wi-Fi advertises IPv6 but the VPN server doesn't.";

    // Updates — UpdateChannelHeader / ReceivePrereleasesLabel /
    // CurrentVersion / CheckForUpdates
    public static string UpdateChannelHeader => Ru ? "Канал обновлений" : "Update channel";

    public static string ReceivePrereleasesLabel => Ru
        ? "Получать prerelease обновления (experimental канал)"
        : "Receive prereleases (experimental channel)";

    public static string CurrentVersionLabel => Ru ? "Текущая версия" : "Current version";

    public static string CheckForUpdatesButton => Ru ? "Проверить обновления" : "Check for updates";

    public static string AutostartLabelVpn => Ru
        ? "Запускать VPN при старте системы"
        : "Start VPN on system boot";

    public static string AutostartLabelZapret => Ru
        ? "Запускать Zapret при старте системы"
        : "Start Zapret on system boot";

    public static string AutostartLabelTgProxy => Ru
        ? "Запускать TgProxy при старте системы"
        : "Start TgProxy on system boot";

    // ── v2.32.0 (AND-CC, 2026-05-07) — Custom sing-box JSON mode ───────
    //
    // Mirrors desktop's ConfigMode="custom" flow (CustomConfigViewModel,
    // ServersPage Custom sub-tab). Three labels for the segmented mode
    // selector + watermark + hint + button captions + error messages
    // surfaced from CustomConfigInjector.Validate.
    public static string CcModeSubscription => Ru ? "Подписка" : "Subscription";

    public static string CcModeManual => Ru ? "Сервер" : "Server";

    public static string CcModeCustom => Ru ? "Свой JSON" : "Custom JSON";

    public static string CcCustomLabel => Ru
        ? "Свой sing-box JSON"
        : "Custom sing-box JSON";

    public static string CcCustomHint => Ru
        ? "Вставь полный sing-box JSON-конфиг (например Hysteria2 + obfs, цепочки DNS, несколько outbounds). Перед сохранением жми «Проверить»."
        : "Paste a full sing-box JSON config (e.g. Hysteria2 + obfs, DNS chains, multiple outbounds). Tap «Validate» before saving.";

    public static string CcCustomWatermark => Ru
        ? "{ \"log\": {…}, \"dns\": {…}, \"inbounds\": […], \"outbounds\": […], \"route\": {…} }"
        : "{ \"log\": {…}, \"dns\": {…}, \"inbounds\": […], \"outbounds\": […], \"route\": {…} }";

    public static string CcValidateButton => Ru ? "Проверить" : "Validate";

    public static string CcSaveButton => Ru ? "Сохранить" : "Save";

    public static string CcClearButton => Ru ? "Очистить" : "Clear";

    public static string CcSourceCustom => Ru ? "свой JSON" : "custom JSON";

    public static string CcValidationOk => Ru
        ? "✓ JSON корректен. Найдено протоколов: {0}. Сервер: {1}."
        : "✓ JSON is valid. Protocols: {0}. Server: {1}.";

    public static string CcValidationFailed => Ru
        ? "✗ Не валидно: {0}"
        : "✗ Invalid: {0}";

    public static string CcValidationParseError => Ru
        ? "✗ Не удалось разобрать JSON: {0}"
        : "✗ Could not parse JSON: {0}";

    public static string CcSaveStatusEmpty => Ru
        ? "Введи sing-box JSON или нажми «Очистить»."
        : "Paste a sing-box JSON or tap «Clear».";

    public static string CcSaveStatusOk => Ru
        ? "Сохранено. Жми «Подключить»."
        : "Saved. Tap Connect.";

    public static string CcSaveStatusInvalid => Ru
        ? "JSON не валиден — сохраняю как есть, но sing-box может его отвергнуть."
        : "JSON is invalid — saving as-is, but sing-box may reject it.";

    public static string AutostartZapretNotPorted => Ru
        ? "⛔ Zapret пока не портирован на Android"
        : "⛔ Zapret is not ported to Android yet";

    public static string AutostartTgProxyNotPorted => Ru
        ? "⛔ TgProxy пока не портирован на Android"
        : "⛔ TgProxy is not ported to Android yet";

    // ── v2.32.0 (AND-ZAPRET, 2026-05-07) — DPI bypass picker (handbook §7 Phase 8.4) ──
    //
    // Android Zapret port via sing-box's native tls_fragment / udp_fragment
    // instead of winws.exe. Strings parallel desktop's DpiBypassPage layout
    // (LblDpiDescription, LblDpiWarning, ZapretStrategies items) but trimmed
    // for mobile — single-line picker + one hint line + warning blurb,
    // no full master-detail page (the controls are simpler on Android
    // because we don't have hosts-file installers / external binary
    // updaters to manage).
    public static string SettingsDpiBypassLabel => Ru
        ? "DPI bypass (Zapret)"
        : "DPI bypass (Zapret)";

    public static string SettingsDpiBypassHint => Ru
        ? "Дробит TLS-handshake внутри туннеля, чтобы обойти DPI российских провайдеров. Использует встроенный механизм sing-box (tls_fragment), без отдельной службы — в отличие от Windows-версии Zapret."
        : "Splits TLS handshake inside the tunnel to bypass Russian ISP DPI. Uses sing-box's native tls_fragment — no separate service, unlike the Windows Zapret port.";

    public static string SettingsDpiBypassWarning => Ru
        ? "⚠ Включай только если без него сайты не открываются. Может слегка увеличить задержку соединения."
        : "⚠ Turn on only if sites don't open without it. May add a small connection-setup delay.";

    public static string SettingsDpiBypassOff => Ru ? "Выключен" : "Off";

    public static string SettingsDpiBypassStandard => Ru ? "Стандарт" : "Standard";

    public static string SettingsDpiBypassAggressive => Ru ? "Агрессивно" : "Aggressive";

    public static string MenuSectionProfiles => Ru ? "Профили" : "Profiles";

    public static string MenuItemOpenProfiles => Ru ? "Профили маршрутизации" : "Routing profiles";

    public static string ProfilesOverlayTitle => Ru ? "Профили маршрутизации" : "Routing profiles";

    public static string ProfilesIntro => Ru
        ? "Готовые наборы приложений, которые пойдут через VPN. Тап по карточке применяет профиль и переключает в режим Split tunnel."
        : "Pre-made app bundles that go through VPN. Tap a card to apply the profile and switch to Split tunnel.";

    /// <summary>"No profile" pseudo-card at the top of the list — full traffic mode.</summary>
    public static string ProfilesNoneTitle => Ru ? "Без профиля" : "No profile";

    public static string ProfilesNoneDescription => Ru
        ? "Весь трафик через VPN. Список приложений сохранится для последующих профилей."
        : "All traffic through VPN. App list is preserved for future profiles.";

    /// <summary>Active-profile checkmark prefix glyph + label.</summary>
    public static string ProfilesActiveBadge => Ru ? "✓ Активный" : "✓ Active";

    /// <summary>Apps-count chip — args: {0}=count.</summary>
    public static string ProfilesAppsCount => Ru ? "{0} прил." : "{0} apps";

    public static string ProfilesAppsCountOne => Ru ? "1 прил." : "1 app";

    /// <summary>DNS mode chip — args: {0}=mode (vpn_only / smart / direct).</summary>
    public static string ProfilesDnsModeChip => Ru ? "DNS: {0}" : "DNS: {0}";

    /// <summary>Block-on-VPN-fail chip when profile sets it to true.</summary>
    public static string ProfilesBlockOnFailChip => Ru ? "блокировать при сбое" : "block on fail";

    /// <summary>Toast shown after applying a profile — args: {0}=profile name.</summary>
    public static string ProfilesAppliedToast => Ru
        ? "Профиль применён: {0}"
        : "Profile applied: {0}";

    public static string ProfilesClearedToast => Ru
        ? "Профиль снят. Весь трафик через VPN."
        : "Profile cleared. All traffic through VPN.";

    // ── Cross-platform kebab parity (F-10 fix, 2026-05-09) ─────────────
    //
    // Aliases that let both desktop and Android reference the same string
    // by the canonical MenuItem* name. Pre-fix the desktop kebab used
    // SmpMenu* keys and Android used MenuItem*; the wording was identical
    // for these three items but the mapping diverged. Forwarding the new
    // canonical keys to the existing SmpMenu* values keeps backward-
    // compat for any caller still on the old naming.
    public static string MenuItemCheckLeaks => SmpMenuCheckLeaks;
    public static string MenuItemHealthCheck => SmpMenuHealthCheck;
    public static string MenuItemSafeMode => SmpMenuSafeMode;
    public static string TipMenuItemHealthCheck => TipSmpMenuHealthCheck;
    public static string TipMenuItemSafeMode => TipSmpMenuSafeMode;
    public static string TipMenuItemResetConfig => TipSmpMenuResetConfig;

    // ── F-13 Android visual port: Tools / DPI Bypass overlays (2026-05-09) ──
    //
    // Strings for the new Android overlays that mirror desktop ToolsPage
    // and DpiBypassPage. The desktop pages are reachable via the Tools
    // tab in the main TabControl; on Android the kebab is the only
    // non-modal entry surface, so each gets its own kebab item.
    //
    // Content is intentionally short — Android's narrow viewport doesn't
    // tolerate wall-of-text section blurbs, and most of the underlying
    // mechanism (Zapret binary, hosts-file installers, TgProxy daemon)
    // doesn't run on Android anyway. So each overlay carries the
    // structural shell (sub-tabs, status banners, footer toggle) but
    // the content for non-applicable sections is a one-line "managed
    // automatically inside the tunnel" / "not available on Android"
    // explainer.
    public static string MenuSectionTools => Ru ? "Инструменты" : "Tools";

    public static string MenuItemOpenTools => Ru
        ? "DPI bypass + Telegram proxy"
        : "DPI bypass + Telegram proxy";

    public static string MenuItemOpenDpiBypass => Ru
        ? "DPI bypass (Zapret)"
        : "DPI bypass (Zapret)";

    public static string ToolsOverlayTitle => Ru ? "Инструменты" : "Tools";

    public static string DpiBypassOverlayTitle => Ru ? "DPI bypass" : "DPI bypass";

    /// <summary>Sub-tab strip on Android Tools overlay — left segment.</summary>
    public static string ToolsTabZapret => Ru ? "DPI bypass" : "DPI bypass";

    /// <summary>Sub-tab strip on Android Tools overlay — right segment.</summary>
    public static string ToolsTabTgProxy => Ru ? "Telegram-прокси" : "Telegram proxy";

    /// <summary>Status banner shown on Android Zapret card when DPI bypass is off.</summary>
    public static string AndroidZapretStatusOff => Ru ? "Выключено" : "Off";

    /// <summary>Status banner shown when DPI bypass is set to Standard.</summary>
    public static string AndroidZapretStatusStandard => Ru ? "Включено: Стандарт" : "On: Standard";

    /// <summary>Status banner shown when DPI bypass is set to Aggressive.</summary>
    public static string AndroidZapretStatusAggressive => Ru ? "Включено: Агрессивно" : "On: Aggressive";

    /// <summary>One-line note explaining why Hosts/Filters/Updates sections
    /// are inactive on Android (no winws.exe binary on the platform).</summary>
    public static string AndroidZapretSectionNotApplicable => Ru
        ? "Эта секция недоступна на Android — порт Zapret использует встроенный механизм sing-box (tls_fragment), без отдельной службы и hosts-файлов."
        : "This section is not applicable on Android — the Zapret port uses sing-box's native tls_fragment with no separate service or hosts files.";

    /// <summary>Body text for the TgProxy card on Android Tools overlay.</summary>
    public static string AndroidTgProxyNotApplicable => Ru
        ? "Telegram-прокси (MTProto) пока не портирован на Android. Используй DPI bypass выше — он обходит блокировку Telegram внутри основного туннеля."
        : "The Telegram MTProto proxy is not ported to Android yet. Use the DPI bypass above — it bypasses Telegram blocking inside the main tunnel.";

    /// <summary>Footer toggle button — turns DPI bypass on/off without entering Settings.</summary>
    public static string AndroidDpiBypassFooterToggleOn => Ru ? "Включить" : "Turn on";

    /// <summary>Footer toggle button — when DPI bypass is currently on.</summary>
    public static string AndroidDpiBypassFooterToggleOff => Ru ? "Выключить" : "Turn off";

    /// <summary>Diagnostics row label inside Tools overlay (Advanced section).</summary>
    public static string AndroidToolsDiagnosticsHeader => Ru ? "Диагностика" : "Diagnostics";

    /// <summary>"Run health check" button label in Tools / Advanced.</summary>
    public static string AndroidToolsRunHealthCheck => Ru ? "Запустить health check" : "Run health check";

    /// <summary>"Open log" button label in Tools / Advanced.</summary>
    public static string AndroidToolsOpenLog => Ru ? "Открыть лог sing-box" : "Open sing-box log";

    /// <summary>"Check IP leak" button label in Tools / Advanced.</summary>
    public static string AndroidToolsCheckLeak => Ru ? "Проверить IP-утечку" : "Check IP leak";

    // ── Android Advanced > Servers + Subscribe (Phase B parity, 2026-05-10) ──
    // Phase B of plans/vpnrouter-android-advanced-parity-plan.md adds a
    // sub-tab segmented control (Servers / Custom Config JSON) to the
    // Servers tab and a Test all / Deep verify / Add row footer matching
    // desktop ServersPage + SubscribePage chrome.

    /// <summary>Sub-tab label inside Servers tab — VLESS server list view (active by default).</summary>
    public static string AdvServersSubTabServers => Ru ? "Серверы" : "Servers";

    /// <summary>Sub-tab label inside Servers tab — Custom sing-box JSON config view.</summary>
    public static string AdvServersSubTabCustomJson => Ru ? "Custom Config (JSON)" : "Custom Config (JSON)";

    /// <summary>Footer action button — runs TCP+TLS probe on every listed server.</summary>
    public static string AdvServersTestAll => Ru ? "Тест все" : "Test all";

    /// <summary>Footer action button — runs deep HTTP-through-tunnel verification on every listed server.</summary>
    public static string AdvServersDeepVerify => Ru ? "Deep verify" : "Deep verify";

    /// <summary>Footer action button — removes the highlighted/active server from the list.</summary>
    public static string AdvServersRemove => Ru ? "Удалить" : "Remove";

    /// <summary>Footer action button — adds a server parsed from the URI input field.</summary>
    public static string AdvServersAddServers => Ru ? "+ Добавить" : "+ Add Server(s)";

    /// <summary>Subscribe tab action — refreshes every enabled subscription via SubscriptionFetcher.</summary>
    public static string AdvSubscribeRefreshAll => Ru ? "Обновить все" : "Refresh all";

    /// <summary>Subscribe tab — header above the add-new-subscription form (mirrors desktop section layout).</summary>
    public static string AdvSubscribeAddSubscription => Ru ? "Добавить подписку" : "Add subscription";

    /// <summary>Subscribe tab — input watermark for the subscription's display name (compact left field).</summary>
    public static string AdvSubscribeNameLabel => Ru ? "Имя" : "Name";

    /// <summary>Subscribe tab — input watermark for the subscription's URL (long right field).</summary>
    public static string AdvSubscribeUrlLabel => Ru ? "URL подписки" : "Subscription URL";

    /// <summary>
    /// Status text shown when the Custom Config (JSON) sub-tab is selected
    /// but no JSON has been pasted yet. Explains what the textarea does.
    /// </summary>
    public static string AdvServersCustomJsonExplainer => Ru
        ? "Свой sing-box JSON для нестандартных протоколов (Hysteria2, TUIC, Reality+gRPC и т.п.). Вставь конфиг ниже и сохрани — VPNRouter подменит routing рулы автоматически."
        : "Custom sing-box JSON for non-standard protocols (Hysteria2, TUIC, Reality+gRPC, etc.). Paste a config below and save — VPNRouter injects the routing rules automatically.";

    /// <summary>
    /// Watermark for the deep-verify button when the Android binary can
    /// only run TCP+TLS probes (sing-box can't be spawned as a subprocess
    /// inside the app). Surfaced as a tooltip on the Deep verify button.
    /// </summary>
    public static string AdvServersDeepVerifyAndroidNote => Ru
        ? "На Android Deep verify эквивалентен расширенному TCP+TLS пробу — отдельный sing-box процесс из приложения недоступен."
        : "On Android, Deep verify equals an extended TCP+TLS probe — spawning a separate sing-box process from the app isn't available.";

    /// <summary>
    /// Empty state shown in the Subscribe tab's aggregated server list
    /// when no enabled subscription has fetched any servers yet.
    /// </summary>
    public static string AdvSubscribeAggregatedEmpty => Ru
        ? "В подписках пока нет серверов — добавь подписку ниже и нажми ↻."
        : "No servers in any subscription yet — add one below and click ↻.";

    // ── Phase C: Android Settings tab side-nav (2026-05-10) ─────────────
    public static string SettingsSectionRules => Ru ? "Правила" : "Rules";

    public static string AdvSettingsRulesAndroidNote => Ru
        ? "Кастомные правила маршрутизации (домен → действие) пока не подключены на Android. Пока что используй вкладку «Приложения» — там можно выбрать, какие приложения идут через VPN."
        : "Custom routing rules (domain → action) aren't wired into the Android tunnel yet. For now use the Apps tab to choose which apps go through VPN.";

    public static string AdvSettingsAutostartAndroidIntro => Ru
        ? "На Android системного аналога Windows-службы нет. Чтобы VPN поднимался после перезагрузки и при смене сети — включи «Always-on VPN» в системных настройках Android (кнопка ниже)."
        : "Android has no system-level equivalent of the Windows service. To bring the VPN up after reboot and network change, enable «Always-on VPN» in Android system settings (button below).";

    // AND-ADV-TOOLS-PUBLIC (2026-05-10) — Phase E of Android Advanced
    // parity. Tools tab now hosts merged Zapret + Telegram sub-tabs;
    // Public tab keeps the existing Search / Saved sub-tabs but with
    // localization keys aligned to the AdvPublic* naming scheme.
    public static string AdvToolsSubTabZapret => Ru ? "Zapret" : "Zapret";
    public static string AdvToolsSubTabTelegram => Ru ? "Telegram-прокси" : "Telegram proxy";

    /// <summary>Banner shown inside Tools > Zapret on Android explaining
    /// the platform-impossible substitution: desktop's 5-section side-nav
    /// (Status/Strategy/Hosts/Filters/Advanced) drives a winws.exe Cygwin
    /// process that doesn't exist on Android. The Android port uses
    /// sing-box's native tls_fragment outbound instead, so only the mode
    /// picker (off / standard / aggressive) is meaningful here.</summary>
    public static string AdvToolsZapretAndroidExplainer => Ru
        ? "Android использует встроенный sing-box (tls_fragment) вместо winws.exe. Поэтому секции Status / Strategy / Hosts / Filters / Advanced с десктопа здесь не применимы — управление сводится к выбору режима ниже."
        : "Android uses sing-box's native tls_fragment instead of winws.exe. The desktop Status / Strategy / Hosts / Filters / Advanced sub-sections don't apply — only the mode picker below is meaningful.";

    /// <summary>Banner shown inside Tools > Telegram proxy on Android.
    /// Desktop hosts a full TgProxy daemon (download / start / stop /
    /// secret regeneration). Android routes Telegram traffic through the
    /// main sing-box tunnel — no daemon needed, no controls to expose.</summary>
    public static string AdvToolsTelegramAndroidExplainer => Ru
        ? "На Android Telegram-трафик идёт через основной VPN-туннель — отдельный MTProto-демон не нужен. Кнопка ниже открывает приложение Telegram, если оно установлено."
        : "On Android, Telegram traffic is routed through the main VPN tunnel — no separate MTProto daemon is needed. The button below opens the Telegram app if it's installed.";

    /// <summary>"Open Telegram" deep-link button on the Telegram sub-tab.</summary>
    public static string AdvToolsOpenTelegram => Ru ? "Открыть Telegram" : "Open Telegram";

    /// <summary>Toast shown when "Open Telegram" can't find an installed
    /// Telegram client (org.telegram.messenger missing). Falls through to
    /// opening the Play Store listing.</summary>
    public static string AdvToolsTelegramNotInstalled => Ru
        ? "Telegram не установлен — открываю Play Store."
        : "Telegram is not installed — opening Play Store.";

    // ── Public tab (Phase E P1-P4) ──────────────────────────────────────
    public static string AdvPublicSubTabSearch => Ru ? "▶ Поиск" : "▶ Search";
    public static string AdvPublicSubTabSaved  => Ru ? "★ Сохранённые" : "★ Saved";

    /// <summary>Big green CTA on Public > Search. Mirrors desktop's
    /// "✓✓ Найти рабочие конфиги" / "✓✓ Find working configs".</summary>
    public static string AdvPublicFindButton => Ru
        ? "✓✓ Найти рабочие конфиги"
        : "✓✓ Find working configs";

    /// <summary>"Settings" expander label inside the green search card.</summary>
    public static string AdvPublicSettingsExpand => Ru ? "▾ Настройки" : "▾ Settings";

    /// <summary>Per-tab Connect button at the bottom of Public.</summary>
    public static string AdvPublicConnect => Ru ? "Подключить" : "Connect";

    /// <summary>Empty-state hint when the configs list is empty (pre-find).</summary>
    public static string AdvPublicCacheEmpty => Ru
        ? "Нажмите кнопку выше, чтобы найти рабочие публичные конфиги."
        : "Click the button above to find working public configs.";

    /// <summary>Hint shown when no row is highlighted and the bottom Connect
    /// button is disabled.</summary>
    public static string AdvPublicSelectRow => Ru
        ? "Выберите конфиг из списка и нажмите «Подключить»."
        : "Select a config from the list and click Connect.";
}
