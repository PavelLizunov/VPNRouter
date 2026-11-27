namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string SmpToggleToAdvanced => Ru ? "Расширенный ▸" : "Advanced ▸";
    public static string SmpToggleToSimple   => Ru ? "◂ Простой"     : "◂ Simple";
    public static string SmpToggleTooltip => Ru
        ? "Переключить между упрощённым и полным интерфейсом"
        : "Switch between Simple and Advanced UI";

    public static string SmpPlaceholderTitle => Ru
        ? "Упрощённый интерфейс"
        : "Simple mode";
    public static string SmpPlaceholderBody => Ru
        ? "Вставьте ссылку на сервер или подписку и нажмите «Подключить», либо перейдите в расширенный интерфейс."
        : "Paste a server or subscription link and press Connect, or switch to the full Advanced UI.";
    public static string SmpPlaceholderSwitchToAdvanced => Ru
        ? "Переключить на Advanced"
        : "Switch to Advanced";

    public static string SmpInputLabel => Ru ? "Ссылка подписки или сервера" : "Subscription or server link";
    public static string SmpInputWatermark => Ru
        ? "vless:// / trusttunnel:// / mieru:// или https://..."
        : "vless:// / trusttunnel:// / mieru:// or https://...";
    public static string SmpInputHint => Ru
        ? "Вставьте ссылку, которую получили от провайдера."
        : "Paste the link provided by your VPN provider.";
    public static string SmpInvalidServerLink => Ru ? "Некорректная ссылка на сервер. Проверьте её формат." : "Invalid server link. Check its format.";
    public static string SmpDnsttCoreRequired => Ru ? "Для DNSTT нужна совместимая версия ядра sing-box-vpnctl." : "DNSTT requires a compatible sing-box-vpnctl core.";
    public static string SmpTurnRelayChainRequired => Ru
        ? "TurnRelay — это промежуточное звено. Вставьте его вместе со ссылкой VLESS-выхода (две строки)."
        : "TurnRelay is a chain entry. Paste it together with its VLESS exit link (two lines).";
    public static string SmpTunnelModeLabel => Ru ? "Что направлять через VPN" : "Route through VPN";
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
        ? "Трафик направляется по правилам приложений."
        : "App rules determine which traffic uses the VPN.";
    public static string SmpTipFull => Ru
        ? "VPN используется по умолчанию. Настроенные исключения действуют."
        : "Traffic uses the VPN by default. Configured exceptions still apply.";
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
        ? "QR не содержит ссылку на сервер или подписку"
        : "QR doesn't contain a server link or subscription URL";
    public static string SmpQrNaiveUnsupportedAndroid => Ru
        ? "NaiveProxy больше не поддерживается в этой сборке"
        : "NaiveProxy is no longer supported in this build";

    public static string PlaceholderCredentialRejected => Ru
        ? "Эта ссылка содержит шаблонные ключи Reality — настоящего VPN-сервера в ней нет. Получите рабочую ссылку у вашего провайдера."
        : "This link contains placeholder Reality credentials — no real VPN server behind it. Get a working server link from your provider.";
    public static string PlaceholderSubscriptionDropped => Ru
        ? "Подписка вернула {0} шаблонных серверов — они пропущены. Если повторится — сообщите провайдеру."
        : "Subscription returned {0} placeholder servers — skipped. Report to your provider if this keeps happening.";
    public static string PlaceholderPruneBanner => Ru
        ? "Убрано {0} небезопасных серверов из конфига (шаблонные ключи Reality, с которыми VPN не работает)."
        : "Removed {0} unsafe servers from your config (placeholder Reality keys — VPN couldn't work with them).";
    public static string PlaceholderPruneBannerAllGone => Ru
        ? "Все сохранённые серверы оказались шаблонными. Добавьте рабочий сервер или подписку, чтобы продолжить."
        : "All saved servers were placeholders. Add a working server or subscription to continue.";

    public static string SmpStatusProtected    => Ru ? "Подключено"     : "Connected";
    public static string SmpStatusConnecting   => Ru ? "Подключение…"   : "Connecting…";
    public static string SmpStatusNotConnected => Ru ? "Не подключено"  : "Not connected";

    public static string SmpStatusConnectedVia      => Ru ? "через" : "via";
    public static string SmpStatusConnectedNoDetails=> Ru ? "Туннель активен." : "Tunnel is active.";
    public static string SmpServerSwitchedNote(string previous) => Ru
        ? $"«{previous}» не отвечал, выбран самый быстрый из рабочих."
        : $"'{previous}' was not responding; picked the fastest working server.";
    public static string SmpPickServerLabel         => Ru ? "Сервер" : "Server";
    public static string SmpSearchServers => Ru ? "Поиск серверов" : "Server search";
    public static string SmpSearchLabel => Ru ? "Поиск по списку" : "Search the list";
    public static string SmpSearchHint => Ru ? "Название, страна или город" : "Name, country or city";
    public static string SmpSearchLooksLikeLink => Ru ? "Похоже на ссылку VPN." : "This looks like a VPN link.";
    public static string SmpMoveLink => Ru ? "Перенести в поле ссылки" : "Move to link field";
    public static string SmpNoMatchingServers       => Ru ? "Серверы не найдены" : "No matching servers";
    public static string SmpDailyCategory => Ru ? "Повседневный" : "Everyday";
    internal static string ServerAllCategories => Ru ? "Все категории" : "All categories";
    internal static string ServerCategoryFilter => Ru ? "Категория серверов" : "Server category";
    public static string SmpAutoInCategory => Ru ? "Авто" : "Auto";
    public static string SmpPinnedServer => Ru ? "Вручную" : "Manual";
    public static string SmpExpandCategory => Ru ? "Показать серверы" : "Show servers";
    public static string SmpCollapseCategory => Ru ? "Скрыть серверы" : "Hide servers";
    public static string SmpSelectCategory => Ru ? "Автоподбор в категории" : "Auto-select in category";
    public static string SmpPinnedServerUnreachable => Ru
        ? "Закреплённый сервер не отвечает. Можно повторить подключение или выбрать другой."
        : "The pinned server is not responding. Retry the connection or choose another server.";
    public static string SmpManualOnlyCategory => Ru
        ? "Автоподбор недоступен — нажмите, чтобы выбрать сервер вручную"
        : "Auto-select unavailable — click to pick a server manually";
    public static string SmpServerCountryByIp(string code) => Ru
        ? $"{code} · Страна по IP (DB-IP)"
        : $"{code} · Country by IP (DB-IP)";
    public static string SmpServerUnsupported => Ru ? "Недоступно в этой сборке" : "Unsupported in this build";
    public static string SmpSelectionUnavailableTitle => Ru ? "Выбор недоступен" : "Selection unavailable";
    public static string SmpNoOtherInCategory => Ru
        ? "В этой категории больше нет доступных серверов."
        : "There are no other available servers in this category.";
    public static string SmpNoVerifiedServerInCategory => Ru
        ? "Не удалось подтвердить работу серверов в этой категории."
        : "Could not verify any servers in this category.";
    public static string SmpSelectionUnavailable => Ru
        ? "Выбранный сервер или категория недоступны. Выберите другой вариант."
        : "The selected server or category is unavailable. Choose another option.";
    public static string SmpCurrentServer           => Ru ? "Активен" : "Active";
    public static string SmpSubscriptionLink => Ru ? "Изменить ссылку" : "Change link";
    public static string SmpDirectLink => Ru ? "Изменить ссылку" : "Change link";
    public static string SmpSaveLink => Ru ? "Сохранить ссылку" : "Save link";
    public static string SmpLinkInputInvalid => Ru ? "Вставьте действительную ссылку подписки или сервера." : "Enter a valid subscription or server link.";
    public static string SmpLinkSaved => Ru ? "Ссылка сохранена для следующего подключения." : "Link saved for the next connection.";
    public static string SmpLinkSaving => Ru ? "Сохраняем и загружаем список…" : "Saving and loading list…";
    public static string SmpLinkSaveBusy => Ru ? "Дождитесь завершения подключения или применения настроек." : "Wait for the connection or settings change to finish.";
    public static string SmpLinkSaveStale => Ru ? "Состояние VPN изменилось. Повторите сохранение." : "The VPN state changed. Save the link again.";
    public static string SmpCheckServers => Ru ? "Проверить серверы" : "Test servers";
    public static string SmpApplicationRules        => Ru ? "Настроить приложения" : "Configure app rules";
    public static string SmpPickServerHint          => Ru
        ? "Подключение берёт последний выбранный сервер, если он отвечает, иначе самый быстрый из рабочих."
        : "Connect uses the server you picked last if it answers, otherwise the fastest working one.";
    public static string SmpCheckPing => Ru ? "Проверить пинг" : "Check ping";
    public static string SmpRefreshSubscriptions => Ru ? "Обновить список серверов" : "Refresh server list";
    internal static string SmpRefreshUpdated(string count, string time) => Ru
        ? $"Список обновлён · {count} · {time}"
        : $"List updated · {count} · {time}";
    internal static string SmpRefreshUpdatedConnectedUnchanged(string count) => Ru
        ? $"Список обновлён ({count}). Текущее подключение не изменилось."
        : $"List updated ({count}). Your active connection stayed unchanged.";
    internal static string SmpRefreshUpdatedConnectedReconnecting => Ru
        ? "Список обновлён. Переподключаем активный сервер…"
        : "List updated. Reconnecting the active server…";
    internal static string SmpRefreshUpdatedConnectedService => Ru
        ? "Список обновлён. Изменения активного сервера вступят в силу после «Применить» или переподключения."
        : "List updated. Active server changes take effect after Apply or reconnect.";
    internal static string SmpRefreshPartialFailure(int ok, int total, string failedNames) => Ru
        ? $"Обновлено {ok} из {total}. Не удалось обновить «{failedNames}» — сохранённый список доступен."
        : $"Updated {ok} of {total}. Couldn't refresh “{failedNames}” — saved servers are still available.";
    internal static string SmpRefreshFailedCached(string count) => Ru
        ? $"Не удалось обновить список. Сохранённые серверы ({count}) по-прежнему доступны."
        : $"Couldn't refresh the list. Saved servers ({count}) are still available.";
    internal static string SmpRefreshFailedEmpty => Ru
        ? "Не удалось загрузить серверы. Проверьте сеть или ссылку и повторите."
        : "Couldn't load servers. Check your network or link and try again.";
    internal static string SmpRefreshChainPreserved => Ru
        ? "Прежний список сохранён: активная цепочка отсутствует в обновлённой подписке."
        : "Kept the previous list: the active server chain is missing in the updated subscription.";
    internal static string SmpRefreshBlockedConnecting => Ru
        ? "Обновление недоступно, пока идёт подбор сервера. Дождитесь завершения или нажмите «Отмена»."
        : "Refresh is unavailable while selecting a server. Wait for it to finish or press Cancel.";
    internal static string SmpRefreshBlockedApplying => Ru
        ? "Обновление недоступно, пока применяются настройки."
        : "Refresh is unavailable while settings are being applied.";
    internal static string SmpRefreshBlockedTesting => Ru
        ? "Обновление недоступно во время проверки серверов. Дождитесь завершения или остановите проверку."
        : "Refresh is unavailable while testing servers. Wait for it to finish or stop the test.";
    internal static string SmpEmptySubTitle => Ru ? "Подписка без серверов" : "Subscription has no servers";
    internal static string SmpEmptySubHint => Ru ? "Нажмите «Обновить список серверов»" : "Press “Refresh server list”";
    internal static string SmpLinkSavedLoaded(string count) => Ru
        ? $"Ссылка сохранена · загружено: {count}."
        : $"Link saved · loaded: {count}.";
    internal static string SmpLinkSavedConnected(string count) => Ru
        ? $"Ссылка сохранена ({count}). Вступит в силу при следующем подключении."
        : $"Link saved ({count}). Takes effect on the next connection.";
    internal static string SmpLinkAlreadySaved => Ru
        ? "Эта ссылка уже сохранена. Чтобы загрузить изменения, нажмите «Обновить список серверов»."
        : "This link is already saved. Press “Refresh server list” to fetch changes.";
    internal static string SmpTurnRelayBadge => Ru ? "Через TurnRelay" : "Via TurnRelay";
    internal static string SmpTurnRelayTooltip => Ru
        ? "Подключение идёт через промежуточный узел TurnRelay. Выбирается вручную; быстрый пинг и автоподбор недоступны."
        : "Connects through a TurnRelay hop. Select manually; quick ping and auto-selection are not supported.";
    internal static string SmpTurnRelayOnlyHopsTitle => Ru ? "Нет выходных серверов" : "No selectable exit servers";
    internal static string SmpTurnRelayOnlyHops => Ru
        ? "В подписке нет выходных серверов — получены только служебные узлы TurnRelay. Обновите подписку или обратитесь к провайдеру."
        : "No selectable exit servers in this subscription — only TurnRelay hops were received. Refresh or contact your provider.";
    internal static string SmpTurnRelayMissingHop => Ru
        ? "Нет связанного узла TurnRelay — выберите другой сервер или обновите подписку."
        : "Linked TurnRelay hop is missing — choose another server or refresh the subscription.";
    internal static string SmpTurnRelayCoreRequired => Ru
        ? "Для TurnRelay нужна совместимая версия ядра sing-box."
        : "TurnRelay requires a compatible sing-box core.";
    internal static string SmpApplyServiceNow => Ru ? "Применить" : "Apply";
    internal static string SmpMenuAppSection => Ru ? "Приложение" : "App";
    internal static string SmpMenuVersionHistory => Ru ? "История версий и откат…" : "Version history & rollback…";
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
        ? "Проверяем готовность туннеля…"
        : "Checking tunnel readiness…";
    public static string SmpStatusDisconnectedHint  => Ru
        ? "Трафик идёт напрямую."
        : "Traffic goes directly.";

    public static string SmpConfigRowLabel => Ru ? "Конфиг · Режим" : "Config · Mode";
    public static string SmpConfigRowChange => Ru ? "Изменить" : "Change";
    public static string SmpConfigRowChoose => Ru ? "Выбрать" : "Choose";
    public static string SmpConfigRowHide   => Ru ? "Свернуть" : "Hide";
    internal static string SmpActiveMode(string mode) => Ru ? $"Сейчас · {mode}" : $"Active · {mode}";
    internal static string SmpPendingConfig(string config) => Ru ? $"Ожидает применения · {config}" : $"Pending · {config}";
    internal static string SmpModeUnknown => Ru ? "Действующий режим неизвестен" : "Active mode unavailable";
    public static string SmpCfgSubscribe   => Ru ? "подписка"       : "subscribe";
    public static string SmpCfgManual      => Ru ? "вручную"        : "manual";
    public static string SmpCfgCustom      => Ru ? "свой"           : "custom";
    public static string SmpCfgSplit       => Ru ? "по приложениям" : "by app";
    public static string SmpCfgFull        => Ru ? "полный"         : "full";

    public static string SmpHeroAddConfigTitle => Ru ? "Добавьте ссылку VPN" : "Add a VPN link";
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
