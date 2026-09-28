namespace VPNRouter.Core.Localization;

public static partial class Strings
{
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
    public static string TgProxyStart => Ru ? "Запустить Telegram-прокси" : "Start Telegram proxy";
    public static string TgProxyStop  => Ru ? "Остановить Telegram-прокси" : "Stop Telegram proxy";
    public static string TgProxyOpenInTelegram => Ru ? "Открыть в Telegram" : "Open in Telegram";

    public static string TgProxyStartAndOpen => Ru
        ? "Запустить и открыть Telegram"
        : "Start & open Telegram";
    public static string TgProxySetupOnce => Ru
        ? "Нажмите «Открыть в Telegram» один раз для настройки прокси. Затем достаточно использовать кнопку запуска и остановки."
        : "Click 'Open in Telegram' once to set up the proxy. After that just Start/Stop.";

    public static string TgProxyReopenInTelegram => Ru
        ? "Открыть в Telegram повторно"
        : "Reopen in Telegram";

    public static string TgProxyCopySecretA11y => Ru
        ? "Скопировать MTProto secret в буфер обмена"
        : "Copy MTProto secret to clipboard";
    public static string TgProxyRegenerateSecretA11y => Ru
        ? "Сгенерировать новый MTProto secret"
        : "Generate new MTProto secret";

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

    public static string TgProxySchemeMissingWarning => Ru
        ? "Telegram Desktop не найден. Прокси работает, но открыть его автоматически нельзя — скопируйте ссылку и добавьте вручную в Telegram (на этом или другом устройстве)."
        : "Telegram Desktop not found. The proxy is running, but auto-open isn't available — copy the link below and add it manually in Telegram (here or on another device).";

    public static string TgProxyDownloadStep1Python => Ru
        ? "Шаг 1/3: Загрузка Python 3.12 (~11 МБ)..."
        : "Step 1/3: Downloading Python 3.12 (~11 MB)...";
    public static string TgProxyDownloadStep2Wheels => Ru
        ? "Шаг 2/3: Установка зависимостей (cryptography, cffi, pycparser)..."
        : "Step 2/3: Installing dependencies (cryptography, cffi, pycparser)...";
    public static string TgProxyDownloadStep3Source => Ru
        ? "Шаг 3/3: Загрузка proxy source с GitHub..."
        : "Step 3/3: Downloading proxy source from GitHub...";

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

    public static string TgProxyTabSettings => Ru ? "Настройки" : "Settings";
    public static string TgProxyTabVersion  => Ru ? "Версия"    : "Version";
    public static string TgProxyTabHelp     => Ru ? "Помощь"    : "Help";

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
    public static string ZapretOneTapLedeStopped => Ru
        ? "Откроем заблокированные сайты — Discord, YouTube и другие. Один клик — всё настроим автоматически."
        : "Unblock sites your ISP filters — Discord, YouTube and more. One click, fully automatic.";
    public static string ZapretOneTapLedeProbing(int index, int total, string strategy) => Ru
        ? $"Тестирую ({index}/{total}): {strategy} — проверяю Discord и YouTube..."
        : $"Probing ({index}/{total}): {strategy} — checking Discord and YouTube...";
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
