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
public static partial class Strings
{
    public static string Lang { get; set; } = "en";
    internal static bool Ru => Lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    /// <summary>v2.38.0 — Explorer "route through VPN" context-menu verb label
    /// (used by ShellMenuRegistrar; public so VPNRouter.App can read it since
    /// <see cref="Ru"/> is internal to Core).</summary>
    public static string ShellMenuRouteLabel =>
        Ru ? "Добавить в VPNRouter (через VPN)" : "Add to VPNRouter (route through VPN)";

    /// <summary>v2.38.0-r4 — parent label for the cascading "VPNRouter ▸"
    /// submenu shown only when the user has more than one app-category
    /// (the submenu items are the category names). Single-category users
    /// keep the flat <see cref="ShellMenuRouteLabel"/> verb (no submenu).</summary>
    public static string ShellMenuParentLabel =>
        Ru ? "Добавить в VPNRouter" : "Add to VPNRouter";

    /// <summary>v2.38.0-r5 — Explorer "remove from VPN" context-menu verb
    /// (separate flat verb alongside the Add verb; always visible — no COM
    /// conditional display — so it no-ops with a toast if the app wasn't
    /// routed).</summary>
    public static string ShellMenuUnrouteLabel =>
        Ru ? "Убрать из VPNRouter" : "Remove from VPNRouter";

    /// <summary>v2.38.0-r7 — subscription card badge when the last refresh
    /// failed but cached servers are preserved. Turns the bare "0s · —" (which
    /// reads as "configs lost / banned") into an honest "couldn't refresh,
    /// servers are still cached" signal. See Z:\surito diagnosis 2026-05-29
    /// (provider DPI-flap → fetch failed → list looked empty/lost).</summary>
    public static string SubRefreshFailedCached =>
        Ru ? "не обновилось — показаны кэшированные серверы"
           : "refresh failed — showing cached servers";

    /// <summary>v2.38.0-r7 — subscription card badge when the last refresh
    /// failed AND there are no cached servers to fall back on (provider
    /// unreachable / blocked). Distinguishes a network/block failure from
    /// genuine "empty subscription".</summary>
    public static string SubRefreshFailedEmpty =>
        Ru ? "не удалось загрузить — провайдер недоступен (проверьте сеть/Zapret)"
           : "couldn't load — provider unreachable (check network/Zapret)";

    // v2.29.0: dynamic OS name shown in user-facing autostart copy. Mac
    // users were seeing "Windows" hardcoded in Simple-mode autostart card
    // and Network → Autostart labels (reported 2026-04-29). Now Strings
    // detect runtime platform and substitute "macOS" / "Linux" / "Windows"
    // into RU+EN templates. Does NOT change Windows-Service-tech labels
    // (those reference an actual Windows-only API surface).
    public static string OsDisplayName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS() ? "macOS" :
        OperatingSystem.IsAndroid() ? "Android" : "Linux";

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
    // the back-to-Simple button in the Advanced shell header. Mobile design
    // 2026-05-11 swapped the "+ Simple" plus-prefix (read as "add Simple")
    // for "◂ Simple" — standard Android back-affordance and matches the
    // design's `.ahdr .back` style at Mobile.html line 78. Same glyph
    // works for RU and EN since it's a typographic arrow not a word.
    public static string TabAdvServers => Ru ? "Серверы" : "Servers";
    public static string TabAdvSubscribe => Ru ? "Подписка" : "Subscribe";
    public static string TabAdvSettings => Ru ? "Настройки" : "Settings";
    public static string TabAdvApplications => Ru
        ? "Приложения"
        : (OperatingSystem.IsAndroid() ? "Apps" : "Applications");
    public static string TabAdvTools => Ru ? "Инструменты" : "Tools";
    public static string TabAdvPublic => Ru ? "Публичные" : "Public";
    public static string AdvSimpleToggle => Ru ? "◂ Простой" : "◂ Simple";

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
    public static string Syncing => Ru ? "Обновление…" : "Syncing…";
    public static string SyncComplete(int count) => Ru ? $"Получено серверов: {count}" : $"Fetched servers: {count}";
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
    public static string SplitTunnel => Ru ? "Раздельный туннель (выбранные приложения)" : "Split Tunnel (selected apps)";
    public static string FullTunnel => Ru ? "Полный туннель (весь трафик)" : "Full Tunnel (all traffic)";
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

    // v2.37.0-r7 — idle/quiescent state for Zapret + TgProxy status fields.
    // Distinct from Stopping («Остановка...») which is an active transition.
    // Pre-r7 multiple sites used inline `IsRussian ? "Остановлен" : "Stopped"`
    // ternaries + string-literal field defaults that hardcoded the English
    // word, violating the bilingual-UI invariant (no English in RU UI).
    public static string Stopped => Ru ? "Остановлен" : "Stopped";

    // v2.37.0-r18 — RuntimeStatus tooltips + Subscriptions status text.
    // RuntimeStatus tooltips were inline `IsRussian ? "VPN" : "VPN"`
    // ternaries with **identical** strings in both branches — pointless
    // overhead. Translation only differs on the meaningful word in two of
    // them; VPN stays "VPN" universally.
    public static string BadgeTooltipVpn => "VPN";
    public static string BadgeTooltipZapret => Ru ? "Zapret обход DPI" : "Zapret DPI bypass";
    public static string BadgeTooltipTgProxy => Ru ? "Telegram прокси" : "Telegram proxy";
    public static string SubscriptionEnterUrl => Ru ? "Введите URL подписки" : "Enter subscription URL";
    public static string SubscriptionCleared => Ru ? "Подписка удалена" : "Subscription cleared";

    // v2.37.0-r17 — ServerTesting tab labels (Test all / Cancel /
    // Deep verify / Stop) + progress text. Pre-r17 these were inline
    // `IsRussian` ternaries in MainWindowViewModel.ServerTesting.cs
    // partial. Moving them to Strings.cs makes the localization
    // inventory greppable.
    public static string ServerTestCancel => Ru ? "Отмена" : "Cancel";
    public static string ServerTestAll => Ru ? "Проверить все" : "Test all";
    public static string ServerDeepStop => Ru ? "Остановить" : "Stop";
    public static string ServerDeepVerify => Ru ? "Глубокая проверка" : "Deep verify";
    public static string ServerTestingManual => Ru ? "Проверка Manual-серверов" : "Testing Manual servers";
    public static string ServerTestingSubscriptions => Ru ? "Проверка подписочных серверов" : "Testing subscription servers";
    public static string ServerTestNoServers => Ru ? "Нет серверов" : "No servers";
    public static string ServerTestCancelled => Ru ? "Отменено" : "Cancelled";

    // v2.38.2 (surito Bug A) — the ping probe is a plain socket from this
    // process; under an active TUN (especially full tunnel) it routes through
    // the proxy, so every server measures the SAME tunnel RTT, not its own.
    public static string PingUnavailableWhenConnected => Ru
        ? "Пинг измеряется только при отключённом VPN — через туннель он показывает RTT туннеля, а не сервера"
        : "Ping is measured only while the VPN is disconnected — through the tunnel it shows the tunnel's RTT, not the server's";
    public static string ServerDeepVerifyManual => Ru ? "Глубокая проверка Manual" : "Deep verify Manual";
    public static string ServerDeepVerifySubscription => Ru ? "Глубокая проверка подписки" : "Deep verify subscription";

    // v2.37.0-r16 — TgProxy stats labels (Active / Total prefixes).
    // ParseStatsShort returns "Active: N | Total: N | ↑bytes ↓bytes";
    // the up/down arrows are universal symbols (no localization needed)
    // but the textual prefixes were English-only. Now localized.
    public static string TgProxyStatsActive => Ru ? "Активных" : "Active";
    public static string TgProxyStatsTotal => Ru ? "Всего" : "Total";
    public static string TgProxyStopFailed => Ru
        ? "Не удалось остановить (проверьте права)"
        : "Couldn't stop (check permissions)";

    // v2.37.0-r14 — short status / toast strings still inline in MVM.
    // Sites swept: "нет value" (rule validation), "✓ Удалено все правила"
    // (toast), "Уже отсортировано" (sort toast), "Пустое значение" (form
    // validation), "Нажмите на конфиг для активации" (free-config hint).
    public static string RuleParserMissingValue => Ru ? "нет value" : "missing value";
    public static string RuleParserUnknownType(string type) => Ru
        ? $"неизвестный тип «{type}»"
        : $"unknown type «{type}»";
    public static string RulesAllDeleted => Ru ? "✓ Удалено все правила" : "✓ All rules deleted";
    public static string RulesAlreadySorted => Ru ? "Уже отсортировано" : "Already sorted";
    public static string RulesEmptyValue => Ru ? "Пустое значение" : "Empty value";
    public static string ClickToActivateConfig => Ru
        ? "Нажмите на конфиг для активации"
        : "Click a config to activate it";

    // v2.37.0-r13 — Custom Rules type-help text. Pre-r13 these lived as
    // inline IsRussian-ternaries in MainWindowViewModel.NewRuleActionHint
    // (lines 918-924) + NewRuleTypeHint (lines 930-944). Moving them into
    // Strings.cs (the canonical location for all localized text) makes the
    // inventory greppable and the call sites cleaner.
    //
    // The dispatch switch (rule type → display name) stays in the VM —
    // these are just per-type localized strings.
    public static string RuleActionHintDirect => Ru ? "напрямую (мимо VPN)" : "direct (bypass VPN)";
    public static string RuleActionHintProxy => Ru ? "через VPN-туннель" : "through the VPN tunnel";
    public static string RuleActionHintBlock => Ru ? "блокировать соединение" : "block the connection";

    public static string RuleTypeHintDomain => Ru ? "точное имя (discord.com)" : "exact match (discord.com)";
    public static string RuleTypeHintDomainSuffix => Ru ? "оканчивается на (.discord.com)" : "ends with (.discord.com)";
    public static string RuleTypeHintDomainKeyword => Ru ? "содержит (discord)" : "contains (discord)";
    public static string RuleTypeHintIpCidr => Ru ? "IPv4/IPv6 + маска (10.0.0.0/8)" : "IPv4/IPv6 + mask (10.0.0.0/8)";
    public static string RuleTypeHintPort => Ru ? "порт или список (53,853)" : "port or list (53,853)";
    public static string RuleTypeHintPortRange => Ru ? "диапазон портов (1000-2000)" : "port range (1000-2000)";
    public static string RuleTypeHintNetwork => Ru ? "tcp или udp" : "tcp or udp";
    public static string RuleTypeHintProcessName => Ru ? "имя процесса (Discord.exe)" : "process name (Discord.exe)";
    public static string RuleTypeHintProcessPath => Ru ? "полный путь к .exe" : "full .exe path";
    public static string RuleTypeHintGeosite => Ru ? "тег geosite (cn, ads, …)" : "geosite tag (cn, ads, …)";
    public static string RuleTypeHintGeoip => Ru ? "тег geoip (cn, us, private)" : "geoip tag (cn, us, private)";

    // v2.37.0-r21 — better probe progress info + direct-start path.
    // User feedback: «мало информативно что происходит при проверке,
    // нет запуска со своими настройками, чтоб если пользователь знает
    // свою стратегию ему не приходилось ждать».
    public static string ZapretProbeElapsedAndEta(int elapsedSec, int? etaSec)
    {
        var elapsed = $"{elapsedSec / 60}:{(elapsedSec % 60):D2}";
        if (etaSec.HasValue && etaSec.Value > 0)
        {
            var eta = $"{etaSec.Value / 60}:{(etaSec.Value % 60):D2}";
            return Ru ? $"Прошло {elapsed} · осталось ~{eta}" : $"Elapsed {elapsed} · ~{eta} left";
        }
        return Ru ? $"Прошло {elapsed}" : $"Elapsed {elapsed}";
    }
    public static string ZapretStartSelectedStrategyButton => Ru
        ? "Запустить с этой стратегией"
        : "Start with this strategy";
    public static string ZapretStartSelectedStrategyHint => Ru
        ? "Применит выбранную стратегию сразу, минуя авто-подбор (для пользователей которые знают свою рабочую стратегию)."
        : "Apply selected strategy immediately, skipping auto-probe (for users who know their working strategy).";
    public static string ZapretStartingSelected(string strategy) => Ru
        ? $"Запуск стратегии: {strategy}..."
        : $"Starting strategy: {strategy}...";
    public static string ZapretRunningSelected(string strategy, int pid) => Ru
        ? $"Работает [{strategy}] (PID {pid}, выбрано вручную)"
        : $"Running [{strategy}] (PID {pid}, manual)";
    public static string ZapretSelectedStrategyFailed(string strategy) => Ru
        ? $"Стратегия {strategy} не запустилась — возможно AV блокирует winws.exe или нужен другой выбор."
        : $"Strategy {strategy} failed to start — antivirus may be blocking winws.exe, or try another one.";

    // v2.37.0-r10 — Zapret probe-cache UI controls (Tools expander).
    // r6 added the cache silently; r10 surfaces user controls:
    //   - "Найти заново (без кэша)" — bypasses cache, runs full sweep
    //   - "Очистить кэш стратегий" — wipes cache file
    //   - cache-hit info string in status when warm-start was used
    public static string ZapretForceFreshProbeButton => Ru
        ? "Найти стратегию заново"
        : "Re-probe strategy";
    public static string ZapretClearCacheButton => Ru
        ? "Очистить кэш стратегий"
        : "Clear strategy cache";
    public static string ZapretCacheCleared => Ru
        ? "Кэш стратегий очищен"
        : "Strategy cache cleared";
    public static string ZapretCacheInfo(string strategy, int successCount) => Ru
        ? $"Кэш: {strategy} (успехов: {successCount})"
        : $"Cache: {strategy} (successes: {successCount})";
    public static string ZapretCacheEmpty => Ru
        ? "Кэш пуст — следующая проверка будет полной"
        : "Cache empty — next probe will be full";

    // ───── v2.37.0-r24 — Hero strategy summary card copy ─────────────────
    //
    // Strings used by the new card under "Включить обход блокировок". Two
    // header variants (fresh / stale) since the user picked the no-auto-
    // probe UX — we never silently re-run; only nudge via the badge.

    public static string ZapretSummaryHeaderFresh(string strategy) => Ru
        ? $"Стратегия «{strategy}» работает"
        : $"Strategy '{strategy}' is working";

    public static string ZapretSummaryHeaderStale(string strategy) => Ru
        ? $"Стратегия «{strategy}» устарела"
        : $"Strategy '{strategy}' is stale";

    /// <summary>
    /// "4 из 5 целей · проверено 12 мин назад"
    /// "4 of 5 targets · checked 12 min ago"
    /// </summary>
    public static string ZapretSummarySubtextWithScore(int passed, int total, string relativeTime) => Ru
        ? $"{passed} из {total} целей · проверено {relativeTime}"
        : $"{passed} of {total} targets · checked {relativeTime}";

    /// <summary>
    /// "проверено 12 мин назад" — used for legacy v1 cache entries
    /// where target score wasn't recorded.
    /// </summary>
    public static string ZapretSummarySubtextNoScore(string relativeTime) => Ru
        ? $"Проверено {relativeTime}"
        : $"Checked {relativeTime}";

    public static string ZapretReverifyButton => Ru
        ? "Перепроверить"
        : "Re-verify";

    public static string ZapretReverifyHint => Ru
        ? "Заново подберёт лучшую стратегию (полная проверка, 2-5 минут)"
        : "Picks the best strategy again (full sweep, 2-5 minutes)";

    public static string ZapretSummaryDetailsButton => Ru
        ? "Подробнее"
        : "Details";

    public static string ZapretSummaryStaleHint => Ru
        ? "Стратегия проверялась более 7 дней назад. Рекомендуем перепроверить."
        : "Strategy hasn't been checked in over 7 days. Re-verify recommended.";

    // r33: Cancel button shown during probe (Hero card).
    public static string ZapretCancelProbeButton => Ru
        ? "Отменить"
        : "Cancel";

    // Relative-time formatter outputs (used by FormatRelativeTime).
    // "только что" / "12 минут назад" / "2 часа назад" / "3 дня назад" /
    // "давно".
    public static string RelativeTimeJustNow => Ru
        ? "только что"
        : "just now";

    public static string RelativeTimeMinutes(int n) => Ru
        ? $"{n} {RuMinutesWord(n)} назад"
        : $"{n} min ago";

    public static string RelativeTimeHours(int n) => Ru
        ? $"{n} {RuHoursWord(n)} назад"
        : (n == 1 ? "1 hour ago" : $"{n} hours ago");

    public static string RelativeTimeDays(int n) => Ru
        ? $"{n} {RuDaysWord(n)} назад"
        : (n == 1 ? "1 day ago" : $"{n} days ago");

    public static string RelativeTimeLongAgo => Ru
        ? "давно"
        : "long ago";

    // Russian noun-declension helpers — Ru numerals trigger different word
    // forms for 1, 2-4, and 5+. Keep these private to Strings.cs.
    private static string RuMinutesWord(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 19) return "минут";
        if (mod10 == 1) return "минуту";
        if (mod10 is >= 2 and <= 4) return "минуты";
        return "минут";
    }

    private static string RuHoursWord(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 19) return "часов";
        if (mod10 == 1) return "час";
        if (mod10 is >= 2 and <= 4) return "часа";
        return "часов";
    }

    private static string RuDaysWord(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 19) return "дней";
        if (mod10 == 1) return "день";
        if (mod10 is >= 2 and <= 4) return "дня";
        return "дней";
    }

    // v2.37.0-r9 — Custom Rules import/export localization. Pre-r9 every
    // toast / validation message in `ImportCustomRulesAsync` +
    // `ExportCustomRulesAsync` was hardcoded English, violating the
    // bilingual-UI invariant (no English in RU UI). The feature has been live since
    // v2.30.0-r3 (2026-04-30) but the validation-error slot stayed unilingual
    // for ~25 days — RU users saw "Import failed: ..." in an otherwise-
    // Russian sub-section. r9 closes the gap.
    public static string RulesFilePickerOpenFailed => Ru
        ? "Не удалось открыть диалог выбора файлов"
        : "Could not open file picker";
    public static string RulesImportDialogTitle => Ru ? "Импорт правил" : "Import rules";
    public static string RulesExportDialogTitle => Ru ? "Экспорт правил" : "Export rules";
    public static string RulesImportFailed(string warning) => Ru
        ? $"Импорт не удался: {warning}"
        : $"Import failed: {warning}";
    public static string RulesImportNoRules => Ru
        ? "Импорт: в файле нет правил"
        : "Import: file contained no rules";
    public static string RulesImported(int count, string format) => Ru
        ? $"Импортировано правил: {count} [{format}]"
        : $"Imported {count} rule(s) [{format}]";
    public static string RulesImportWithWarnings(int count) => Ru
        ? $" — {count} предупреждение(й) (см. лог)"
        : $" — {count} warning(s) (see app log)";
    public static string RulesImportError(string err) => Ru
        ? $"Ошибка импорта: {err}"
        : $"Import error: {err}";
    public static string RulesExportNothing => Ru
        ? "Нечего экспортировать — список правил пуст"
        : "Nothing to export — rule list is empty";
    public static string RulesExported(int count, string filename) => Ru
        ? $"Экспортировано правил: {count} → {filename}"
        : $"Exported {count} rule(s) to {filename}";
    public static string RulesExportError(string err) => Ru
        ? $"Ошибка экспорта: {err}"
        : $"Export error: {err}";

    // ── Task #41 Stage 2 (PinkuDani 2026-05-21) — two-phase Start timer ──
    // Phase A diagnostic: sing-box never reported started within 60s.
    // Real hang at firewall / TUN cleanup / wintun launch.
    public static string StartTimeoutPhaseA => Ru
        ? "Таймаут запуска (60 с). Sing-box не стартовал."
        : "Start timed out (60s). sing-box never started.";

    // Phase B diagnostic: sing-box started but the TUN warmup probe never
    // confirmed reachability within 20s. wintun driver issue, network gone,
    // or warmup probe blocked by upstream firewall.
    public static string StartTimeoutPhaseB => Ru
        ? "Таймаут TUN (20 с). Запуск не завершён."
        : "TUN warm-up timed out (20s). Start incomplete.";

    // ── Server list columns ──
    public static string ColName => Ru ? "Имя" : "Name";
    public static string ColServer => Ru ? "Сервер" : "Server";
    public static string ColPort => Ru ? "Порт" : "Port";
    public static string ColSecurity => Ru ? "Защита" : "Security";
    // v2.25.3 — extra column labels for the redesigned Servers / Subscribe rows
    public static string ColIp => "IP";
    // Bug-AND-016 (2026-05-16): was unilingual EN.
    public static string ColPing => Ru ? "Пинг" : "Ping";
    // v2.30.6-r1 (UX-23/32 fix): tooltip on Ping column header — explains
    // the "—" placeholder users see before any test has been run.
    public static string ColPingTooltip => Ru
        ? "Задержка в мс. «—» означает «не запускалось» — нажмите «Проверить все»."
        : "Latency in ms. \"—\" means not measured — click \"Test all\".";

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
    //   • Amber : no service, but App has a per-component bootstrap (after
    //     DBG-2 lands the App-side bootstrap for vpn/zapret/tgproxy) → fires
    //     when the user logs into the App, not at OS boot
    //   • Red : no service AND no App-side bootstrap → the toggle does
    //     literally nothing; show the strongest hint to install the service
    public static string AutostartStatusBoot => Ru
        ? "✓ Через службу Windows (на старте ОС)"
        : "✓ Via Windows Service (at boot)";
    public static string AutostartStatusLoginFallback => Ru
        ? "Служба не установлена — сработает после входа в приложение"
        : "Service not installed — will fire after App login";
    public static string AutostartStatusNoBoot => Ru
        ? "Не сработает без службы Windows"
        : "Will not fire without the Windows service";
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
        ? "Запускает приложение VPNRouter в трей после входа в систему. VPN придётся запустить вручную."
        : "Launches VPNRouter into the tray after you sign in. VPN itself must be started manually.";

    private static string AutostartLoginAppDescriptionWindows => Ru
        ? "Запускает приложение VPNRouter после входа. VPN придётся запустить вручную или включить «на старте Windows» выше."
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
    // Bug-AND-015 (2026-05-16, manual test pass iter 23) — empty-Connect
    // error message ("No server configured…") was hardcoded EN in
    // MainActivity.cs. Surface a localized string so RU users see RU.
    public static string AndroidErrorNoServerConfigured => Ru
        ? "Сервер не настроен. Добавьте подписку или вставьте vless://-URI."
        : "No server configured. Add a subscription or paste a vless:// URI.";

    // Bug-AND-019 (2026-05-16) — long-press → tap-to-confirm delete UX
    // for user-defined custom categories on the Applications tab.
    public static string AndroidDeleteCategoryConfirm => Ru
        ? "Удалить?"
        : "Tap to delete";

    public static string SmpAutostartCardTitle => Ru
        ? "Автозапуск"
        : "Autostart";
    public static string SmpAutostartCardOn => Ru
        ? (OperatingSystem.IsAndroid()
            ? "Служба VPN активна"
            : "Служба установлена и запущена")
        : (OperatingSystem.IsAndroid()
            ? "VPN service active"
            : "Service installed and running");
    public static string SmpAutostartCardOff => Ru
        ? (OperatingSystem.IsAndroid()
            ? "Настроить автозапуск VPN при загрузке устройства"
            : $"Настроить автозапуск VPN при старте {OsDisplayName}")
        : (OperatingSystem.IsAndroid()
            ? "Configure VPN autostart on device boot"
            : $"Configure VPN autostart at {OsDisplayName} boot");
    // Generic subtitle used on Android inline card (no Service-installed/stopped
    // distinction yet — Android lifecycle differs from Windows Service).
    // 2026-05-15 fix (Bug-AND-loc-001, brat live-test on KYOCERA A101BM):
    // pre-fix this card said «при старте Windows» on Android too because the
    // Russian template was hardcoded. Use OsDisplayName so the platform
    // shows correctly (Android phones say «при загрузке Android»).
    public static string SmpAutostartCardSubtitle => Ru
        ? (OperatingSystem.IsAndroid()
            ? "Настроить автозапуск VPN при загрузке устройства"
            : $"Настроить автозапуск VPN при старте {OsDisplayName}")
        : (OperatingSystem.IsAndroid()
            ? "Configure VPN autostart on device boot"
            : $"Configure VPN autostart at {OsDisplayName} boot");

    // ── Dialogs ──
    public static string FailedStartVpn => Ru ? "Не удалось запустить VPN:" : "Failed to start VPN:";
    public static string LinuxTunSandboxUnsupported => Ru
        ? "Эта песочница не позволяет sing-box создать системный TUN-интерфейс. Запустите VPNRouter вне AppImage/bubblewrap, например из нативного пакета дистрибутива."
        : "This sandbox cannot let sing-box create the host TUN interface. Run VPNRouter outside AppImage/bubblewrap, for example from a native distro package.";
    public static string LinuxPkexecUnavailable => Ru
        ? "Не найден доверенный pkexec. Установите polkit или выдайте capability файлу sing-box вне песочницы."
        : "No trusted pkexec was found. Install polkit or grant the sing-box file capability outside a sandbox.";
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
    public static string FieldPublicKey => Ru ? "Открытый ключ:" : "Public Key:";
    public static string FieldShortId => Ru ? "Короткий ID:" : "Short ID:";

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
    public static string CheckLeaks => Ru ? "Проверить IP-утечку" : "Check IP leak";
    public static string ShowLogs => Ru ? "Логи" : "Logs";

    public static string StrictModeLabel => Ru
        ? "Строгий режим (быстрая реакция на сбои)"
        : "Strict mode (faster crash detection)";
    public static string StrictModeHint => Ru
        ? "Health check каждые 5 секунд вместо 30. Уменьшает окно потенциальной утечки трафика при крахе sing-box."
        : "Health check every 5 seconds instead of 30. Reduces the leak window if sing-box silently hangs.";
    public static string MtuLabel => Ru
        ? "MTU TUN-интерфейса"
        : "TUN interface MTU";
    public static string MtuHint => Ru
        ? "Размер пакета TUN-интерфейса (576–1500; при IPv6 минимум 1280). По умолчанию 1420: лучше для Steam SDR и realtime-игр. 1400/1380 — запасные варианты для узких mobile/PPPoE/nested VPN путей. Кнопка Windows проверяет только IPv4 DF ping до 8.8.8.8, а не путь до VPN-сервера или IPv6. Применяется при переподключении."
        : "TUN interface packet size (576–1500; minimum 1280 with IPv6). Default 1420: better for Steam SDR and realtime games. 1400/1380 are fallbacks for narrow mobile/PPPoE/nested VPN paths. The Windows button checks only IPv4 DF ping to 8.8.8.8, not the VPN server path or IPv6. Applied on reconnect.";
    public static string MtuWarningLow => Ru
        ? "MTU ниже 1332 может ломать Dota 2 / CS2 / TF2 / Steam SDR."
        : "MTU below 1332 may break Dota 2 / CS2 / TF2 / Steam SDR.";
    public static string MtuWarningHigh => Ru
        ? "MTU выше 1420 может ломать VPN/proxy пути из-за PMTU. Попробуйте 1400, затем 1380."
        : "MTU above 1420 may break VPN/proxy paths due to PMTU. Try 1400, then 1380.";
    public static string MtuAutoTuneButton => Ru
        ? "Подобрать по IPv4 ping"
        : "Pick from IPv4 ping";
    public static string MtuAutoTuneRunning => Ru
        ? "Проверяю IPv4 DF ping до 8.8.8.8..."
        : "Running IPv4 DF ping to 8.8.8.8...";
    public static string MtuAutoTuneWindowsOnly => Ru
        ? "IPv4 DF-проба доступна только в Windows."
        : "The IPv4 DF probe is available only on Windows.";
    public static string MtuAutoTuneApplied(int mtu) => Ru
        ? $"По IPv4 DF ping до 8.8.8.8 сохранён консервативный MTU {mtu}. Переподключите VPN, чтобы применить."
        : $"IPv4 DF ping to 8.8.8.8 selected conservative MTU {mtu}. Reconnect VPN to apply.";
    public static string MtuAutoTuneBlocked => Ru
        ? "Обычный IPv4 ping до 8.8.8.8 не проходит. Сначала выключите True Split/почините WFP."
        : "Plain IPv4 ping to 8.8.8.8 fails. Turn off True Split/fix WFP first.";
    public static string MtuAutoTuneNoResult => Ru
        ? "IPv4 DF ping до 8.8.8.8 не нашёл рабочий payload; ICMP может быть заблокирован."
        : "IPv4 DF ping to 8.8.8.8 found no working payload; ICMP may be blocked.";
    public static string MtuAutoTuneTooLow(int mtu) => Ru
        ? $"До 8.8.8.8 прошёл только IPv4 DF payload {mtu}. Он ниже пола 1332 для Steam SDR, автоматически не сохраняю."
        : $"Only IPv4 DF payload {mtu} reached 8.8.8.8. It is below the 1332 Steam SDR floor, so it was not saved automatically.";
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
    // Wave 39 (v2.35.0-r5) — firewall-level DNS lockdown. Targets the
    // Windows DNS Client multi-resolver race that bypasses sing-box even
    // with SMHNR/ParallelAAAA disabled. See
    // plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md.
    public static string DnsLeakLockdownLabel => Ru
        ? "Блокировать DNS вне VPN (защита от утечек)"
        : "Block DNS outside VPN (leak protection)";
    // v2.40.x (Fix #9): honesty note — shown only where the lockdown is still a
    // no-op. macOS gained a working DNS-hardening backend in v2.41.0 (Fix #1),
    // so this now applies to Linux only (no nftables kill-switch yet, task #131).
    public static string DnsLeakLockdownUnavailableNote => Ru
        ? "Пока недоступно на Linux"
        : "Not available on Linux yet";

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
    // v2.36.0-r3 (EOStārāTheia 2026-05-23 UX-3 fix): added {0} placeholder
    // for download percentage. Pre-r3 the string was constant — the
    // string.Format(UpdateDownloading, pct) call silently dropped the pct
    // argument because no placeholder existed. User saw "Загрузка
    // обновления..." indefinitely with no way to tell if hung or progressing.
    public static string UpdateDownloading => Ru ? "Загрузка обновления: {0}%" : "Downloading update: {0}%";
    public static string UpdateApplying => Ru ? "Применение обновления..." : "Applying update...";
    public static string UpdateRestarting => Ru ? "Перезапуск..." : "Restarting...";
    public static string UpdateFailed => Ru ? "Ошибка обновления: {0}" : "Update failed: {0}";
    public static string OtherVersions => Ru ? "Другие версии" : "Other versions";
    public static string HideOlderVersions => Ru ? "Скрыть версии" : "Hide versions";
    public static string LoadingVersions => Ru ? "Загружаю список версий..." : "Loading versions...";
    public static string NoOlderVersions => Ru ? "Подходящих старых версий нет." : "No eligible older versions.";
    public static string VersionHistoryFailed => Ru ? "Не удалось загрузить список версий." : "Could not load version history.";
    public static string InstalledVersion => Ru ? "Установлена" : "Installed";
    public static string RollbackAction => Ru ? "Установить" : "Install";
    public static string RollbackSafetyHint => Ru
        ? "Доступны только недавние стабильные версии с проверкой SHA-256."
        : "Only recent stable releases with verified SHA-256 are available.";
    public static string RollbackConfirmation => Ru
        ? "Установить v{0}? VPN остановится, приложение перезапустится. Перед откатом будет сохранена резервная копия настроек."
        : "Install v{0}? VPN will stop and the app will restart. A settings backup will be saved before rollback.";
    public static string ConfirmRollback => Ru ? "Установить старую версию" : "Install older version";
    public static string Cancel => Ru ? "Отмена" : "Cancel";
    public static string DowngradeNoConfigBackup => Ru
        ? "Файл настроек отсутствует — резервная копия не требуется."
        : "No settings file needed a downgrade backup.";
    public static string DowngradeConfigBackupCreated => Ru
        ? "Резервная копия настроек создана: {0}"
        : "Settings backup created: {0}";
    public static string DowngradeReceiptCleanupFailed => Ru
        ? "Не удалось очистить состояние предыдущего обновления перед откатом."
        : "Could not clear the previous update state before downgrade.";
    public static string DowngradeInvalidVersion => Ru
        ? "Откат отменён: версия имеет неверный формат."
        : "Downgrade was refused because the target version is invalid.";

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
    public static string ChannelExperimental => Ru ? "Эксперимент." : "Experimental";

    // ── Autostart ──
    // ── Subscriptions (multi) ──
    public static string SubscriptionsSection => Ru ? "Подписки" : "Subscriptions";
    public static string SubscriptionNameHint => Ru ? "Имя" : "Name";
    public static string AddSubscription => Ru ? "+ Добавить" : "+ Add";
    public static string RefreshAll => Ru ? "Обновить все" : "Refresh all";
    public static string NeverRefreshed => Ru ? "никогда" : "never";
    public static string SubUpdatedAt => Ru ? "Обновлено" : "Updated";
    // A (2026-06-20) — opt-in urltest auto-select toggle (Android Subscribe tab,
    // parity with desktop SubscribePage). Desktop uses VM L_AutoSelectBest*; Android
    // reads these shared strings.
    // urltest R5 (audit batch-1 #3): honest wording — Auto is a QUICK WEB TEST
    // selector (one generate_204 probe), NOT full protocol verification. Never
    // present it as having proven "the best server" works.
    public static string AutoSelectBestServer => Ru
        ? "Авто-выбор по быстрому веб-тесту"
        : "Auto-select via quick web test";
    public static string AutoSelectBestServerTip => Ru
        ? "Оборачивает серверы подписки одного протокола в urltest-группу — соединение идёт через узел, "
          + "быстрее всех отвечающий на веб-запрос (generate_204). Это быстрый веб-тест, а не полная проверка "
          + "VPN-протокола. Серверы с недавно подтверждённой блокировкой протокола исключаются из группы."
        : "Wraps same-protocol subscription servers in a urltest group — traffic rides the node that answers "
          + "a web probe (generate_204) fastest. This is a quick web test, not full VPN-protocol verification. "
          + "Servers with a recently confirmed protocol block are excluded from the group.";
    // B7 (2026-06-21) — Android foreground-service notification, passed to the Java
    // VpnRouterService via intent extras (English literals stay as the Java fallback).
    public static string NotifTunnelActive => Ru ? "Туннель активен" : "Tunnel active";
    public static string NotifDisconnect => Ru ? "Отключить" : "Disconnect";

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
    // Wave 39 (v2.35.0-r5) — long-form tooltip explaining the firewall
    // lockdown's blast radius. Copy intentionally calls out the dnscrypt-proxy
    // / AdGuard Home / Pi-hole exception so power users know to disable it
    // if they run a local resolver. See
    // plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md §Risk + rollback.
    public static string TipDnsLeakLockdown => Ru
        ? "Блокирует системные DNS-запросы по UDP/53, TCP/53 и TCP/853 на всех интерфейсах кроме TUN, пока VPN активен. Защищает от утечки DNS к провайдеру даже если Windows DNS Client использует множественные резолверы параллельно. Может сломать локальные DNS-прокси (dnscrypt-proxy, AdGuard Home на 127.0.0.1) — отключите если используете."
        : "Blocks system DNS queries on UDP/53, TCP/53, and TCP/853 across all non-TUN interfaces while VPN is active. Protects against DNS leaks to ISP resolvers even when Windows DNS Client races multiple resolvers in parallel. May break local DNS proxies (dnscrypt-proxy, AdGuard Home on 127.0.0.1) — disable if you use one.";
    public static string TipBlockAds => Ru
        ? "Блокировать известные рекламные/трекинг домены на уровне VPN DNS"
        : "Block known ad/tracker domains at the VPN DNS layer";

    // Tooltips — Zapret / DPI
    public static string TipZapretAutoUpdate => Ru
        ? "Каждые 24 часа проверять обновление Zapret"
        : "Check for Zapret updates every 24 hours";

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
    public static string TipDismiss => Ru ? "Скрыть" : "Dismiss";
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
    public static string LblPublicKey => Ru ? "Открытый ключ:" : "Public Key:";
    public static string LblShortId   => Ru ? "Короткий ID:" : "Short ID:";

    // Descriptive labels
    public static string LblRoutingMode          => Ru ? "Режим маршрутизации" : "Routing mode";
    public static string LblNoServers            => Ru ? "Серверов нет" : "No servers";
    public static string LblAddSubscriptionHint  => Ru
        ? "Добавьте подписку ниже"
        : "Add a subscription below";

    // Badge
    public static string LblCustomBadge => Ru ? "свой" : "custom";

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
        : "A custom config is a ready-made sing-box JSON file for non-standard protocols (TUIC, Hysteria2, Reality+gRPC, etc.). Click \"Add config…\" below to import.";

    // v2.32.0 — recovery banner shown after SettingsValidator rejected a
    // structurally-valid but semantically-broken config.yaml (typoed
    // config_mode, port out of range, malformed subscription URL, etc.)
    // and the loader rewrote defaults. The backup path comes from
    // SettingsLoader.LastRecoveryNotice and is appended verbatim by the
    // VM, so the localized string is the prefix only.
    public static string SettingsRecoveredFromBadConfig(string backupPath) => Ru
        ? string.IsNullOrEmpty(backupPath)
            ? "Конфиг был повреждён, восстановлены настройки по умолчанию."
            : $"Конфиг был повреждён, восстановлены настройки по умолчанию. Резервная копия: {backupPath}"
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
        ? "Проверка не отвечает"
        : "Stale check";

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
        ? "Состояние туннеля повторяет иконку VPN-ключа в строке состояния."
        : "Tunnel state mirrors the system VPN-key icon in the status bar.";

    // Section headers
    public static string MenuSectionView => Ru ? "Вид" : "Appearance";

    public static string MenuSectionDiagnostics => Ru ? "Диагностика" : "Diagnostics";

    public static string MenuSectionTroubleshooting => Ru ? "Устранение неполадок" : "Troubleshooting";

    public static string MenuSectionAbout => Ru ? "О приложении" : "About";

    // Diagnostics items
    public static string MenuItemOpenLogs => Ru ? "Открыть лог" : "Open log";
    public static string MenuItemExportDiag => Ru ? "Экспорт диагностики" : "Export diagnostics";

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
        : "Tip: open the system Camera app and point at a QR — Android recognizes the URL and offers a copy action.";

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

    // Bug #2 (2026-05-11) — Android mobile redesign of Applications tab.
    // Shown next to the search box so the user can verify the device-wide
    // app enumeration is producing a sane count (some OEM ROMs hide apps
    // from PackageManager.GetInstalledApplications; we merge with launcher
    // queries to catch them — surfaced here for transparency).
    public static string PerAppShowingCount => Ru ? "Показано: {0}" : "Showing: {0}";

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

    public static string SrvTestAll => Ru ? "Проверить все" : "Test all";

    public static string SrvTestOne => Ru ? "Проверить" : "Test";

    public static string SrvTesting => Ru ? "Проверка…" : "Testing…";

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

    /// <summary>Toast when the user picks a server in an Advanced tab while the
    /// VPN is connected — the new server is applied in place (a brief reconnect)
    /// and the user stays in Advanced (no bounce to Simple, no manual Stop+Start).
    /// {0} = server name.</summary>
    public static string SrvSwitchedReconnect => Ru
        ? "Переключаюсь на {0}..."
        : "Switching to {0}...";

    /// <summary>Toast when the user picks a server in an Advanced tab while
    /// disconnected — saved as the active server, applies on the next Connect.
    /// {0} = server name.</summary>
    public static string SrvSelectedActive => Ru
        ? "Активный сервер: {0}"
        : "Active server: {0}";

    /// <summary>Kebab menu item that opens the Free Configs overlay.</summary>
    public static string MenuSectionFreeConfigs => Ru ? "Публичные конфиги" : "Public configs";

    public static string MenuItemOpenFreeConfigs => Ru ? "Найти сервер" : "Find a server";

    public static string FcOverlayTitle => Ru ? "Публичные конфиги" : "Public configs";

    public static string FcSearchHint => Ru
        ? "Соберём список ниже из публичных источников и проверим TCP+TLS до каждого. Жми «Найти» — выберем самые быстрые. Рабочие конфиги сохраняются автоматически — открой вкладку «Сохранённые», чтобы их увидеть."
        : "We'll pull the list below from public sources and run TCP+TLS to each. Tap Find — we'll pick the fastest. Verified configs are saved automatically — open the Saved tab to see them.";

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

    // v2.39.0 (apps-page audit): shown when applying a public config fails —
    // the user's existing Servers list is explicitly left untouched.
    public static string FcApplyFailed => Ru
        ? "Не удалось применить конфиг — список серверов не тронут"
        : "Couldn't apply config — your server list is unchanged";

    // v2.39.0 (public-configs audit P1): backstop message if a public config
    // that hasn't passed deep verify is somehow tapped — Connect is gated on
    // Verified (✓✓) status; a single-✓ TCP/TLS candidate is not connectable.
    public static string FcConnectNeedsVerify => Ru
        ? "Дождитесь проверки конфига (✓✓) перед подключением."
        : "Wait for the config to be verified (✓✓) before connecting.";

    // v2.40.0 (contracts B1 #5): shown when Connect is tapped while a search /
    // recheck is still running — adopting a config stops+starts the VPN and
    // would race the verifier; wait until the search finishes.
    public static string FcConnectBusySearch => Ru
        ? "Дождитесь окончания поиска перед подключением."
        : "Wait for the search to finish before connecting.";

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

}
