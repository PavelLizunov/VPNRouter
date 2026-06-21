namespace VPNRouter.Core.Localization;

public static partial class Strings
{
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
        ? "vless:// / naive://... или https://..."
        : "vless:// / naive://... or https://...";
    public static string SmpInputHint => Ru
        ? "Приму ссылку сервера (vless / hysteria2 / tuic / ss / naive) или URL подписки (http/https)."
        : "Accepts a server link (vless / hysteria2 / tuic / ss / naive) or a subscription URL (http/https).";
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

    // ── Bug-AND-023 v3 (2026-05-17, user: "магия 1-действия") — ───────────
    // Live-preview QR scan now auto-routes the payload by URI scheme:
    //   vless:// / hy2:// / tuic:// / ss://  → add as server + Connect
    //   http:// / https://                   → add as subscription, refresh,
    //                                          pick first server, Connect
    // Pre-v3 the user had to type a name, press Add, then Connect — three
    // taps for a flow that should be zero.
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

    // v2.32.3 (2026-05-17) — placeholder credentials rejection toasts.
    // Triggered when the user scans / pastes / subscribes to a vless URL
    // whose Reality public_key (or short_id, or server IP) matches the
    // known PlaceholderVlessUri leftover from old Android smoke-test
    // builds. F-E catches it at Connect; v2.32.3 input gates catch it
    // before the credential ever lands in storage. UI message has to
    // tell the user this is THEIR provider's problem, not a VPNRouter
    // bug — otherwise they think the app is broken.
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
        ? (OperatingSystem.IsAndroid()
            ? "Серверы · Подписки · Настройки · Приложения · Публичные"
            : "Серверы · Подписки · Zapret · Telegram-прокси · Публичные")
        : (OperatingSystem.IsAndroid()
            ? "Servers · Subscriptions · Settings · Applications · Public"
            : "Servers · Subscriptions · Zapret · Telegram proxy · Public");

    // Mini-header menu items (⋯ flyout)
    public static string SmpMenuTheme         => Ru ? "Тема"                   : "Theme";
    public static string SmpMenuLanguage      => Ru ? "Язык"                   : "Language";
    public static string SmpMenuOpenLogs      => Ru ? "Открыть логи"           : "Open logs";
    public static string SmpMenuCheckLeaks    => Ru ? "Проверить утечку IP"    : "Check IP leak";
    public static string SmpMenuCheckUpdates  => Ru ? "Проверить обновления"   : "Check for updates";
    public static string SmpMenuSwitchToAdv   => Ru ? "Перейти в Advanced"     : "Switch to Advanced";
    // v2.39.0 — one-click diagnostics export (Settings -> Updates -> Support)
    public static string DiagSupportHeader    => Ru ? "Поддержка"              : "Support";
    public static string DiagExportButton     => Ru ? "Собрать диагностику"    : "Export diagnostics";
    public static string DiagExporting        => Ru ? "Собираю…"               : "Collecting…";
    public static string DiagExportHint       => Ru
        ? "Соберёт логи и настройки в один ZIP на рабочем столе. Пароли, ключи и токены удаляются. Проверьте архив перед отправкой."
        : "Collects your logs and settings into one ZIP on the Desktop. Passwords, keys and tokens are removed. Review the archive before sharing.";
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
    // v2.40.x (Fix #7): tri-state theme — follow the OS appearance.
    public static string SmpSegSystem                 => Ru ? "Системная"          : "System";
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

}
