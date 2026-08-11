namespace VPNRouter.Core.Localization;

public static partial class Strings
{
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

    // v2.36 (MVP one-button task B): typed port-conflict toast text.
    // Format args: {0} = port number, {1} = owner process hint
    // (only on the *WithOwner variant). Format strings stay stable so
    // unit tests can pin them via Assert.Contains.
    public static string TgProxyPortBusy => Ru
        ? "Порт {0} занят другим приложением. Закройте его или поменяйте порт в настройках."
        : "Port {0} is busy. Close the other app or change the port in settings.";
    public static string TgProxyPortBusyWithOwner => Ru
        ? "Порт {0} занят: {1}. Закройте его или поменяйте порт в настройках."
        : "Port {0} is busy (owner: {1}). Close the other app or change the port in settings.";
    public static string TgProxyExitedImmediately => Ru
        ? "Ошибка: tg-ws-proxy завершился сразу."
        : "Error: tg-ws-proxy exited immediately.";
    public static string TgProxyTelegramNotInstalled => Ru
        ? "Telegram не установлен — скачай с desktop.telegram.org"
        : "Telegram not installed — download from desktop.telegram.org";

    // v2.36 (MVP one-button task C): non-blocking warning banner shown
    // when the tg:// URI scheme has no registered handler (Telegram
    // Desktop not installed / no scheme association). Proxy keeps
    // running — banner offers Copy link + Dismiss fallbacks.
    public static string TgProxySchemeMissingWarning => Ru
        ? "Telegram Desktop не найден. Прокси работает, но открыть его автоматически нельзя — скопируйте ссылку и добавьте вручную в Telegram (на этом или другом устройстве)."
        : "Telegram Desktop not found. The proxy is running, but auto-open isn't available — copy the link below and add it manually in Telegram (here or on another device).";

    // v2.36 (MVP one-button task A): per-step download progress text.
    // Used by TgProxyUpdater.StatusChanged event consumers (the
    // ViewModel mirrors them into the banner). Stable "Step N/3:"
    // prefix lets tests pin the format.
    public static string TgProxyDownloadStep1Python => Ru
        ? "Шаг 1/3: Загрузка Python 3.12 (~11 МБ)..."
        : "Step 1/3: Downloading Python 3.12 (~11 MB)...";
    public static string TgProxyDownloadStep2Wheels => Ru
        ? "Шаг 2/3: Установка зависимостей (cryptography, cffi, pycparser)..."
        : "Step 2/3: Installing dependencies (cryptography, cffi, pycparser)...";
    public static string TgProxyDownloadStep3Source => Ru
        ? "Шаг 3/3: Загрузка proxy source с GitHub..."
        : "Step 3/3: Downloading proxy source from GitHub...";

    // v2.36.0-r7 — TgProxyOneTap design (per claude.ai/design handoff
    // `TgProxyOneTap.html`, variant A "Centered stack"). Replaces the dense
    // r3 grid layout (port + secret + buttons + setup hint always visible)
    // with a hero stack: plane icon → title → lede → big magic button →
    // 3 micro-step chips. Power-user controls collapse behind a "Тонкая
    // настройка" expander. Strings here are the hero copy.
    //
    // Per chat transcripts: deliberately DON'T mention "один клик" / "one
    // click" — the magic-button concept stays implicit in the layout, not
    // restated in the copy.
    public static string TgProxyOneTapTitleStopped => Ru
        ? "Включить Telegram"
        : "Activate Telegram";
    public static string TgProxyOneTapTitleRunning => Ru
        ? "Telegram через MTProto"
        : "Telegram via MTProto";
    public static string TgProxyOneTapLedeStopped => Ru
        ? "Поднимем локальный MTProto, откроем ссылку и Telegram сам подцепит секрет. Дальше только Start / Stop."
        : "We bring up a local MTProto proxy, open the t.me link and Telegram picks up the secret on its own. After that just Start / Stop.";
    public static string TgProxyOneTapLedeRunning(int port) => Ru
        ? $"Прокси работает локально на :{port}. Telegram уже подцепил секрет."
        : $"Proxy is running locally on :{port}. Telegram has picked up the secret.";
    public static string TgProxyOneTapStep1 => Ru
        ? "поднимется локально"
        : "starts locally";
    public static string TgProxyOneTapStep2 => Ru
        ? "откроется t.me / proxy"
        : "opens t.me / proxy";
    public static string TgProxyOneTapStep3 => Ru
        ? "Telegram настроится сам"
        : "Telegram configures itself";
    public static string TgProxyOneTapTune => Ru
        ? "Тонкая настройка"
        : "Advanced settings";
    public static string TgProxyOneTapAirPill(int port) => Ru
        ? $"В эфире · :{port}"
        : $"On the air · :{port}";

    // ───── v2.37.0-r25 — TgProxy TabControl headers (replaces Expander) ─────
    public static string TgProxyTabSettings => Ru ? "Настройки" : "Settings";
    public static string TgProxyTabVersion  => Ru ? "Версия"    : "Version";
    public static string TgProxyTabHelp     => Ru ? "Помощь"    : "Help";

    // v2.36.0-r8 — ZapretOneTap design (per
    // `plans/research-one-button-zapret-deep-2026-05-24.md`). Mirrors the
    // TgProxyOneTap variant-A pattern: hero card with shield icon → title →
    // lede → big magic button → 3 step chips, all r5-era controls stowed
    // behind a "Тонкая настройка" Expander.
    //
    // The 4 hero states (Stopped, Probing, Running, Fallback) drive title
    // and lede via VM property switches. Probe attempt strings parameterise
    // index/total/name so the user sees real-time progression.
    public static string ZapretOneTapTitleStopped => Ru
        ? "Обход блокировок"
        : "DPI bypass";
    public static string ZapretOneTapTitleProbing => Ru
        ? "Подбираю стратегию..."
        : "Picking strategy...";
    public static string ZapretOneTapTitleRunning(string strategy) => Ru
        ? $"Активна стратегия: {strategy}"
        : $"Active strategy: {strategy}";
    public static string ZapretOneTapTitleFallback => Ru
        ? "Стратегия не подобрана"
        : "No strategy matched";
    // r50: benefit-focused lede (was implementation-focused). User feedback
    // pointed out the lede listed *what we do* ("Скачаем zapret, поставим
    // Discord hosts...") instead of *what user gets*. Updated to mirror
    // TgProxyOneTap pattern which focuses on outcome.
    public static string ZapretOneTapLedeStopped => Ru
        ? "Откроем заблокированные сайты — Discord, YouTube и другие. Один клик — всё настроим автоматически."
        : "Unblock sites your ISP filters — Discord, YouTube and more. One click, fully automatic.";
    public static string ZapretOneTapLedeProbing(int index, int total, string strategy) => Ru
        ? $"Тестирую ({index}/{total}): {strategy} — проверяю Discord и YouTube..."
        : $"Probing ({index}/{total}): {strategy} — checking Discord and YouTube...";
    // v2.37.0-r1: per-attempt score in lede ("3/8 ok" while probing).
    public static string ZapretOneTapLedeProbingScored(int index, int total, string strategy, int pass, int totalProbes) => Ru
        ? $"Тестирую ({index}/{total}): {strategy} — {pass}/{totalProbes} ok"
        : $"Probing ({index}/{total}): {strategy} — {pass}/{totalProbes} ok";
    public static string ZapretOneTapLedeRunning => Ru
        ? "YouTube, Discord и другие заблокированные сервисы должны открываться через локальный bypass."
        : "YouTube, Discord and other blocked services should work via the local bypass.";
    public static string ZapretOneTapLedeFallback => Ru
        ? "Ни одна стратегия не подошла. Открой «Тонкую настройку» — там полный список + диагностика."
        : "No strategy matched. Open \"Advanced settings\" for the full list and diagnostics.";
    public static string ZapretOneTapStep1 => Ru
        ? "скачаем zapret"
        : "download zapret";
    public static string ZapretOneTapStep2 => Ru
        ? "настроим Discord hosts"
        : "configure Discord hosts";
    public static string ZapretOneTapStep3 => Ru
        ? "подберём стратегию"
        : "pick strategy";
    public static string ZapretOneTapTune => Ru
        ? "Тонкая настройка"
        : "Advanced settings";
    public static string ZapretOneTapStartButton => Ru
        ? "Включить обход блокировок"
        : "Enable DPI bypass";
    public static string ZapretOneTapStopButton => Ru
        ? "Остановить обход"
        : "Stop bypass";
    public static string ZapretOneTapAirPill(string strategy, int pid) => Ru
        ? $"В эфире · {strategy} · PID {pid}"
        : $"On the air · {strategy} · PID {pid}";
    // v2.37.0-r1: air-pill with probe-score badge ("В эфире · general (ALT3) · 7/8").
    public static string ZapretOneTapAirPillScored(string strategy, int pass, int total) => Ru
        ? $"В эфире · {strategy} · {pass}/{total}"
        : $"On the air · {strategy} · {pass}/{total}";
    public static string ZapretOneTapDownloading => Ru
        ? "Скачивание zapret..."
        : "Downloading zapret...";
    public static string ZapretOneTapInstallingHosts => Ru
        ? "Установка Discord hosts... (потребуется UAC)"
        : "Installing Discord hosts... (UAC required)";
    public static string ZapretOneTapAllFailedToast => Ru
        ? "Авто-подбор не сработал. Открой «Тонкую настройку» и выбери стратегию вручную."
        : "Auto-pick failed. Open \"Advanced settings\" to choose a strategy manually.";
    public static string ZapretOneTapNoSignalToast => Ru
        ? "Похоже, интернет недоступен. Проверь соединение и повтори."
        : "Looks like no internet. Check the connection and retry.";

    public static string OpenFolder => Ru ? "Открыть папку" : "Open folder";
    public static string OpenGitHub => "GitHub";

}
