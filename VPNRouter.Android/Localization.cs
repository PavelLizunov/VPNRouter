using System;
using System.Globalization;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 1.H (2026-05-04) — bilingual UI strings (RU/EN), mirrors
/// the desktop <c>VPNRouter.App.Localization.Strings</c> pattern. Static
/// getters branch on the <see cref="Ru"/> flag.
///
/// <para>Initial language is resolved in this priority order:
/// <list type="number">
///   <item>Explicit user choice persisted via
///   <see cref="AndroidStorage.GetLanguage"/> (last toggle).</item>
///   <item>System Locale (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
///   — "ru" → Ru=true, anything else → English.</item>
///   <item>Fallback: English.</item>
/// </list></para>
///
/// <para><see cref="ToggleAndPersist"/> flips Ru and writes the choice
/// back to SharedPreferences so the next app launch starts in the same
/// language. Phase 2 will swap to a fully reactive scheme (INotifyPropertyChanged
/// on a singleton VM); for Phase 1.H the AndroidApp manually re-reads
/// each label after toggle.</para>
/// </summary>
internal static class Localization
{
    public static bool Ru { get; private set; }

    public static void LoadFromStorage()
    {
        var stored = AndroidStorage.GetLanguage();
        if (string.Equals(stored, "ru", StringComparison.OrdinalIgnoreCase))
        {
            Ru = true;
            return;
        }
        if (string.Equals(stored, "en", StringComparison.OrdinalIgnoreCase))
        {
            Ru = false;
            return;
        }

        // No explicit choice — guess from system Locale.
        try
        {
            var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Ru = string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            Ru = false;
        }
    }

    public static void ToggleAndPersist()
    {
        Ru = !Ru;
        AndroidStorage.SetLanguage(Ru ? "ru" : "en");
    }

    // ── Header ─────────────────────────────────────────────────────────

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

    // ── Phase 4: sub-header parity with desktop ────────────────────────

    public static string BrandTitle => Ru ? "Virtual Penguin Network" : "Virtual Penguin Network";

    public static string MenuLanguageLabel => Ru ? "Язык: Русский" : "Language: English";
    public static string MenuThemeLabel => Ru ? "Тема: переключить" : "Theme: toggle";

    // ── Status / Connect button ─────────────────────────────────────────

    public static string StatusConnected => Ru ? "Подключено" : "Connected";
    public static string StatusDisconnected => Ru ? "Отключено" : "Disconnected";
    public static string ButtonConnect => Ru ? "Подключить" : "Connect";
    public static string ButtonDisconnect => Ru ? "Отключить" : "Disconnect";
    public static string ButtonConnecting => Ru ? "Подключение…" : "Connecting…";

    // ── SimplePage parity (Phase 3) ────────────────────────────────────

    public static string SimpleStatusTitleOn => Ru ? "Подключено" : "Connected";
    public static string SimpleStatusTitleOff => Ru ? "Не подключено" : "Not connected";
    public static string SimpleStatusDescOn => Ru
        ? "Трафик идёт через VPN-туннель."
        : "Traffic is routed through the VPN tunnel.";
    public static string SimpleStatusDescOff => Ru
        ? "Трафик идёт напрямую — выбери конфиг и запусти туннель."
        : "Traffic goes direct — pick a config and start the tunnel.";

    public static string SmpConfigRowLabel => Ru ? "Конфиг · Режим" : "Config · Mode";
    public static string SmpSourceManual => Ru ? "вручную" : "manual";
    public static string SmpSourceSubscription => Ru ? "подписка" : "subscription";
    public static string SmpInputLabel => Ru ? "Конфиг VPN" : "VPN Config";
    public static string SmpInputWatermark => Ru
        ? "vless://… или https://…/sub"
        : "vless://… or https://…/sub";
    public static string SmpInputHint => Ru
        ? "Приму vless://-ссылку или URL подписки (http/https)."
        : "Accepts a vless:// share-link or subscription URL (http/https).";
    public static string SmpTunnelModeLabel => Ru ? "Что идёт через VPN" : "What goes via VPN";
    public static string SmpSplitOption => Ru ? "Выбранные приложения" : "Selected apps";
    public static string SmpSplitHint => Ru
        ? "По списку выбранных приложений (расширенные настройки)"
        : "By selected apps list (advanced settings)";
    public static string SmpFullOption => Ru ? "Весь трафик" : "All traffic";
    public static string SmpFullHint => Ru
        ? "Включая игры и банки"
        : "Including games and banks";
    public static string SmpAdvCardTitle => Ru ? "Расширенные настройки" : "Advanced settings";
    public static string SmpAdvCardSubtitle => Ru
        ? "Серверы · Подписки · Маршрутизация · Логи"
        : "Servers · Subscriptions · Routing · Logs";

    public static string SimpleConfigSummary => Ru ? "вручную · полный" : "manual · full";

    // ── Server input ────────────────────────────────────────────────────

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

    // ── Server list ─────────────────────────────────────────────────────

    public static string AvailableServers => Ru ? "Доступные серверы" : "Available servers";
    public static string ServerSelected => Ru
        ? "Выбран: {0} ({1}:{2})"
        : "Selected: {0} ({1}:{2})";

    // ── QR scan (Phase 2.4 placeholder) ────────────────────────────────

    public static string QrComingSoon => Ru
        ? "QR-сканер появится в следующем апдейте — пока вставляй URI вручную."
        : "QR scanner is coming in the next update — paste the URI manually for now.";

    // ── Bottom hint ─────────────────────────────────────────────────────

    public static string HintTunnel => Ru
        ? "Состояние туннеля повторяет иконку 🔑 в строке состояния."
        : "Tunnel state mirrors the system VPN-key icon in the status bar.";

    // ── Phase 7.2 kebab menu — full sections ────────────────────────────

    // Section headers
    public static string MenuSectionView => Ru ? "Вид" : "Appearance";
    public static string MenuSectionDiagnostics => Ru ? "Диагностика" : "Diagnostics";
    public static string MenuSectionTroubleshooting => Ru ? "Устранение неполадок" : "Troubleshooting";
    public static string MenuSectionAbout => Ru ? "О приложении" : "About";

    // Diagnostics items
    public static string MenuItemOpenLogs => Ru ? "Открыть лог" : "Open log";
    public static string MenuItemCopyLogPath => Ru ? "Скопировать путь к логу" : "Copy log path";
    public static string MenuItemUpdateCheck => Ru ? "Проверить обновления" : "Check for updates";
    public static string MenuItemUpdateComingSoon => Ru
        ? "Авто-обновление появится в следующем апдейте."
        : "Auto-update is coming in the next release.";

    // ── v2.32.0 (2026-05-07) — auto-update flow (AndroidUpdater.cs) ────
    // Mirrors VPNRouter.App/Localization/Strings.cs entries
    // (Checking / UpToDate / UpdateAvailableMessage / UpdateDownloading /
    // UpdateApplying / UpdateRestarting / UpdateFailed / CheckFailed)
    // verbatim where the desktop string fits, with Android-only additions
    // for the install-permission deep link.

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

    /// <summary>Progress label — arg: {0}=percent 0-100.</summary>
    public static string UpdateDownloading => Ru
        ? "Скачивание… {0}%"
        : "Downloading… {0}%";
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

    // About items
    public static string MenuItemVersion => Ru ? "Версия" : "Version";
    public static string MenuItemRepoLink => Ru ? "GitHub репозиторий" : "GitHub repository";

    // ── Phase 7.4 in-app log viewer (handbook §5.6) ─────────────────────

    public static string LogViewerEmpty => Ru
        ? "Лог пуст. Подключи туннель — sing-box начнёт писать сюда."
        : "Log is empty. Connect the tunnel — sing-box will start writing here.";
    public static string LogViewerError => Ru
        ? "Не удалось прочитать лог: {0}: {1}"
        : "Failed to read log: {0}: {1}";

    // ── Phase 7.5 per-app filter (handbook §5.5) ────────────────────────

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

    // ── v2.32.0 (2026-05-07) — exclude-mode UI strings (AND-5) ─────────
    // Mode toggle inside the picker overlay + form-side count label
    // suffixes that distinguish "selected apps go via VPN" (include) from
    // "selected apps bypass VPN" (exclude).

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

    // ── v2.32.0 (2026-05-07) — SubscribePage parity (AND-1) ────────────
    // Mirror desktop's VPNRouter.App/Localization/Strings.cs entries
    // (SubscriptionsSection, SubscriptionNameHint, AddSubscription,
    // RefreshAll, SubscriptionUrlHint, TipRefreshSubscription,
    // TipRemoveSubscription, LblNoServers, LblAddSubscriptionHint,
    // TipSubscriptionMetadata). RU/EN copy is verbatim from desktop so
    // bilingual users see identical wording on both platforms.
    public static string SubscriptionsSection => Ru ? "Подписки" : "Subscriptions";
    public static string SubscriptionNameHint => Ru ? "Имя" : "Name";
    public static string SubscriptionUrlHint => Ru ? "URL подписки" : "Subscription URL";
    public static string AddSubscription => Ru ? "+ Добавить" : "+ Add";
    public static string RefreshAll => Ru ? "Обновить все" : "Refresh all";
    public static string TipRefreshSubscription => Ru ? "Обновить подписку" : "Refresh subscription";
    public static string TipRemoveSubscription => Ru ? "Удалить подписку" : "Remove subscription";
    public static string TipEditSubscription => Ru ? "Изменить URL" : "Edit URL";
    public static string LblNoSubscriptions => Ru ? "Подписок нет" : "No subscriptions";
    public static string LblAddSubscriptionHint => Ru
        ? "Добавьте подписку ниже"
        : "Add a subscription below";
    public static string TipSubscriptionMetadata => Ru
        ? "URL · число серверов в последнем обновлении · когда был последний рефреш. «—» если ни разу не обновлялась."
        : "URL · server count from last refresh · time since last refresh. \"—\" means it has never been refreshed.";
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

    // ── v2.32.0 (AND-4) Per-server testing UI (drill-down from a subscription card) ──
    //
    // Mirrors desktop ServersPage labels (L_TipTestTcpTls, ServerTestButtonText,
    // L_ColPing etc.) but adapted to mobile drill-down semantics — tap card
    // → server list overlay opens, "Test all" runs concurrent TCP+TLS,
    // sort toggle reorders by latency.

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

    // ── v2.32.0 Free Configs (Android port — see plans/v2.32.0-android-free-configs.md) ──

    /// <summary>Kebab menu item that opens the Free Configs overlay.</summary>
    public static string MenuSectionFreeConfigs => Ru ? "Бесплатные конфиги" : "Free configs";
    public static string MenuItemOpenFreeConfigs => Ru ? "Найти сервер" : "Find a server";

    public static string FcOverlayTitle => Ru ? "Бесплатные конфиги" : "Free configs";
    public static string FcTabSearch => Ru ? "Поиск" : "Search";
    public static string FcTabSaved => Ru ? "★ Сохранённые" : "★ Saved";
    public static string FcTabSavedWithCount => Ru ? "★ Сохранённые ({0})" : "★ Saved ({0})";

    public static string FcSearchHint => Ru
        ? "Соберём список ниже из публичных источников и проверим TCP+TLS до каждого. Жми «Найти» — выберем самые быстрые."
        : "We'll pull the list below from public sources and run TCP+TLS to each. Tap Find — we'll pick the fastest.";
    public static string FcFindButton => Ru ? "✓✓ Найти рабочие конфиги" : "✓✓ Find working configs";
    public static string FcStopButton => Ru ? "✕ Остановить" : "✕ Stop";

    public static string FcAdvancedSettings => Ru ? "Расширенные настройки" : "Advanced settings";
    public static string FcTargetNLabel => Ru ? "Найти" : "Find";
    public static string FcConfigsWord => Ru ? "конфигов" : "configs";
    public static string FcWithPingUnder => Ru ? "с пингом до" : "with ping under";
    public static string FcMsUnit => "ms";
    public static string FcExcludeRu => Ru
        ? "Исключить серверы в России"
        : "Skip servers in Russia";

    public static string FcColCountry => Ru ? "Страна" : "Country";
    public static string FcColEndpoint => Ru ? "Endpoint" : "Endpoint";
    public static string FcColLatency => Ru ? "Пинг" : "Latency";
    public static string FcColTransport => Ru ? "Транспорт" : "Transport";
    public static string FcColStatus => Ru ? "Статус" : "Status";

    public static string FcSearchListEmptyHint => Ru
        ? "Список пуст. Нажми «Найти рабочие конфиги» выше."
        : "List is empty. Tap «Find working configs» above.";
    public static string FcSavedEmptyHint => Ru
        ? "Сохранённых конфигов пока нет. Запусти «Найти» — найденные сохранятся здесь."
        : "No saved configs yet. Run «Find» — results will be saved here.";

    public static string FcSavedClearAll => Ru ? "✕ Удалить всё" : "✕ Clear all";
    public static string FcSavedRemoveOne => "✕";

    public static string FcStatusEmpty => Ru ? "Готов к поиску." : "Ready to search.";
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
    public static string FcStatusDoneOk => Ru
        ? "Готово. Найдено {0} конфигов."
        : "Done. Found {0} working configs.";
    public static string FcStatusDoneExhausted => Ru
        ? "Список источников исчерпан. Найдено {0} из {1}."
        : "Sources exhausted. Found {0} of {1}.";
    public static string FcStatusCancelled => Ru ? "Отменено пользователем." : "Cancelled by user.";
    public static string FcStatusFailed => Ru
        ? "Ошибка: {0}"
        : "Error: {0}";

    public static string FcConnectHint => Ru
        ? "Выбери сервер в списке — кнопка «Подключить» активируется."
        : "Pick a server above — the Connect button activates.";
    public static string FcUseSelected => Ru ? "Подключить к выбранному" : "Connect to selected";
    public static string FcUsedToast => Ru
        ? "Сервер сохранён. Подключаюсь…"
        : "Server saved. Connecting…";

    // ── v2.32.0 Settings overlay (mirrors desktop NetworkPage 1:1) (AND-2) ──────
    //
    // Strings copied verbatim from VPNRouter.App/Localization/Strings.cs
    // so every label the desktop NetworkPage shows is reproduced on
    // Android — no paraphrasing, no Android-only renaming. When the
    // desktop string changes, the Android one must follow (handbook §1.1).

    public static string MenuItemSettings => Ru ? "Настройки" : "Settings";
    public static string SettingsTitle => Ru ? "Настройки" : "Settings";

    // Sub-section headers (Strings.SectionRouting / SectionLeakProtection /
    // SectionUpdates / AutostartSection in desktop)
    public static string SettingsSectionRouting => Ru ? "Маршрутизация" : "Routing";
    public static string SettingsSectionLeak => Ru ? "Защита от утечек" : "Leak Protection";
    public static string SettingsSectionUpdates => Ru ? "Обновления" : "Updates";
    public static string SettingsSectionAutostart => Ru ? "Автозапуск" : "Autostart";

    // Routing — RoutingDescription / SplitTunnelTitle+Subtitle /
    // FullTunnelTitle+Subtitle / BypassRussianTrafficLabel+Hint
    public static string RoutingDescription => Ru
        ? "Определяет, какой трафик пойдёт через VPN."
        : "Determines which traffic goes through the VPN.";
    public static string SplitTunnelTitle => Ru ? "Раздельный туннель" : "Split Tunnel";
    public static string SplitTunnelSubtitle => Ru
        ? "Только выбранные приложения. Остальное идёт напрямую."
        : "Only selected apps. Everything else goes direct.";
    public static string FullTunnelTitle => Ru ? "Полный туннель" : "Full Tunnel";
    public static string FullTunnelSubtitle => Ru
        ? "Весь трафик ОС через VPN, включая игры и банки."
        : "All OS traffic through VPN — games and banks included.";
    public static string BypassRussianTrafficLabel => Ru
        ? "Российский трафик через реальный IP"
        : "Russian traffic via real IP";
    public static string BypassRussianTrafficHint => Ru
        ? "Сайты и приложения с российскими доменами/IP идут напрямую, минуя VPN. Защищает VPN-сервер от блокировок российскими сервисами."
        : "Russian domains and IPs go directly, bypassing VPN. Protects the VPN server from being blocked by Russian services.";

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

    // Autostart — Section A "boot" + Section B "login" mirroring desktop
    public static string AutostartBootSectionTitle => Ru
        ? "На старте Android (до разблокировки)"
        : "At Android startup (before unlock)";
    public static string AutostartBootSectionSub => Ru
        ? "На Android требуется приёмник BOOT_COMPLETED + фоновый Service. Пока не реализовано — флаги сохраняются, но не запускают туннель сами."
        : "On Android this needs a BOOT_COMPLETED receiver + a background Service. Not implemented yet — flags persist but won't start the tunnel by themselves.";
    public static string AutostartLabelVpn => Ru
        ? "Запускать VPN при старте системы"
        : "Start VPN on system boot";
    public static string AutostartLabelZapret => Ru
        ? "Запускать Zapret при старте системы"
        : "Start Zapret on system boot";
    public static string AutostartLabelTgProxy => Ru
        ? "Запускать TgProxy при старте системы"
        : "Start TgProxy on system boot";

    // DBG-3 status badges (mirror desktop's ComputeAutostartStatus output)
    public static string AutostartStatusBoot => Ru
        ? "✓ Через службу Android (на старте ОС)"
        : "✓ Via Android Service (at boot)";
    public static string AutostartStatusLoginFallback => Ru
        ? "⚠ Без службы — сработает после открытия приложения"
        : "⚠ No Service — will fire after the App opens";
    public static string AutostartStatusNoBoot => Ru
        ? "⛔ Не сработает: нужен BOOT_COMPLETED + Service"
        : "⛔ Will not fire: needs BOOT_COMPLETED + Service";
    public static string AutostartZapretNotPorted => Ru
        ? "⛔ Zapret пока не портирован на Android"
        : "⛔ Zapret is not ported to Android yet";
    public static string AutostartTgProxyNotPorted => Ru
        ? "⛔ TgProxy пока не портирован на Android"
        : "⛔ TgProxy is not ported to Android yet";
}
