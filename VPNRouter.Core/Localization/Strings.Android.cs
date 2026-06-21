namespace VPNRouter.Core.Localization;

public static partial class Strings
{
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

    // B3 (2026-06-21) — one-time Always-on + Lockdown kill-switch nudge
    public static string AlwaysOnNudgeTitle => Ru
        ? "Включить kill-switch?"
        : "Enable a kill-switch?";
    public static string AlwaysOnNudgeBody => Ru
        ? "Чтобы трафик не утекал, если VPN отвалится, включите VPNRouter как «Always-on VPN» с «Блокировкой» (Lockdown) в системных настройках VPN. Без этого Android не гарантирует блокировку при разрыве туннеля."
        : "To stop traffic from leaking if the VPN drops, set VPNRouter as your Always-on VPN with \"Block connections without VPN\" (Lockdown) in system VPN settings. Without it, Android can't guarantee a block when the tunnel fails.";
    public static string AlwaysOnNudgeOpen => Ru ? "Открыть настройки VPN" : "Open VPN settings";
    public static string AlwaysOnNudgeLater => Ru ? "Позже" : "Not now";

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

    // Bug-AND-017 (2026-05-16, polish iter 31): RU mixed half-EN
    // ("prerelease", "experimental канал"). Now fully Russian.
    public static string ReceivePrereleasesLabel => Ru
        ? "Получать пре-релизы (экспериментальный канал)"
        : "Receive prereleases (experimental channel)";

    // Bug-AND-018 (2026-05-16): both RU "Текущая версия" and EN "Current
    // version" overflowed the narrow card column on Android. Short
    // forms paired with the actual version number below convey the
    // same meaning.
    public static string CurrentVersionLabel => Ru ? "Версия" : "Version";

    // Bug-AND-018 (2026-05-16): full-width RU label crowded the version
    // column on 5" phones. Short form fits beside the version stack.
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
    /// Bug-AND-016 (2026-05-16, manual test pass): was unilingual EN.
    public static string AdvServersSubTabCustomJson => Ru ? "Свой конфиг (JSON)" : "Custom Config (JSON)";

    /// <summary>Footer action button — runs TCP+TLS probe on every listed server.</summary>
    public static string AdvServersTestAll => Ru ? "Тест все" : "Test all";

    /// <summary>Footer action button — runs deep HTTP-through-tunnel verification on every listed server.</summary>
    /// Bug-AND-016 (2026-05-16): was unilingual EN.
    public static string AdvServersDeepVerify => Ru ? "Глубокая проверка" : "Deep verify";

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

    // ── Bug-r9-E (2026-05-11) — third-party VPN conflict banner ──
    // Shown in the header banner of MainWindow when StartAsync throws
    // ConflictingVpnException. Stas's repro: xraycore.exe from v2RayTun
    // held wintun, sing-box silently failed adapter creation, user had
    // no way to know what to do. These strings name the specific process
    // and tell the user to stop it before retrying.

    /// <summary>Banner title — bold leading line on the conflict banner.</summary>
    public static string ConflictOtherVpnDetectedTitle => Ru
        ? "Обнаружен другой VPN-клиент"
        : "Another VPN client detected";

    /// <summary>Body of the conflict banner. <paramref name="processName"/>
    /// is the OS-level process name (e.g. <c>xraycore</c>),
    /// <paramref name="pid"/> the PID. One VPN can hold the TUN adapter
    /// at a time — explain that constraint so the user understands why
    /// VPNRouter can't just coexist.</summary>
    public static string ConflictOtherVpnDetectedMessage(string processName, int pid) => Ru
        ? $"Обнаружен другой VPN-клиент: {processName} (PID {pid}). " +
          $"Один VPN держит TUN-адаптер за раз. Остановите {processName} перед запуском VPNRouter."
        : $"Another VPN client detected: {processName} (PID {pid}). " +
          $"Only one VPN can hold the TUN adapter at a time. Stop {processName} before launching VPNRouter.";

    /// <summary>"Refresh" button on the conflict banner — re-runs the
    /// detection so the user can dismiss the banner once they've closed
    /// the other VPN.</summary>
    public static string ConflictRefreshButton => Ru ? "Проверить ещё раз" : "Refresh";

    // ── Bug-r9-G (2026-05-11) — Zapret AV-block toast ──
    // Detected in ZapretManager when winws.exe exits within < 2 s of
    // launch with non-zero code — almost always Windows Defender or
    // a third-party AV terminating it as suspicious. The user-facing
    // message tells them which folder to whitelist; the toast carries
    // a "Copy path" button (LblConflictCopyPath below) for convenience.

    /// <summary>Toast body shown when Zapret's winws.exe exits immediately.
    /// Includes the canonical whitelist path so the user can paste it
    /// into their AV's exception list. Path is hardcoded
    /// %ProgramData%\VPNRouter\zapret\ because that's where
    /// ZapretUpdater unpacks the release.</summary>
    public static string ZapretAvBlockToast => Ru
        ? "Zapret (winws.exe) был остановлен сразу после запуска. Возможно его блокирует антивирус. " +
          @"Добавьте в исключения: C:\ProgramData\VPNRouter\zapret\ (вся папка)."
        : "Zapret (winws.exe) exited immediately after launch. Likely an antivirus is blocking it. " +
          @"Whitelist: C:\ProgramData\VPNRouter\zapret\ (whole folder).";

    /// <summary>"Copy path" button label on the Zapret AV-block toast.
    /// Puts the whitelist directory into the clipboard.</summary>
    public static string ZapretAvBlockCopyPath => Ru ? "Скопировать путь" : "Copy path";
}
