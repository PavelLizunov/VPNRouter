namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string SettingsSectionReliability => Ru ? "Надёжность" : "Reliability";

    public static string SettingsReliabilityIntro => Ru
        ? "Чтобы VPN держался даже при перезагрузке телефона, в режиме энергосбережения и при смене Wi-Fi на мобильную сеть."
        : "Keep VPN up across reboots, in battery-saver / Doze mode, and when switching Wi-Fi ↔ cellular.";

    public static string ReliabilityAlwaysOnTitle => Ru
        ? "Always-on VPN"
        : "Always-on VPN";

    public static string ReliabilityAlwaysOnHint => Ru
        ? "В системных настройках Android: VPN → шестерёнка рядом с VPNRouter → «Always-on VPN». После включения туннель поднимется сам после перезагрузки и при подключении к новой сети."
        : "In Android Settings: VPN → gear next to VPNRouter → «Always-on VPN». Once enabled, the tunnel comes up on its own after reboot and when joining a new network.";

    public static string ReliabilityAlwaysOnButton => Ru
        ? "Открыть настройки VPN"
        : "Open VPN settings";

    public static string ReliabilityBatteryOptTitle => Ru
        ? "Энергосбережение"
        : "Battery optimization";

    public static string ReliabilityBatteryOptStatusExempt => Ru
        ? "VPNRouter исключён из энергосбережения"
        : "VPNRouter is excluded from battery optimization";

    public static string ReliabilityBatteryOptStatusOptimized => Ru
        ? "VPNRouter в обычном энергосбережении — Android может прибить туннель в Doze"
        : "VPNRouter is under standard battery optimization — Android may kill the tunnel in Doze";

    public static string ReliabilityBatteryOptHint => Ru
        ? "Android в Doze (экран выключен 30+ минут) урезает CPU фоновым процессам. Если VPNRouter не исключён, sing-box может застрять между ретрансляциями и потерять трафик."
        : "Android Doze (screen-off for 30+ min) throttles background CPU. Without an exclusion, sing-box can stall between retransmissions and drop packets.";

    public static string ReliabilityBatteryOptButtonGrant => Ru
        ? "Запросить исключение"
        : "Request exclusion";

    public static string ReliabilityBatteryOptButtonOpen => Ru
        ? "Открыть настройки энергосбережения"
        : "Open battery settings";

    public static string AlwaysOnNudgeTitle => Ru
        ? "Включить kill-switch?"
        : "Enable a kill-switch?";
    public static string AlwaysOnNudgeBody => Ru
        ? "Чтобы трафик не утекал, если VPN отвалится, включите VPNRouter как «Always-on VPN» с «Блокировкой» (Lockdown) в системных настройках VPN. Без этого Android не гарантирует блокировку при разрыве туннеля."
        : "To stop traffic from leaking if the VPN drops, set VPNRouter as your Always-on VPN with \"Block connections without VPN\" (Lockdown) in system VPN settings. Without it, Android can't guarantee a block when the tunnel fails.";
    public static string AlwaysOnNudgeOpen => Ru ? "Открыть настройки VPN" : "Open VPN settings";
    public static string AlwaysOnNudgeLater => Ru ? "Позже" : "Not now";

    public static string ReliabilityAutoReconnectTitle => Ru
        ? "Авто-переподключение при смене сети"
        : "Auto-reconnect on network change";

    public static string ReliabilityAutoReconnectHint => Ru
        ? "При переключении Wi-Fi ↔ мобильная sing-box сам пересвяжет upstream-сокеты с новым интерфейсом. Отключите только если подозреваете конфликт с внутренним монитором интерфейсов libbox."
        : "On Wi-Fi ↔ cellular handoff, sing-box re-binds upstream sockets to the new interface. Disable only if you suspect a conflict with libbox's own interface monitor.";

    public static string ExternalControlTitle => Ru
        ? "Разрешить внешнее управление (Tasker, виджеты)"
        : "Allow external control (Tasker, widgets)";
    public static string ExternalControlHint => Ru
        ? "Другие приложения смогут включать/выключать VPN через broadcast (EXT_START / EXT_STOP / EXT_TOGGLE). По умолчанию выключено. Включайте только если доверяете автоматизации — при включении управлять туннелем сможет любое приложение."
        : "Lets other apps start/stop the VPN via broadcast (EXT_START / EXT_STOP / EXT_TOGGLE). Off by default. Enable only if you trust your automation — while on, any app can control the tunnel.";

    public static string BlockOnVpnFailHint => Ru
        ? "Android не блокирует трафик при падении VPN сам. Включите VPNRouter как «Always-on VPN» и «Блокировать соединения без VPN» (Lockdown) в системных настройках VPN."
        : "Android does not block traffic when the VPN drops on its own. Set VPNRouter as Always-on VPN and enable \"Block connections without VPN\" (Lockdown) in system VPN settings.";

    public static string DnsStrategyHeader => Ru ? "DNS-стратегия" : "DNS strategy";

    public static string DnsStrategyIpv4Only => Ru ? "Только IPv4" : "IPv4 only";

    public static string DnsStrategyPreferIpv4 => Ru ? "Предпочитать IPv4" : "Prefer IPv4";

    public static string DnsStrategyPreferIpv6 => Ru ? "Предпочитать IPv6" : "Prefer IPv6";

    public static string DnsStrategyHint => Ru
        ? "IPv4-only защищает от IPv6-утечек, если у провайдера или Wi-Fi включён IPv6, а у VPN-сервера — нет."
        : "IPv4-only protects against IPv6 leaks when the carrier/Wi-Fi advertises IPv6 but the VPN server doesn't.";

    public static string UpdateChannelHeader => Ru ? "Канал обновлений" : "Update channel";

    public static string ReceivePrereleasesLabel => Ru
        ? "Получать пре-релизы (экспериментальный канал)"
        : "Receive prereleases (experimental channel)";

    public static string CurrentVersionLabel => Ru ? "Версия" : "Version";

    public static string CheckForUpdatesButton => Ru ? "Проверить" : "Check for updates";

    public static string AutostartLabelVpn => Ru
        ? "Запускать VPN при старте системы"
        : "Start VPN on system boot";

    public static string AutostartLabelZapret => Ru
        ? "Запускать Zapret при старте системы"
        : "Start Zapret on system boot";

    public static string AutostartLabelTgProxy => Ru
        ? "Запускать TgProxy при старте системы"
        : "Start TgProxy on system boot";

    public static string CcModeSubscription => Ru ? "Подписка" : "Subscription";

    public static string CcModeManual => Ru ? "Сервер" : "Server";

    public static string CcModeCustom => Ru ? "Свой конфиг (JSON)" : "Custom Config (JSON)";

    public static string CcCustomLabel => Ru
        ? "Свой sing-box JSON"
        : "Custom sing-box JSON";

    public static string CcCustomHint => Ru
        ? "Вставьте полный JSON-конфиг sing-box (например Hysteria2 + obfs, цепочки DNS, несколько outbounds). Перед сохранением нажмите «Проверить»."
        : "Paste a full sing-box JSON config (e.g. Hysteria2 + obfs, DNS chains, multiple outbounds). Tap «Validate» before saving.";

    public static string CcCustomWatermark => Ru
        ? "{ \"log\": {…}, \"dns\": {…}, \"inbounds\": […], \"outbounds\": […], \"route\": {…} }"
        : "{ \"log\": {…}, \"dns\": {…}, \"inbounds\": […], \"outbounds\": […], \"route\": {…} }";

    public static string CcValidateButton => Ru ? "Проверить" : "Validate";

    public static string CcSaveButton => Ru ? "Сохранить" : "Save";

    public static string CcClearButton => Ru ? "Очистить" : "Clear";

    public static string CcSourceCustom => Ru ? "свой JSON" : "custom JSON";

    public static string CcValidationOk => Ru
        ? "JSON корректен. Найдено протоколов: {0}. Сервер: {1}."
        : "JSON is valid. Protocols: {0}. Server: {1}.";

    public static string CcValidationFailed => Ru
        ? "Некорректно: {0}"
        : "Invalid: {0}";

    public static string CcValidationParseError => Ru
        ? "Не удалось разобрать JSON: {0}"
        : "Could not parse JSON: {0}";

    public static string CcSaveStatusEmpty => Ru
        ? "Вставьте sing-box JSON или нажмите «Очистить»."
        : "Paste a sing-box JSON or tap «Clear».";

    public static string CcSaveStatusOk => Ru
        ? "Сохранено. Нажмите «Подключить»."
        : "Saved. Tap Connect.";

    public static string CcSaveStatusInvalid => Ru
        ? "JSON некорректен — сохраняю как есть, но sing-box может его отвергнуть."
        : "JSON is invalid — saving as-is, but sing-box may reject it.";

    public static string AutostartZapretNotPorted => Ru
        ? "Zapret пока не портирован на Android"
        : "Zapret is not ported to Android yet";

    public static string AutostartTgProxyNotPorted => Ru
        ? "TgProxy пока не портирован на Android"
        : "TgProxy is not ported to Android yet";

    public static string SettingsDpiBypassLabel => Ru
        ? "Обход блокировок (Zapret)"
        : "DPI bypass (Zapret)";

    public static string SettingsDpiBypassHint => Ru
        ? "Дробит TLS-handshake внутри туннеля, чтобы обойти DPI российских провайдеров. Использует встроенный механизм sing-box (tls_fragment), без отдельной службы — в отличие от Windows-версии Zapret."
        : "Splits TLS handshake inside the tunnel to bypass Russian ISP DPI. Uses sing-box's native tls_fragment — no separate service, unlike the Windows Zapret port.";

    public static string SettingsDpiBypassWarning => Ru
        ? "Включайте только если без него сайты не открываются. Может незначительно увеличить задержку соединения."
        : "Turn on only if sites don't open without it. May add a small connection-setup delay.";

    public static string SettingsDpiBypassOff => Ru ? "Выключен" : "Off";

    public static string SettingsDpiBypassStandard => Ru ? "Стандарт" : "Standard";

    public static string SettingsDpiBypassAggressive => Ru ? "Агрессивно" : "Aggressive";

    public static string ProfilesOverlayTitle => Ru ? "Профили маршрутизации" : "Routing profiles";

    public static string ProfilesIntro => Ru
        ? "Готовые наборы приложений, которые пойдут через VPN. Тап по карточке применяет профиль и переключает в режим Split tunnel."
        : "Pre-made app bundles that go through VPN. Tap a card to apply the profile and switch to Split tunnel.";

    public static string ProfilesNoneTitle => Ru ? "Без профиля" : "No profile";

    public static string ProfilesNoneDescription => Ru
        ? "Весь трафик через VPN. Список приложений сохранится для последующих профилей."
        : "All traffic through VPN. App list is preserved for future profiles.";

    public static string ProfilesActiveBadge => Ru ? "✓ Активный" : "✓ Active";

    public static string ProfilesAppsCount => Ru ? "{0} прил." : "{0} apps";

    public static string ProfilesAppsCountOne => Ru ? "1 прил." : "1 app";

    public static string ProfilesDnsModeChip => Ru ? "DNS: {0}" : "DNS: {0}";

    public static string ProfilesBlockOnFailChip => Ru ? "блокировать при сбое" : "block on fail";

    public static string ProfilesAppliedToast => Ru
        ? "Профиль применён: {0}"
        : "Profile applied: {0}";

    public static string ProfilesClearedToast => Ru
        ? "Профиль снят. Весь трафик через VPN."
        : "Profile cleared. All traffic through VPN.";

    public static string MenuItemCheckLeaks => SmpMenuCheckLeaks;
    public static string MenuItemHealthCheck => SmpMenuHealthCheck;
    public static string MenuItemSafeMode => SmpMenuSafeMode;

    public static string MenuItemAddTile => Ru
        ? "Добавить кнопку VPN в шторку"
        : "Add VPN button to the shade";

    public static string TileAddInstruction => Ru
        ? "Проведите вниз от верха экрана двумя пальцами, нажмите значок карандаша (изменить) и перетащите плитку «VPNRouter» в верхнюю часть."
        : "Swipe down from the top of the screen with two fingers, tap the pencil (edit) icon and drag the \"VPNRouter\" tile into the active area.";

    public static string TileAddResultAdded => Ru ? "Кнопка VPN добавлена в шторку." : "The VPN button was added to the shade.";
    public static string TileAddResultAlready => Ru ? "Кнопка VPN уже есть в шторке." : "The VPN button is already in the shade.";
    public static string TileAddResultDeclined => Ru ? "Кнопка не добавлена." : "The button was not added.";

    public static string TileSetupNeeded => Ru
        ? "Сначала настройте подключение в приложении: после этого кнопка в шторке заработает."
        : "Set up a connection in the app first; then the button in the shade works.";
    public static string TipMenuItemHealthCheck => TipSmpMenuHealthCheck;
    public static string TipMenuItemSafeMode => TipSmpMenuSafeMode;
    public static string TipMenuItemResetConfig => TipSmpMenuResetConfig;

    public static string ToolsOverlayTitle => Ru ? "Инструменты" : "Tools";

    public static string DpiBypassOverlayTitle => Ru ? "Обход DPI" : "DPI bypass";

    public static string ToolsTabZapret => Ru ? "Обход DPI" : "DPI bypass";

    public static string ToolsTabTgProxy => Ru ? "Telegram-прокси" : "Telegram proxy";

    public static string AndroidZapretStatusOff => Ru ? "Выключено" : "Off";

    public static string AndroidZapretStatusStandard => Ru ? "Включено: Стандарт" : "On: Standard";

    public static string AndroidZapretStatusAggressive => Ru ? "Включено: Агрессивно" : "On: Aggressive";

    public static string AndroidZapretSectionNotApplicable => Ru
        ? "Эта секция недоступна на Android — порт Zapret использует встроенный механизм sing-box (tls_fragment), без отдельной службы и hosts-файлов."
        : "This section is not applicable on Android — the Zapret port uses sing-box's native tls_fragment with no separate service or hosts files.";

    public static string AndroidTgProxyNotApplicable => Ru
        ? "Telegram-прокси (MTProto) пока не портирован на Android. Используй DPI bypass выше — он обходит блокировку Telegram внутри основного туннеля."
        : "The Telegram MTProto proxy is not ported to Android yet. Use the DPI bypass above — it bypasses Telegram blocking inside the main tunnel.";

    public static string AndroidDpiBypassFooterToggleOn => Ru ? "Включить" : "Turn on";

    public static string AndroidDpiBypassFooterToggleOff => Ru ? "Выключить" : "Turn off";

    public static string AndroidToolsDiagnosticsHeader => Ru ? "Диагностика" : "Diagnostics";

    public static string AndroidToolsRunHealthCheck => Ru ? "Запустить health check" : "Run health check";

    public static string AndroidToolsOpenLog => Ru ? "Открыть лог sing-box" : "Open sing-box log";

    public static string AndroidToolsCheckLeak => Ru ? "Проверить IP-утечку" : "Check IP leak";

    public static string AdvServersSubTabServers => Ru ? "Серверы" : "Servers";

    public static string AdvServersSubTabCustomJson => Ru ? "Свой конфиг (JSON)" : "Custom Config (JSON)";

    public static string AdvServersTestAll => Ru ? "Тест все" : "Test all";

    public static string AdvServersDeepVerify => Ru ? "Глубокая проверка" : "Deep verify";

    public static string AdvServersRemove => Ru ? "Удалить" : "Remove";

    public static string AdvServersAddServers => Ru ? "+ Добавить" : "+ Add Server(s)";

    public static string AdvSubscribeRefreshAll => Ru ? "Обновить все" : "Refresh all";

    public static string AdvSubscribeAddSubscription => Ru ? "Добавить подписку" : "Add subscription";

    public static string AdvSubscribeNameLabel => Ru ? "Имя" : "Name";

    public static string AdvSubscribeUrlLabel => Ru ? "URL подписки" : "Subscription URL";

    public static string AdvServersCustomJsonExplainer => Ru
        ? "Свой sing-box JSON для нестандартных протоколов (Hysteria2, TUIC, Reality+gRPC и т.п.). Вставь конфиг ниже и сохрани — VPNRouter подменит routing рулы автоматически."
        : "Custom sing-box JSON for non-standard protocols (Hysteria2, TUIC, Reality+gRPC, etc.). Paste a config below and save — VPNRouter injects the routing rules automatically.";

    public static string AdvServersDeepVerifyAndroidNote => Ru
        ? "На Android Deep verify эквивалентен расширенному TCP+TLS пробу — отдельный sing-box процесс из приложения недоступен."
        : "On Android, Deep verify equals an extended TCP+TLS probe — spawning a separate sing-box process from the app isn't available.";

    public static string AdvSubscribeAggregatedEmpty => Ru
        ? "В подписках пока нет серверов — добавьте подписку ниже и обновите её."
        : "No servers in any subscription yet — add one below and refresh it.";

    public static string SettingsSectionRules => Ru ? "Правила" : "Rules";

    public static string AdvSettingsRulesAndroidNote => Ru
        ? "Кастомные правила маршрутизации (домен → действие) пока не подключены на Android. Используйте вкладку «Приложения» — там можно выбрать, какие приложения идут через VPN."
        : "Custom routing rules (domain → action) aren't wired into the Android tunnel yet. For now use the Apps tab to choose which apps go through VPN.";

    public static string AdvSettingsAutostartAndroidIntro => Ru
        ? "На Android системного аналога Windows-службы нет. Чтобы VPN поднимался после перезагрузки и при смене сети — включите «Always-on VPN» в системных настройках Android (кнопка ниже)."
        : "Android has no system-level equivalent of the Windows service. To bring the VPN up after reboot and network change, enable «Always-on VPN» in Android system settings (button below).";

    public static string AdvToolsSubTabZapret => Ru ? "Zapret" : "Zapret";
    public static string AdvToolsSubTabTelegram => Ru ? "Telegram-прокси" : "Telegram proxy";

    public static string AdvToolsZapretAndroidExplainer => Ru
        ? "Android использует встроенный sing-box (tls_fragment) вместо winws.exe. Поэтому секции Status / Strategy / Hosts / Filters / Advanced с десктопа здесь не применимы — управление сводится к выбору режима ниже."
        : "Android uses sing-box's native tls_fragment instead of winws.exe. The desktop Status / Strategy / Hosts / Filters / Advanced sub-sections don't apply — only the mode picker below is meaningful.";

    public static string AdvToolsTelegramAndroidExplainer => Ru
        ? "На Android Telegram-трафик идёт через основной VPN-туннель — отдельный MTProto-демон не нужен. Кнопка ниже открывает приложение Telegram, если оно установлено."
        : "On Android, Telegram traffic is routed through the main VPN tunnel — no separate MTProto daemon is needed. The button below opens the Telegram app if it's installed.";

    public static string AdvToolsOpenTelegram => Ru ? "Открыть Telegram" : "Open Telegram";

    public static string AdvToolsTelegramNotInstalled => Ru
        ? "Telegram не установлен — открываю Play Store."
        : "Telegram is not installed — opening Play Store.";

    public static string AdvPublicSubTabSearch => Ru ? "▶ Поиск" : "▶ Search";
    public static string AdvPublicSubTabSaved  => Ru ? "Сохранённые" : "Saved";

    public static string AdvPublicFindButton => Ru
        ? "✓✓ Найти рабочие конфиги"
        : "✓✓ Find working configs";

    public static string AdvPublicSettingsExpand => Ru ? "▾ Настройки" : "▾ Settings";

    public static string AdvPublicConnect => Ru ? "Подключить" : "Connect";

    public static string AdvPublicCacheEmpty => Ru
        ? "Нажмите кнопку выше, чтобы найти рабочие публичные конфиги."
        : "Click the button above to find working public configs.";

    public static string AdvPublicSelectRow => Ru
        ? "Выберите конфиг из списка и нажмите «Подключить»."
        : "Select a config from the list and click Connect.";

    public static string ConflictOtherVpnDetectedTitle => Ru
        ? "Обнаружен другой VPN-клиент"
        : "Another VPN client detected";

    public static string ConflictOtherVpnDetectedMessage(string processName, int pid) => Ru
        ? $"Обнаружен другой VPN-клиент: {processName} (PID {pid}). " +
          $"Один VPN держит TUN-адаптер за раз. Остановите {processName} перед запуском VPNRouter."
        : $"Another VPN client detected: {processName} (PID {pid}). " +
          $"Only one VPN can hold the TUN adapter at a time. Stop {processName} before launching VPNRouter.";

    public static string ConflictRefreshButton => Ru ? "Проверить ещё раз" : "Refresh";

    public static string ZapretAvBlockToast => Ru
        ? "Zapret (winws.exe) был остановлен сразу после запуска. Возможно его блокирует антивирус. " +
          @"Добавьте в исключения: C:\ProgramData\VPNRouter\zapret\ (вся папка)."
        : "Zapret (winws.exe) exited immediately after launch. Likely an antivirus is blocking it. " +
          @"Whitelist: C:\ProgramData\VPNRouter\zapret\ (whole folder).";

    public static string ZapretAvBlockCopyPath => Ru ? "Скопировать путь" : "Copy path";

    // Android versions of desktop strings: touch wording, and no text symbols (the Android UI draws icons).
    public static string FcConnectHintTouch => Ru
        ? "Выберите строку и нажмите «Подключить к выбранному»"
        : "Tap a row, then Connect to selected";
    public static string FcConnectNeedsVerifyTouch => Ru
        ? "Дождитесь, пока конфиг пройдёт проверку, и подключайтесь."
        : "Wait until the config is verified, then connect.";
}
