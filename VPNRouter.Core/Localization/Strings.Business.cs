namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessRussian => "Русский";
    public static string BusinessEnglish => "English";
    public static string BusinessServiceConnected => Ru ? "Подключено через службу" : "Connected via service";
    public static string BusinessConnected => Ru ? "Подключено" : "Connected";
    public static string BusinessPolicyEmbedded => Ru ? "Правила компании: встроенный список." : "Company rules: embedded list.";
    public static string BusinessPolicyPending => Ru ? "Новые правила сохранены. Применятся при подключении." : "New rules saved. They will apply when you connect.";
    public static string BusinessPolicyApplying => Ru ? "Применяем правила компании. VPN переподключается…" : "Applying company rules. VPN is reconnecting…";
    public static string BusinessPolicyApplyFailed => Ru ? "Правила сохранены, но применить их не удалось. Переподключитесь к VPN." : "Rules were saved but could not be applied. Reconnect to the VPN.";
    public static string BusinessPolicyApplied => Ru ? "Правила компании применены." : "Company rules applied.";
    public static string BusinessPolicyRefreshUnavailable => Ru ? "Не удалось обновить правила компании. Сохранены последние проверенные правила." : "Could not refresh company rules. The last verified rules are retained.";
    public static string BusinessPolicyRecoveryRequired => Ru ? "Не удалось восстановить правила компании. Обратитесь к администратору для восстановления настроек." : "Could not recover company rules. Contact your administrator to restore settings.";
    public static string BusinessMenu => Ru ? "Меню" : "Menu";
    public static string BusinessLanguage => Ru ? "Язык" : "Language";
    public static string BusinessLanguageSaveFailed => Ru ? "Не удалось сохранить язык." : "Could not save language.";
    public static string BusinessFullCompact => Ru ? "Весь трафик с исключениями" : "All traffic with exceptions";
    public static string BusinessUpdateServers => Ru ? "Обновить список серверов" : "Refresh server list";
    public static string BusinessSubscriptionNotConfigured => Ru ? "Список встроен в эту сборку. Для обновления нужна новая версия." : "This build has an embedded list. Updating requires a newer build.";
    public static string BusinessSubscriptionUpdating => Ru ? "Обновляем список серверов…" : "Updating server list…";
    public static string BusinessSubscriptionUpdated(int count) => Ru ? $"Список обновлён. Серверов: {count}." : $"List updated. Servers: {count}.";
    public static string BusinessSubscriptionFailed => Ru ? "Список не обновлён. Сохранены прежние серверы. Повторите позже." : "Update failed. Your previous servers are retained. Try again later.";
    public static string BusinessSubscriptionCancelled => Ru ? "Обновление остановлено. Сохранены прежние серверы." : "Update stopped. Your previous servers are retained.";
    public static string BusinessDiagnosticsHeader => Ru ? "Диагностика" : "Diagnostics";
    public static string BusinessDiagnosticsLocalOnly => Ru ? "Можно собрать отчёт локально. Отправка в этой сборке не настроена." : "You can collect a local report. Upload is not configured in this build.";
    public static string BusinessDiagnosticsHint => Ru ? "Отчёт содержит имя компьютера и Windows-пользователя со слов клиента. Отправка — только по кнопке. Пароли маскируются; в логах могут остаться адреса и названия приложений." : "The report includes client-reported computer and Windows user names. Upload happens only on Send. Secrets are masked; logs may retain addresses and app names.";
    public static string BusinessDiagnosticsCollect => Ru ? "Сохранить отчёт локально" : "Save report locally";
    public static string BusinessDiagnosticsSend => Ru ? "Отправить диагностику" : "Send diagnostics";
    public static string BusinessDiagnosticsCancel => Ru ? "Остановить" : "Cancel";
    public static string BusinessDiagnosticsOpen => Ru ? "Открыть папку отчёта" : "Open report folder";
    public static string BusinessDiagnosticsCollecting => Ru ? "Собираем диагностику…" : "Collecting diagnostics…";
    public static string BusinessDiagnosticsSending => Ru ? "Отправляем диагностику…" : "Sending diagnostics…";
    public static string BusinessDiagnosticsCollected => Ru ? "Отчёт сохранён." : "Report saved.";
    public static string BusinessDiagnosticsDelivered => Ru ? "Отчёт доставлен." : "Report delivered.";
    public static string BusinessDiagnosticsCollectFailed => Ru ? "Не удалось собрать отчёт. Проверьте доступ к папке логов и повторите." : "Collection failed. Check access to the logs folder and try again.";
    public static string BusinessDiagnosticsSendFailed => Ru ? "Отправка не подтверждена. Повторите." : "Delivery not confirmed. Retry Send.";
    public static string BusinessDiagnosticsCancelled => Ru ? "Операция остановлена. Сохранённые отчёты оставлены на компьютере." : "Operation stopped. Saved reports are retained locally.";
    public static string BusinessDiagnosticsOpenFailed => Ru ? "Не удалось открыть папку отчёта." : "Could not open the report folder.";
    public static string BusinessNodeMetricsUnavailable => Ru ? "Активность: нет данных" : "Activity: no data";
    public static string BusinessNodeMetricsStale => Ru ? "Активность: данные устарели" : "Activity: data is stale";
    public static string BusinessUpdatesUnavailable => Ru ? "Обновления недоступны" : "Updates unavailable";
    public static string BusinessUpdateWaitForConnection => Ru ? "Дождитесь завершения подключения и повторите обновление." : "Wait for the connection to finish and retry the update.";
    public static string BusinessUpdateStopFailed => Ru ? "Не удалось остановить VPN. Отключите его и повторите обновление." : "Could not stop VPN. Disconnect and retry the update.";
    public static string BusinessUpdateDownloadFailed => Ru ? "Не удалось загрузить проверенное обновление. Повторите позже." : "Could not download a verified update. Retry later.";
    public static string BusinessUpdateInstallFailed => Ru ? "Не удалось запустить обновление. Повторите позже." : "Could not start the update. Retry later.";
    public static string BusinessNodeActivityLow => Ru ? "Активность: низкая" : "Activity: low";
    public static string BusinessNodeActivityMedium => Ru ? "Активность: средняя" : "Activity: medium";
    public static string BusinessNodeActivityHigh => Ru ? "Активность: высокая" : "Activity: high";
    public static string BusinessNodeMetricsHint(ulong? ageSeconds, bool stale)
    {
        var meaning = Ru
            ? "Активность по трафику за последние сутки; оценка от сервера."
            : "Activity from traffic over the last day, assessed by the server.";
        if (ageSeconds is not { } age)
            return meaning + (Ru ? " Время пробы неизвестно." : " Sample age is unknown.");
        var amount = age < 60 ? age : age / 60;
        var unit = Ru ? age < 60 ? "с" : "мин" : age < 60 ? "s" : "min";
        return meaning + (Ru ? $" Проба {amount} {unit} назад." : $" Sample {amount} {unit} ago.") +
            (stale ? (Ru ? " Данные устарели." : " Data is stale.") : string.Empty);
    }
    public static string BusinessCountryName(string code) => (code, Ru) switch
    {
        ("DE", true) => "Германия", ("DE", false) => "Germany",
        ("IS", true) => "Исландия", ("IS", false) => "Iceland",
        ("NL", true) => "Нидерланды", ("NL", false) => "Netherlands",
        ("FI", true) => "Финляндия", ("FI", false) => "Finland",
        ("SE", true) => "Швеция", ("SE", false) => "Sweden",
        ("FR", true) => "Франция", ("FR", false) => "France",
        ("GB", true) => "Великобритания", ("GB", false) => "United Kingdom",
        ("US", true) => "США", ("US", false) => "United States",
        ("CH", true) => "Швейцария", ("CH", false) => "Switzerland",
        ("RO", true) => "Румыния", ("RO", false) => "Romania",
        ("AL", true) => "Албания", ("AL", false) => "Albania",
        ("IT", true) => "Италия", ("IT", false) => "Italy",
        ("AT", true) => "Австрия", ("AT", false) => "Austria",
        ("PL", true) => "Польша", ("PL", false) => "Poland",
        ("ES", true) => "Испания", ("ES", false) => "Spain",
        ("CZ", true) => "Чехия", ("CZ", false) => "Czechia",
        ("LV", true) => "Латвия", ("LV", false) => "Latvia",
        ("LT", true) => "Литва", ("LT", false) => "Lithuania",
        ("EE", true) => "Эстония", ("EE", false) => "Estonia",
        ("NO", true) => "Норвегия", ("NO", false) => "Norway",
        ("DK", true) => "Дания", ("DK", false) => "Denmark",
        ("IE", true) => "Ирландия", ("IE", false) => "Ireland",
        ("PT", true) => "Португалия", ("PT", false) => "Portugal",
        ("BG", true) => "Болгария", ("BG", false) => "Bulgaria",
        ("TR", true) => "Турция", ("TR", false) => "Turkey",
        ("KZ", true) => "Казахстан", ("KZ", false) => "Kazakhstan",
        ("SG", true) => "Сингапур", ("SG", false) => "Singapore",
        ("JP", true) => "Япония", ("JP", false) => "Japan",
        _ when code.Length == 2 => code,
        _ => Ru ? "Страна неизвестна" : "Country unknown"
    };
    public static string BusinessSplit => Ru ? "Выбранные приложения и сайты" : "Selected apps and sites";
    public static string BusinessSplitDescription => Ru ? "Только рабочие сервисы по правилам компании" : "Only work services under company rules";
    public static string BusinessFull => Ru ? "Всё, кроме российских ресурсов и UIS" : "Everything except Russian resources and UIS";
    public static string BusinessFullDescription => Ru ? "Кроме российских ресурсов и UIS" : "Except Russian resources and UIS";
    public static string BusinessExternalTunnelBusy => Ru
        ? "VPN уже запущен другим процессом или службой. Дождитесь его завершения и повторите."
        : "VPN is already running in another process or service. Wait for it to finish and retry.";
    public static string BusinessConfigMissing => Ru
        ? "Не удалось подготовить корпоративную конфигурацию. Обратитесь к администратору."
        : "The Business configuration could not be prepared. Contact your administrator.";
    public static string BusinessGeoMissing => Ru
        ? "Нет данных для обхода российских ресурсов. Переустановите корпоративную сборку."
        : "Russian bypass data is missing. Reinstall the Business build.";
    public static string BusinessDiagnostics => Ru ? "Сохранить отчёт локально" : "Save report locally";
    public static string BusinessOpen => Ru ? "Открыть VPNRouter Business" : "Open VPNRouter Business";
    public static string BusinessServer => Ru ? "Сервер VPN" : "VPN server";
    public static string BusinessConnect => Ru ? "Подключиться" : "Connect";
    public static string BusinessDisconnect => Ru ? "Отключиться" : "Disconnect";
    public static string BusinessServerMenu => Ru ? "Сервер" : "Server";
    public static string BusinessModeMenu => Ru ? "Режим VPN" : "VPN mode";
    public static string BusinessServerRefresh => Ru ? "Проверить серверы" : "Test servers";
    public static string BusinessServerRefreshChecking => Ru ? "Проверяем…" : "Checking…";
    public static string BusinessServerChecking => Ru ? "Проверяем VPN…" : "Checking VPN…";
    public static string BusinessServerNotChecked => Ru ? "Не проверен" : "Not checked";
    public static string BusinessServerUnsupportedByCheck => Ru
        ? "Проверка не поддерживается — выберите вручную"
        : "Check unsupported; choose manually";
    public static string BusinessServerReachable => Ru ? "Работает" : "Works";
    public static string BusinessServerSlow => Ru ? "Медленный VPN" : "Slow VPN";
    public static string BusinessServerSlowShort => Ru ? "медл." : "slow";
    public static string BusinessServerNoReply => Ru ? "Проверка не прошла" : "Check failed";
    public static string BusinessServerNoReplyShort => Ru ? "нет ответа" : "no reply";
    public static string BusinessServerInconclusive => Ru ? "Неясно" : "Inconclusive";
    public static string BusinessServerLatency(int ms) => Ru ? $"{ms} мс" : $"{ms} ms";
    public static string BusinessServerSubMillisecond => Ru ? "<1 мс" : "<1 ms";
    public static string BusinessServerHealthHint => Ru
        ? "Сквозная HTTPS-проверка через выбранный сервер каждые 30 с при отключённом VPN. VPN при этом не запускается."
        : "End-to-end HTTPS check through the selected server every 30 s while VPN is off. It does not start the VPN.";
    public static string BusinessServerHealthLegend => Ru
        ? "Зелёный: HTTPS прошёл через сервер, до 800 мс. Жёлтый: медленнее 800 мс. Красный: проверка не прошла. Серый: не проверен или протокол не поддерживает проверку. Проверка приостановлена, пока VPN подключается или подключён."
        : "Green: HTTPS completes through the server within 800 ms. Yellow: slower than 800 ms. Red: check failed. Grey: not checked or the protocol cannot be checked. Checks pause while VPN is connecting or connected.";
    public static string BusinessServerHint => Ru
        ? "Автоматический режим выбирает самый быстрый рабочий сервер. Чтобы выбрать вручную, укажите сервер в списке: выбор сохранится."
        : "Automatic mode picks the fastest working server. To choose manually, pick a server in the list; that choice is saved.";
    public static string BusinessAutoSelectServer => Ru
        ? "Автоматически выбирать лучший сервер"
        : "Automatically choose the best server";
    public static string BusinessAutoSelectHint => Ru
        ? "Перед подключением каждый сервер проверяется сквозным HTTPS-запросом через настроенный маршрут. Выбор сервера вручную отключает автоматический режим."
        : "Before connecting, each server is checked with an end-to-end HTTPS request through its configured route. Picking a server manually turns automatic mode off.";
    public static string BusinessAutoSelecting => Ru
        ? "Проверяем серверы…"
        : "Checking servers…";
    public static string BusinessAutoFastest(string name, int ms) => Ru
        ? $"По проверке лучший: {name} · {ms} мс"
        : $"Latest check: {name} · {ms} ms";
    public static string BusinessAutoNoneWorking => Ru
        ? "Ни один сервер не прошёл сквозную проверку. Повторите позже или выберите сервер вручную."
        : "No server passed the end-to-end check. Try again later or choose a server manually.";
    public static string BusinessAutoCheckUnavailable => Ru
        ? "Автовыбор недоступен — выберите сервер вручную."
        : "Automatic selection is unavailable. Choose a server manually.";
    public static string BusinessServerAutoStale => Ru
        ? "Предыдущая проверка устарела — сервер выберется при подключении."
        : "Previous check expired — server will be chosen on connect.";
    public static string BusinessServerPickerAutoCaption => Ru
        ? "Или выберите сервер вручную (сохранён):"
        : "Or choose a server manually (saved):";
    public static string BusinessServerPickerManualCaption => Ru
        ? "Сохранённый сервер:"
        : "Saved server:";
    public static string BusinessServerLockedActive(string name) => Ru
        ? $"Активный сервер: {name}. Отключите VPN, чтобы изменить."
        : $"Active server: {name}. Disconnect VPN to change.";
    public static string BusinessServerLocked => Ru
        ? "Отключите VPN, чтобы изменить сервер."
        : "Disconnect VPN to change server.";
    public static string BusinessModeUnknown => Ru ? "Не определён" : "Unknown";
    public static string BusinessModeLocked => Ru
        ? "Отключите VPN, чтобы изменить режим."
        : "Disconnect VPN to change mode.";
    public static string BusinessServerSummaryActive(string name) => Ru
        ? $"Активный: {name}"
        : $"Active: {name}";
    public static string BusinessServerSummaryActiveUnknown => Ru ? "Подключён" : "Connected";
    public static string BusinessServerSummaryActiveService => Ru ? "Подключён (служба)" : "Connected (service)";
    public static string BusinessServerSummaryAutoChecked(string name, int ms) => Ru
        ? $"Авто · проверка: {name} ({ms} мс)"
        : $"Auto · check: {name} ({ms} ms)";
    public static string BusinessServerSummaryAutoChecking => Ru ? "Авто · проверка…" : "Auto · checking…";
    public static string BusinessServerSummaryAutoPending => Ru ? "Авто · выбор при старте" : "Auto · on connect";
    public static string BusinessServerSummaryAutoUnavailable => Ru ? "Авто · нужен ручной" : "Auto · pick manually";
    public static string BusinessServerSummaryManual(string name) => Ru
        ? $"Вручную · {name}"
        : $"Manual · {name}";
    public static string BusinessServerSummaryManualWithTag(string name, string tag) => Ru
        ? $"Вручную · {name} ({tag})"
        : $"Manual · {name} ({tag})";
    public static string BusinessPreviousVersions => Ru ? "Предыдущие версии" : "Previous versions";
    public static string BusinessRollbackSafetyHint => Ru
        ? "Доступны только проверенные стабильные версии."
        : "Only verified stable releases are available.";
    public static string BusinessUpdateApplying => Ru
        ? "Останавливаем VPN и устанавливаем обновление…"
        : "Stopping VPN and installing the update…";
    public static string BusinessUpdateStopNote => Ru
        ? "VPN остановится на время обновления."
        : "VPN will stop during the update.";
    public static string BusinessServerSaveFailed => Ru
        ? "Не удалось сохранить выбранный сервер. Проверьте права доступа к настройкам."
        : "The selected server could not be saved. Check access to the settings folder.";
    public static string BusinessDiagnosticsReceived => Ru ? "Отчёт получен." : "Report received.";
    public static string BusinessDiagnosticsReplyPending => Ru ? "Отчёт получен. Получаем короткий ответ…" : "Report received. Getting a short reply…";
    public static string BusinessDiagnosticsReplyQueued => Ru ? "Отчёт получен. Разбор ещё готовится." : "Report received. Analysis is still pending.";
    public static string BusinessDiagnosticsReplyWaitCancelled => Ru ? "Отчёт получен. Ожидание разбора отменено." : "Report received. Waiting for analysis was cancelled.";
    public static string BusinessDiagnosticsReplyUnavailable => Ru ? "Отчёт получен. Краткий разбор сейчас недоступен." : "Report received. A short analysis is currently unavailable.";
}
