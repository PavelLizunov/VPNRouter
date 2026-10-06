namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string SmpToggleToAdvanced => Ru ? "Расширенный ▸" : "Advanced ▸";
    public static string SmpToggleToSimple   => Ru ? "◂ Простой"     : "◂ Simple";
    public static string SmpToggleTooltip => Ru
        ? "Переключить между упрощённым и полным интерфейсом"
        : "Switch between Simple and Advanced UI";

    public static string SmpPlaceholderTitle => Ru
        ? "Упрощённый интерфейс скоро появится"
        : "Simple mode is on the way";
    public static string SmpPlaceholderBody => Ru
        ? "В v2.17 будет одностраничный онбординг: вставил конфиг или ссылку, нажал Start — готово. А пока переключайся в полный интерфейс."
        : "v2.17 will bring a one-page onboarding: paste a config or subscription URL, hit Start, done. Switch to the full Advanced UI for now.";
    public static string SmpPlaceholderSwitchToAdvanced => Ru
        ? "Переключить на Advanced"
        : "Switch to Advanced";

    public static string SmpInputLabel => Ru ? "Конфиг VPN" : "VPN config";
    public static string SmpInputWatermark => Ru
        ? "vless:// / naive://... или https://..."
        : "vless:// / naive://... or https://...";
    public static string SmpInputHint => Ru
        ? "Приму ссылку сервера (vless / hysteria2 / tuic / ss / naive) или URL подписки (http/https)."
        : "Accepts a server link (vless / hysteria2 / tuic / ss / naive) or a subscription URL (http/https).";
    public static string SmpTunnelModeLabel => Ru ? "Что идёт через VPN" : "Route through VPN";
    public static string SmpSplitOption => Ru
        ? "Выбранные приложения"
        : "Selected apps";
    public static string SmpSplitHint => Ru
        ? "По списку выбранных приложений"
        : "Based on your selected apps";
    public static string SmpFullOption => Ru ? "Весь трафик" : "All traffic";
    public static string SmpFullHint => Ru
        ? "Включая игры и банки"
        : "Includes games and banking";
    public static string SmpAdvancedLink => Ru ? "Расширенные настройки ▸" : "Advanced settings ▸";
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
    public static string SmpActiveThrough => Ru ? "Через:" : "Through:";

    public static string SmpInputDetectedServer        => Ru
        ? "Распознано: ссылка на сервер"
        : "Detected: server link";
    public static string SmpInputDetectedSubscription  => Ru
        ? "Распознано: URL подписки"
        : "Detected: subscription URL";
    public static string SmpSavedAsServerToast       => Ru
        ? "Сохранено как сервер"
        : "Saved as server";
    public static string SmpSavedAsSubscriptionToast => Ru
        ? "Сохранено как подписка"
        : "Saved as subscription";
    public static string SmpRefreshDoneToast         => Ru
        ? "Подписка обновлена"
        : "Subscription refreshed";

    public static string SmpScanQrButton => Ru ? "Сканировать QR" : "Scan QR";
    public static string SmpQrPermissionDenied => Ru
        ? "Камера недоступна — разрешите доступ в настройках Android"
        : "Camera permission denied — grant in Android Settings";
    public static string SmpQrNotRecognized => Ru
        ? "QR-код не распознан. Повторите сканирование."
        : "QR not recognized, try again";
    public static string SmpQrScannedToast => Ru ? "QR распознан" : "QR recognized";

    public static string SmpQrConnecting => Ru
        ? "QR распознан, подключаюсь…"
        : "QR recognized, connecting…";
    public static string SmpQrSubscriptionFetching => Ru
        ? "Загружаю подписку…"
        : "Fetching subscription…";
    public static string SmpQrSubscriptionEmpty => Ru
        ? "Подписка пуста — ни одного сервера"
        : "Subscription is empty — no servers";
    public static string SmpQrSubscriptionFailed => Ru
        ? "Не удалось загрузить подписку"
        : "Failed to fetch subscription";
    public static string SmpQrUnsupportedScheme => Ru
        ? "QR не содержит vless:// или подписку"
        : "QR doesn't contain vless:// or a subscription URL";
    public static string SmpQrNaiveUnsupportedAndroid => Ru
        ? "NaiveProxy не поддерживается на Android"
        : "NaiveProxy is not supported on Android";

    public static string PlaceholderCredentialRejected => Ru
        ? "Эта ссылка содержит шаблонные ключи Reality — настоящего VPN-сервера в ней нет. Получи рабочий vless:// у своего провайдера."
        : "This link contains placeholder Reality credentials — no real VPN server behind it. Get a working vless:// from your provider.";
    public static string PlaceholderSubscriptionDropped => Ru
        ? "Подписка вернула {0} шаблонных серверов — они пропущены. Если повторится — пожалуйся провайдеру."
        : "Subscription returned {0} placeholder servers — skipped. Report to your provider if this keeps happening.";
    public static string PlaceholderPruneBanner => Ru
        ? "Обновление v2.32.3: убрано {0} небезопасных серверов из конфига (шаблонные ключи Reality, с которыми VPN не работает)."
        : "v2.32.3 upgrade: removed {0} unsafe servers from your config (placeholder Reality keys — VPN couldn't work with them).";
    public static string PlaceholderPruneBannerAllGone => Ru
        ? "Обновление v2.32.3: все сохранённые серверы оказались шаблонными. Добавь настоящий vless:// или подписку, чтобы продолжить."
        : "v2.32.3 upgrade: all saved servers were placeholders. Add a real vless:// or a subscription to continue.";

    public static string SmpStatusProtected    => Ru ? "Подключено"     : "Connected";
    public static string SmpStatusConnecting   => Ru ? "Подключение…"   : "Connecting…";
    public static string SmpStatusNotConnected => Ru ? "Не подключено"  : "Not connected";

    public static string SmpStatusConnectedVia      => Ru ? "через" : "via";
    public static string SmpStatusConnectedNoDetails=> Ru ? "Туннель активен." : "Tunnel is active.";
    public static string SmpServerSwitchedNote(string previous) => Ru
        ? $"«{previous}» не отвечал, выбран самый быстрый из рабочих."
        : $"'{previous}' was not responding; picked the fastest working server.";
    public static string SmpPickServerLabel         => Ru ? "Сервер" : "Server";
    public static string SmpPickServerHint          => Ru
        ? "Подключение берёт последний выбранный сервер, если он отвечает, иначе самый быстрый из рабочих."
        : "Connect uses the server you picked last if it answers, otherwise the fastest working one.";
    public static string SmpSetupCheckTitle         => Ru ? "Проверка настроек" : "Check my setup";
    public static string SmpSetupCheckSubtitle      => Ru
        ? "Маршрутизация, MTU и аварийное отключение, шаг за шагом"
        : "Routing, MTU and the safety switch, step by step";
    public static string SmpMenuResetWindowSize     => Ru ? "Размер окна по умолчанию" : "Reset window size";
    public static string TipSmpMenuResetWindowSize  => Ru
        ? "Вернуть обычный размер окна (двойной щелчок по значку в шапке делает то же)."
        : "Restore the default window size (a double click on the icon in the header does the same).";
    public static string SmpHomeButton              => Ru ? "Главная" : "Home";
    public static string SmpHomeButtonTip           => Ru ? "Вернуться на главный экран" : "Back to the home screen";
    public static string SmpStatusApplying          => Ru ? "Применяю настройки…" : "Applying settings…";
    public static string SmpStatusApplyingHint      => Ru
        ? "Переключаю маршрутизацию — несколько секунд."
        : "Switching routing — a few seconds.";
    public static string SmpStatusConnectingHint    => Ru
        ? "Рукопожатие с сервером — пара секунд."
        : "Handshaking with the server — a moment.";
    public static string SmpStatusDisconnectedHint  => Ru
        ? "Трафик идёт напрямую — выбери конфиг и запусти туннель."
        : "Traffic goes straight — pick a config and start the tunnel.";

    public static string SmpConfigRowLabel => Ru ? "Конфиг · Режим" : "Config · Mode";
    public static string SmpConfigRowChange => Ru ? "Изменить" : "Change";
    public static string SmpConfigRowHide   => Ru ? "Свернуть" : "Hide";
    public static string SmpCfgSubscribe   => Ru ? "подписка"       : "subscribe";
    public static string SmpCfgManual      => Ru ? "вручную"        : "manual";
    public static string SmpCfgCustom      => Ru ? "свой"           : "custom";
    public static string SmpCfgSplit       => Ru ? "сплит"          : "split";
    public static string SmpCfgFull        => Ru ? "полный"         : "full";

    public static string SmpHeroAddConfigTitle => Ru ? "Добавьте конфиг VPN" : "Add a VPN config";
    public static string SmpHeroAddConfigHint  => Ru
        ? "Вставьте ссылку на сервер или URL подписки, затем подключитесь."
        : "Paste a server link or a subscription URL, then connect.";
    public static string SmpHeroErrorTitle     => Ru ? "Не удалось подключиться" : "Couldn't connect";
    public static string SmpHeroWarnTitle      => Ru ? "Проблема с подключением" : "Connection problem";
    public static string SmpCtaWait            => Ru ? "Подождите…" : "Please wait…";
    public static string SmpSegSplit           => Ru ? "Приложения"       : "Selected apps";
    public static string SmpSegFull            => Ru ? "Весь трафик"      : "All traffic";
    public static string SmpKindServer         => Ru ? "Ссылка на сервер" : "Server link";
    public static string SmpKindSubscription   => Ru ? "Подписка"         : "Subscription";
    public static string SmpKindCustom         => Ru ? "Свой конфиг"      : "Custom config";
    public static string SmpKindServers(int n) => Ru
        ? $"{n} {RuPlural(n, "сервер", "сервера", "серверов")}"
        : (n == 1 ? "1 server" : $"{n} servers");

    private static string RuPlural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    public static string SmpCtaConnect    => Ru ? "Подключить"   : "Connect";
    public static string SmpCtaDisconnect => Ru ? "Отключить"    : "Disconnect";
    public static string SmpCtaCancel     => Ru ? "Отмена"       : "Cancel";

    public static string SmpAdvCardTitle    => Ru ? "Расширенные настройки" : "Advanced settings";
    public static string SmpAdvCardSubtitle => Ru
        ? (OperatingSystem.IsAndroid()
            ? "Серверы · Подписки · Настройки · Приложения · Публичные"
            : "Серверы · Подписки · Zapret · Telegram-прокси · Публичные")
        : (OperatingSystem.IsAndroid()
            ? "Servers · Subscriptions · Settings · Applications · Public"
            : "Servers · Subscriptions · Zapret · Telegram proxy · Public");

    public static string SmpMenuTheme         => Ru ? "Тема"                   : "Theme";
    public static string SmpMenuLanguage      => Ru ? "Язык"                   : "Language";
    public static string SmpMenuOpenLogs      => Ru ? "Открыть логи"           : "Open logs";
    public static string SmpMenuCheckLeaks    => Ru ? "Проверить IP-утечку"    : "Check IP leak";
    public static string SmpMenuCheckUpdates  => Ru ? "Проверить обновления"   : "Check for updates";
    public static string SmpMenuSwitchToAdv   => Ru ? "Перейти в Advanced"     : "Switch to Advanced";
    public static string DiagSupportHeader    => Ru ? "Поддержка"              : "Support";
    public static string DiagExportButton     => Ru ? "Собрать диагностику"    : "Export diagnostics";
    public static string DiagExporting        => Ru ? "Собираю…"               : "Collecting…";
    public static string DiagExportHint       => Ru
        ? "Соберёт логи и настройки в один ZIP на рабочем столе. Пароли, ключи и токены удаляются. Проверьте архив перед отправкой."
        : "Collects your logs and settings into one ZIP on the Desktop. Passwords, keys and tokens are removed. Review the archive before sharing.";
    public static string SmpMenuHealthCheck   => Ru ? "Проверить состояние"    : "Run Health Check";
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

    public static string AutostartPlatformNotice => Ru
        ? "Автозапуск пока поддерживается только на Windows. Поддержка Linux (systemd) и macOS (launchd) появится в будущих версиях."
        : "Autostart is currently available on Windows only. Linux (systemd) and macOS (launchd) support is planned for future releases.";

    public static string SmpMenuViewSection           => Ru ? "Вид"                : "View";
    public static string SmpMenuDiagnosticsSection    => Ru ? "Диагностика"        : "Diagnostics";
    public static string SmpMenuTroubleshootingSection => Ru ? "Устранение неполадок" : "Troubleshooting";
    public static string SmpSegLight                  => Ru ? "Светлая"            : "Light";
    public static string SmpSegDark                   => Ru ? "Тёмная"             : "Dark";
    public static string SmpSegSystem                 => Ru ? "Системная"          : "System";
    public static string SmpSegRu                     => "RU";
    public static string SmpSegEn                     => "EN";

    public static string LanguageSwitching            => Ru
        ? "Переключение языка…"
        : "Switching language…";

    public static string SmpMenuAbout        => Ru ? "О приложении"              : "About";
    public static string TipSmpMenuAbout     => Ru
        ? "Информация о версии, билде и авторе."
        : "Version, build, and author information.";
    public static string AboutTitle          => Ru ? "О приложении"              : "About";
    public static string AboutBrandName      => "Virtual Penguin Network";
    public static string AboutTagline        => Ru
        ? "VPN-роутер на основе процессов, с поддержкой обхода DPI."
        : "Process-based VPN router with DPI bypass support.";
    public static string AboutVersionLabel   => Ru ? "Версия"                    : "Version";
    public static string AboutSingBoxLabel   => Ru ? "sing-box"                  : "sing-box";
    public static string AboutCreatorLabel   => Ru ? "Автор"                     : "Author";
    public static string AboutRepoLabel      => Ru ? "Репозиторий"               : "Repository";
    public static string AboutCloseBtn       => Ru ? "Закрыть"                   : "Close";
    public static string AboutVersionUnknown => Ru ? "неизвестно"                : "unknown";

}
