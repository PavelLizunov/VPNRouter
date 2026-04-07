namespace VPNRouter.GUI.Localization;

/// <summary>
/// Bilingual string provider (EN/RU). Set <see cref="Lang"/> before accessing properties.
/// </summary>
public static class Strings
{
    /// <summary>Current language code: "en" or "ru".</summary>
    public static string Lang { get; set; } = "en";

    private static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    // ── Tabs ──────────────────────────────────────────────────────────────────
    public static string TabServers => Ru ? "Серверы" : "Servers";
    public static string TabApps => Ru ? "Приложения" : "Applications";

    // ── Config mode ──────────────────────────────────────────────────────────
    public static string VlessServers => Ru ? "VLESS Серверы" : "VLESS Servers";
    public static string CustomConfigJson => Ru ? "Свой конфиг (JSON)" : "Custom Config (JSON)";
    public static string PasteVlessUri => Ru ? "Вставьте VLESS URI:" : "Paste VLESS URI(s):";
    public static string PlaceholderVless => "vless://uuid@server:443?security=reality&sni=...#name";

    // ── Buttons ──────────────────────────────────────────────────────────────
    public static string StartVPN => Ru ? "\u25b6  Запустить VPN" : "\u25b6  Start VPN";
    public static string StopVPN => Ru ? "\u2b1b  Остановить VPN" : "\u2b1b  Stop VPN";
    public static string AddServers => Ru ? "Добавить сервер(ы)" : "Add Server(s)";
    public static string Remove => Ru ? "Удалить" : "Remove";
    public static string ClearAll => Ru ? "Очистить все" : "Clear All";
    public static string AddConfig => Ru ? "Добавить конфиг..." : "Add Config...";
    public static string Apply => Ru ? "\u21bb  Применить" : "\u21bb  Apply";
    public static string BtnUp => Ru ? "\u25b2 Вверх" : "\u25b2 Up";
    public static string BtnDown => Ru ? "\u25bc Вниз" : "\u25bc Down";
    public static string Update => Ru ? "Обновить" : "Update";
    public static string BtnAdd => Ru ? "Добавить" : "Add";
    public static string RemoveChecked => Ru ? "Удалить выбранные" : "Remove checked";

    // ── Apps tab ─────────────────────────────────────────────────────────────
    public static string SplitTunnel => Ru ? "Split Tunnel (выбранные приложения)" : "Split Tunnel (selected apps)";
    public static string FullTunnel => Ru ? "Full Tunnel (весь трафик)" : "Full Tunnel (all traffic)";
    public static string BypassRussianTraffic => Ru ? "Российский трафик через реальный IP" : "Russian traffic via real IP";
    public static string CheckLeaks => Ru ? "Проверить утечки" : "Check leaks";
    public static string ShowLogs => Ru ? "Логи" : "Logs";
    public static string StrictMode => Ru ? "Строгий режим (быстрая реакция на сбои)" : "Strict mode (faster crash detection)";
    public static string ForceIpv4Only => Ru ? "Только IPv4 (защита от IPv6 leak)" : "Force IPv4 only (IPv6 leak protection)";
    public static string FlushDnsOnStart => Ru ? "Очищать DNS кэш при подключении" : "Flush DNS cache on connect";
    public static string StrictDns => Ru ? "Строгий DNS (весь DNS через VPN, без утечек)" : "Strict DNS (all DNS via VPN, no leaks)";
    public static string AppsHint => Ru
        ? "Выберите группы для маршрутизации через VPN (раскройте для просмотра):"
        : "Check groups to route through VPN (expand to see apps inside):";
    public static string CustomAppLabel => Ru
        ? "Добавить приложение (имя .exe, напр. spotify.exe):"
        : "Add custom app (exe name, e.g. spotify.exe)";
    public static string CustomAppsNode => Ru
        ? "Свои приложения  \u2014  Добавленные вручную"
        : "Custom Apps  \u2014  Your custom applications";
    public static string ChildProcesses => Ru ? "(+ дочерние процессы)" : "(+ child processes)";

    // ── Header ───────────────────────────────────────────────────────────────
    public static string CheckForUpdates => Ru ? "Проверить обновления" : "Check for updates";
    public static string Checking => Ru ? "Проверка..." : "Checking...";
    public static string UpToDate => Ru ? "Обновлений нет \u2713" : "You're up to date \u2713";
    public static string CheckFailed => Ru ? "Ошибка проверки" : "Check failed";
    public static string ThemeDark => Ru ? "\u25cf Тёмная" : "\u25cf Dark";
    public static string ThemeLight => Ru ? "\u25cb Светлая" : "\u25cb Light";
    public static string ChannelExp => Ru ? "\u26a0 Эксперимент." : "\u26a0 Experimental";
    public static string ChannelStable => Ru ? "\u2714 Стабильная" : "\u2714 Stable";

    // ── Status ───────────────────────────────────────────────────────────────
    public static string NotConnected => Ru ? "Не подключено" : "Not connected";
    public static string ConnectedService => Ru
        ? "Подключено \u2014 Служба Windows (автозапуск)"
        : "Connected \u2014 Windows Service (autostart)";
    public static string Connected(string profile, int pid) => Ru
        ? $"Подключено \u2014 {profile} \u2014 PID {pid}"
        : $"Connected \u2014 {profile} \u2014 PID {pid}";

    // ── Action states ────────────────────────────────────────────────────────
    public static string Starting => Ru ? "Запуск..." : "Starting...";
    public static string Stopping => Ru ? "Остановка..." : "Stopping...";
    public static string Applying => Ru ? "Применение..." : "Applying...";
    public static string ApplyingChanges => Ru ? "Применение изменений..." : "Applying changes...";
    public static string Restarting => Ru ? "Перезапуск..." : "Restarting...";
    public static string RestartingService => Ru ? "Перезапуск службы..." : "Restarting service...";
    public static string InstallingService => Ru ? "Установка службы..." : "Installing service...";
    public static string RemovingService => Ru ? "Удаление службы..." : "Removing service...";
    public static string Reinstalling => Ru ? "Переустановка..." : "Reinstalling...";
    public static string ReinstallingService => Ru ? "Переустановка службы..." : "Reinstalling service...";

    // ── Autostart / Service ─────────────────────────────────────────────────
    public static string AutostartWindows => Ru ? "Автозапуск с Windows" : "Autostart with Windows";
    public static string RestartService => Ru ? "\u21bb  Перезапустить службу" : "\u21bb  Restart Service";
    public static string ReinstallService => Ru ? "\u21bb  Переустановить службу" : "\u21bb  Reinstall Service";

    // ── Server list columns ─────────────────────────────────────────────────
    public static string ColRole => Ru ? "Роль" : "Role";
    public static string ColName => Ru ? "Имя" : "Name";
    public static string ColServer => Ru ? "Сервер" : "Server";
    public static string ColPort => Ru ? "Порт" : "Port";
    public static string ColSecurity => Ru ? "Защита" : "Security";
    public static string ColProtocols => Ru ? "Протоколы" : "Protocols";

    // ── Server roles / hints ────────────────────────────────────────────────
    public static string RoleTcp => "\u2605 TCP";
    public static string RoleUdp => "\u2605 UDP";
    public static string RolePrimary => Ru ? "\u2605 Основной" : "\u2605 Primary";
    public static string RoleFallback(int i) => Ru ? $"Резерв {i}" : $"Fallback {i}";
    public static string NoName => Ru ? "(без имени)" : "(no name)";
    public static string TcpUdpHint => Ru
        ? "\u2139 TCP/UDP разделение \u2014 TCP серверы для браузинга/чата, UDP для голоса/видео"
        : "\u2139 TCP/UDP split active \u2014 TCP servers handle browsing/chat, UDP servers handle voice/video";
    public static string NoFlowHint => Ru
        ? "\u26a0 Все серверы без flow \u2014 добавьте сервер с xtls-rprx-vision для оптимизации TCP"
        : "\u26a0 All servers without flow \u2014 add a server with xtls-rprx-vision for TCP optimization";
    public static string VlessHint => Ru
        ? "Двойной клик \u2014 сделать основным сервером."
        : "Double-click to set as primary server.";
    public static string CustomConfigHint => Ru
        ? "Двойной клик для выбора активного конфига. Любой протокол."
        : "Double-click to set active config. Any protocol supported.";

    // ── Dialogs ─────────────────────────────────────────────────────────────
    public static string ConfirmRemove(string name) => Ru
        ? $"Удалить \"{name}\"?"
        : $"Remove \"{name}\"?";
    public static string FailedStartVpn => Ru ? "Не удалось запустить VPN:" : "Failed to start VPN:";
    public static string FailedApply => Ru ? "Не удалось применить изменения:" : "Failed to apply changes:";
    public static string FailedParseVless => Ru ? "Ошибка разбора VLESS URI:" : "Failed to parse VLESS URI:";
    public static string NoValidVless => Ru ? "Не найдено VLESS URI." : "No valid VLESS URIs found.";
    public static string AddedSkipped(int added, int skipped) => Ru
        ? $"Добавлено {added} сервер(ов), пропущено {skipped} дубликат(ов)."
        : $"Added {added} server(s), skipped {skipped} duplicate(s).";
    public static string ConfigExists(string name) => Ru
        ? $"Конфиг '{name}' уже существует."
        : $"Config '{name}' already exists.";
    public static string InvalidConfig => Ru ? "Некорректный конфиг:" : "Invalid config:";
    public static string SelectSingBoxConfig => Ru ? "Выберите sing-box JSON конфиг" : "Select sing-box JSON config";
    public static string AddServerFirst => Ru
        ? "Сначала добавьте хотя бы один VLESS сервер."
        : "Add at least one VLESS server first.";
    public static string SelectAppGroup => Ru
        ? "Выберите хотя бы одну группу приложений."
        : "Select at least one application group.";
    public static string ServiceExeNotFound => Ru
        ? "VPNRouter.Service.exe не найден.\nАвтозапуск требует файл службы."
        : "VPNRouter.Service.exe not found.\nAutostart requires the service binary.";
    public static string ServiceExeNotFoundReinstall => Ru
        ? "VPNRouter.Service.exe не найден.\nНевозможно переустановить службу."
        : "VPNRouter.Service.exe not found.\nCannot reinstall service.";
    public static string FailedSetupService => Ru ? "Ошибка настройки службы:" : "Failed to setup service:";
    public static string FailedRestartService => Ru ? "Ошибка перезапуска службы:" : "Failed to restart service:";
    public static string ServiceReinstallFailed => Ru ? "Ошибка переустановки службы:" : "Service reinstall failed:";
    public static string ServiceReinstalled => Ru
        ? "Служба переустановлена и запущена."
        : "Service reinstalled and started successfully.";
    public static string ReinstallConfirm => Ru
        ? "Это остановит, удалит и переустановит службу из текущего файла.\n\n" +
          "Используйте после обновления VPNRouter.\n\nПродолжить?"
        : "This will stop the service, uninstall it, and reinstall from the current binary.\n\n" +
          "Use this after updating VPNRouter to apply the new service executable.\n\nContinue?";

    // ── Update ──────────────────────────────────────────────────────────────
    public static string UpdateAvailable(string type, string version, string size) => Ru
        ? $"{type}: v{version}{size}"
        : $"{type} available: v{version}{size}";
    public static string UpdateTypeLite => Ru ? "Лёгкое обновление" : "Lite update";
    public static string UpdateTypeFull => Ru ? "Обновление" : "Update";
    public static string UpdateConfirm(string version) => Ru
        ? $"Обновить до v{version}?"
        : $"Update to v{version}?";
    public static string UpdateVpnWillStop => Ru
        ? "\nVPN будет остановлен перед обновлением."
        : "\nVPN will be stopped before applying the update.";
    public static string UpdateServiceWillRemove => Ru
        ? "\nСлужба автозапуска будет удалена и может быть включена после обновления."
        : "\nAutostart service will be removed and can be re-enabled after update.";
    public static string UpdateWillRestart => Ru
        ? "\nПриложение перезапустится автоматически."
        : "\nThe application will restart automatically.";
    public static string UpdateFailed => Ru ? "Ошибка обновления:" : "Update failed:";
    public static string StoppingVpn => Ru ? "Остановка VPN..." : "Stopping VPN...";
    public static string StoppingService => Ru ? "Остановка службы..." : "Stopping service...";
    public static string RemovingOldService => Ru ? "Удаление старой службы..." : "Removing old service...";
    public static string ApplyingUpdate => Ru ? "Применение обновления..." : "Applying update...";

    // ── Mode labels ──────────────────────────────────────────────────────────
    public static string ModeCustom => "Custom";
    public static string ModeVless => "VLESS";
    public static string ModeSplit => "Split";
    public static string ModeFull => "Full";

    // ── Tray ────────────────────────────────────────────────────────────────
    public static string TrayStart => Ru ? "\u25b6 Запустить VPN" : "\u25b6 Start VPN";
    public static string TrayStop => Ru ? "\u2b1b Остановить VPN" : "\u2b1b Stop VPN";
    public static string TrayNotRunning => Ru ? "Не запущено" : "Not running";
    public static string TraySettings => Ru ? "Настройки..." : "Settings...";
    public static string TrayExit => Ru ? "Выход" : "Exit";
    public static string TrayVpnStarted => Ru ? "VPN запущен" : "VPN started";
    public static string TrayRunningService => Ru
        ? "Работает как служба Windows (автозапуск)"
        : "Running as Windows Service (autostart)";
    public static string TrayRunning(string profile) => Ru
        ? $"Работает \u2014 {profile}"
        : $"Running \u2014 {profile}";
    public static string TrayStopAndExit => Ru
        ? "VPN запущен. Остановить и выйти?"
        : "VPN is running. Stop and exit?";
}
