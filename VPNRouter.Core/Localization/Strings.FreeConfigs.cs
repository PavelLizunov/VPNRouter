namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    // ── Free Configs ──
    // v2.30.7-r2 — "Свободные" / "Free" was deemed unclear (user
    // feedback). Renamed to "Публичные" / "Public" — describes the
    // source (public free pools from 14 sources, server-side
    // pre-aggregated via GH Actions) without sounding like
    // "free trial" or "no-cost product". Fits narrow tab strip.
    public static string TabFreeConfigs => Ru ? "Публичные" : "Public";
    public static string FcDashboardTotal     => Ru ? "Всего"         : "Total";
    public static string FcDashboardWorking   => Ru ? "Работают"      : "Working";
    public static string FcDashboardTimeout   => Ru ? "Таймаут"       : "Timeout";
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
        ? $"Найдено {found}/{target} · группа {batchNum}/{totalBatches} (TCP+TLS)..."
        : $"Found {found}/{target} · batch {batchNum}/{totalBatches} (TCP+TLS)...";
    public static string FcStatusBatchedTcpTlsProgress(int found, int target, int batchNum, int totalBatches, int done, int total) => Ru
        ? $"Найдено {found}/{target} · группа {batchNum}/{totalBatches} · проверено {done}/{total}"
        : $"Found {found}/{target} · batch {batchNum}/{totalBatches} · tested {done}/{total}";
    public static string FcStatusBatchedDeepVerify(int found, int target, int batchNum, int totalBatches, int candidates) => Ru
        ? $"Найдено {found}/{target} · группа {batchNum}/{totalBatches} · глубокая проверка {candidates} кандидатов..."
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
        ? "Основной VPN активен — результаты проверки могут быть недостоверны. Отключите VPN перед глубокой проверкой."
        : "Main VPN is active — verification results may be unreliable. Disconnect VPN first.";
    public static string FcOpenLogs         => Ru ? "Логи"                : "Logs";
    public static string FcClearFailed      => Ru ? "Убрать мусор"        : "Clear dead";
    public static string FcKeepVerified     => Ru ? "Только ✓✓"           : "Keep ✓✓ only";
    public static string FcKeepVerifiedOnly => FcKeepVerified;
    public static string FcClearAll         => Ru ? "Очистить всё"        : "Clear all";
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
        ? "VPN активен — результаты пинга проходят через туннель и могут быть недостоверны. Для точного теста отключите VPN."
        : "VPN is active — ping results go through the tunnel and may be inaccurate. Disconnect VPN for accurate tests.";
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
        ? "Загрузить список конфигов"
        : "Load configs list";
    public static string FcFilteredEmpty      => Ru
        ? "Ничего не найдено по фильтру. Снимите «Только рабочие», увеличьте порог пинга или выберите «Все страны»."
        : "No results for current filter. Uncheck 'Only working', raise the ping threshold, or choose 'All countries'.";
    public static string FcRefreshHint        => Ru
        ? "Первый запуск ≈1 мин. Тестируется до 500 серверов за раз — повторяйте для более полных данных."
        : "First run ≈1 min. Tests up to 500 servers at a time — repeat for fuller coverage.";

    // v2.13.17 — Smart Refresh (latency goal)
    public static string FcSmartRefreshLabel => Ru ? "Smart Refresh (стоп при достижении цели)" : "Smart Refresh (stop when goal reached)";
    public static string FcTargetNLabel      => Ru ? "Найти:" : "Find:";
    public static string FcConfigsWord       => Ru ? "конфигов" : "configs";
    public static string FcWithPingUnder     => Ru ? "с пингом <" : "with ping <";
    public static string FcMsUnit            => "ms";
    public static string FcSmartRefreshHint  => Ru
        ? "Ускоряет Refresh: остановка как только накопится N конфигов с низким пингом. Отключите для полного сканирования."
        : "Speeds up Refresh: stops once N low-ping configs are found. Uncheck for full scan.";

    // v2.28.4-r2: Quickstart banner removed (single-button flow makes the 3-step lecture obsolete).

    // v2.14.7 — collapsible More Options
    public static string FcMoreOptions => Ru ? "Больше опций (фильтры, очистка, свои источники)" : "More options (filters, cleanup, user sources)";

    // v2.28.4-r1: 6-section nav removed (FreeConfigs is now single Simple page).
    public static string FcListHeader    => Ru ? "Конфиги"       : "Configs";
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
    public static string FcTabSaved                  => Ru ? "Сохранённые" : "Saved";
    public static string FcTabSavedWithCount(int n)  => Ru ? $"Сохранённые ({n})" : $"Saved ({n})";
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
    public static string FcFastScanLabel => Ru ? "Fast scan (только TCP, без TLS)" : "Fast scan (TCP only, no TLS)";
    public static string FcFastScanHint  => Ru
        ? "В 3 раза быстрее, но помечает как 'рабочие' даже honeypot-ы (открытый порт ≠ VLESS). Используйте только если Deep Verify отфильтрует дальше."
        : "3× faster but marks even honeypots as 'working' (open port ≠ VLESS). Deep Verify filters them out afterwards.";

    // v2.14.3 — Deep Verify presets
    public static string FcPresetLabel    => Ru ? "Пресет:" : "Preset:";
    public static string FcPresetGaming   => Ru ? "Gaming (пинг<60ms, bw>2 Mbps)" : "Gaming (ping<60ms, bw>2 Mbps)";
    public static string FcPresetStream   => Ru ? "Streaming (пинг<250ms, bw>10 Mbps)" : "Streaming (ping<250ms, bw>10 Mbps)";
    public static string FcPresetChat     => Ru ? "Chat/web (пинг<300ms, bw>1 Mbps)" : "Chat/web (ping<300ms, bw>1 Mbps)";
    public static string FcPresetBest     => Ru ? "Best effort (любой рабочий)" : "Best effort (any verified)";
    public static string FcPresetCustom   => Ru ? "Custom" : "Custom";
    public static string FcCustomPing     => Ru ? "Макс пинг:" : "Max ping:";
    public static string FcCustomBw       => Ru ? "Мин bw:" : "Min bw:";
    public static string FcMbpsUnit       => "Mbps";
    public static string FcBandwidthHint  => Ru
        ? "Замер bandwidth скачивает ~5 MB через прокси (~150 MB для 30 кандидатов). OK на wifi, осторожно на мобильном."
        : "Bandwidth test downloads ~5 MB per config via proxy (~150 MB for 30 candidates). OK on wifi, mind mobile data.";

    // v2.14.4 — User sources
    public static string FcUserSrcSection      => Ru ? "Мои источники" : "My sources";
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
        : "You're connecting to a public proxy server";
    public static string FcSecWarnBody => Ru
        ? "Оператор этого конфига может видеть метаданные вашего трафика — к каким сайтам вы обращаетесь, когда, как часто. Содержимое HTTPS-сайтов (логины, пароли, сообщения) защищено TLS и недоступно оператору."
        : "The operator of this config can see your traffic metadata — which sites you visit, when, how often. HTTPS content (logins, passwords, messages) is protected by TLS and invisible to the operator.";
    public static string FcSecWarnDontUseList => Ru
        ? "✗ НЕ используйте для:\n  • банковских приложений / онлайн-банков\n  • входа в почту (Gmail, Яндекс.Почта, Mail.ru)\n  • Госуслуги, налоговая, банки\n  • 2FA / SMS-коды / криптокошельки\n  • любых паролей, которые вы цените"
        : "✗ DO NOT use for:\n  • banking apps / online banking\n  • email logins (Gmail, Outlook, etc.)\n  • government services, tax sites\n  • 2FA / SMS codes / crypto wallets\n  • any passwords you care about";
    public static string FcSecWarnGoodFor => Ru
        ? "✓ Подходит для: YouTube, новостей, Wikipedia, Discord, Telegram, публичного веба"
        : "✓ Good for: YouTube, news, Wikipedia, Discord, Telegram, public web browsing";
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

}
