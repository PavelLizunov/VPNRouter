namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessRussian => "Русский";
    public static string BusinessEnglish => "English";
    public static string BusinessServiceConnected => Ru ? "Подключено через службу" : "Connected via service";
    public static string BusinessConnected => Ru ? "Подключено" : "Connected";
    public static string BusinessMenu => Ru ? "Меню" : "Menu";
    public static string BusinessLanguage => Ru ? "Язык" : "Language";
    public static string BusinessLanguageSaveFailed => Ru ? "Не удалось сохранить язык." : "Could not save language.";
    public static string BusinessDetails => Ru ? "Подробнее" : "Details";
    public static string BusinessFullCompact => Ru ? "Весь трафик с исключениями" : "All traffic with exceptions";
    public static string BusinessModesHint => Ru ? "Россия и UIS — напрямую." : "Russia and UIS use Direct.";
    public static string BusinessUpdateServers => Ru ? "Обновить список" : "Update list";
    public static string BusinessSubscriptionNotConfigured => Ru ? "Список встроен в эту сборку. Для обновления нужна новая версия." : "This build has an embedded list. Updating requires a newer build.";
    public static string BusinessSubscriptionUpdating => Ru ? "Обновляем список серверов…" : "Updating server list…";
    public static string BusinessSubscriptionUpdated(int count) => Ru ? $"Список обновлён. Серверов: {count}." : $"List updated. Servers: {count}.";
    public static string BusinessSubscriptionFailed => Ru ? "Список не обновлён. Сохранены прежние серверы. Повторите позже." : "Update failed. Your previous servers are retained. Try again later.";
    public static string BusinessSubscriptionCancelled => Ru ? "Обновление остановлено. Сохранены прежние серверы." : "Update stopped. Your previous servers are retained.";
    public static string BusinessDiagnosticsHeader => Ru ? "Диагностика" : "Diagnostics";
    public static string BusinessDiagnosticsLocalOnly => Ru ? "Можно собрать отчёт локально. Отправка в этой сборке не настроена." : "You can collect a local report. Upload is not configured in this build.";
    public static string BusinessDiagnosticsHint => Ru ? "Отчёт содержит имя компьютера и Windows-пользователя со слов клиента. Отправка — только по кнопке. Пароли маскируются; в логах могут остаться адреса и названия приложений." : "The report includes client-reported computer and Windows user names. Upload happens only on Send. Secrets are masked; logs may retain addresses and app names.";
    public static string BusinessDiagnosticsCollect => Ru ? "Сохранить локально" : "Save locally";
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
    public static string BusinessCountryHint => Ru ? "Страна узла из vpnctld или явного названия подписки" : "Node country from vpnctld or an explicit subscription label";
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
        _ when code.Length == 2 => code,
        _ => Ru ? "Страна неизвестна" : "Country unknown"
    };
    public static string BusinessSplit => Ru ? "Выбранные приложения и сайты" : "Selected apps and sites";
    public static string BusinessFull => Ru ? "Всё, кроме российских ресурсов и UIS" : "Everything except Russian resources and UIS";
    public static string BusinessHint => Ru
        ? "Российские ресурсы и телефония UIS направляются напрямую."
        : "Russian resources and UIS telephony are routed directly.";
    public static string BusinessConfigMissing => Ru
        ? "Не удалось подготовить корпоративную конфигурацию. Обратитесь к администратору."
        : "The Business configuration could not be prepared. Contact your administrator.";
    public static string BusinessGeoMissing => Ru
        ? "Нет данных для обхода российских ресурсов. Переустановите корпоративную сборку."
        : "Russian bypass data is missing. Reinstall the Business build.";
    public static string BusinessDiagnostics => Ru ? "Сохранить диагностику" : "Save diagnostics";
    public static string BusinessOpen => Ru ? "Открыть VPNRouter Business" : "Open VPNRouter Business";
    public static string BusinessServer => Ru ? "Сервер VPN" : "VPN server";
    public static string BusinessServerRefresh => Ru ? "Обновить пинг" : "Refresh ping";
    public static string BusinessServerChecking => Ru ? "Проверка…" : "Checking…";
    public static string BusinessServerNotChecked => Ru ? "Не проверен" : "Not checked";
    public static string BusinessServerReachable => Ru ? "Порт доступен" : "Port reachable";
    public static string BusinessServerSlow => Ru ? "Медленный ответ" : "Slow response";
    public static string BusinessServerNoReply => Ru ? "Нет ответа" : "No response";
    public static string BusinessServerInconclusive => Ru ? "Неясно" : "Inconclusive";
    public static string BusinessServerLatency(int ms) => Ru ? $"{ms} мс" : $"{ms} ms";
    public static string BusinessServerSubMillisecond => Ru ? "<1 мс" : "<1 ms";
    public static string BusinessServerHealthHint => Ru
        ? "Доступность порта и пинг: проверка каждые 30 с при отключённом VPN. Это не проверка работы VPN."
        : "Port reachability and ping: checked every 30 s while VPN is off. This does not verify VPN traffic.";
    public static string BusinessServerHealthLegend => Ru
        ? "Зелёный: порт отвечает, до 800 мс. Жёлтый: медленнее 800 мс или результат неясен. Красный: нет ответа. Серый: не проверен. Проверка приостановлена, пока VPN подключается или подключён."
        : "Green: port responds within 800 ms. Yellow: slower than 800 ms or inconclusive. Red: no response. Grey: not checked. Probes pause while VPN is connecting or connected.";
    public static string BusinessServerHint => Ru
        ? "Если VPN недоступен: отключитесь, выберите другой сервер и подключитесь снова. Выбор сохраняется."
        : "If VPN is unavailable, disconnect, choose another server and reconnect. Your choice is saved.";
    public static string BusinessServerSaveFailed => Ru
        ? "Не удалось сохранить выбранный сервер. Проверьте права доступа к настройкам."
        : "The selected server could not be saved. Check access to the settings folder.";
    public static string BusinessDiagnosticsReceived => Ru ? "Отчёт получен." : "Report received.";
    public static string BusinessDiagnosticsReplyPending => Ru ? "Отчёт получен. Получаем короткий ответ…" : "Report received. Getting a short reply…";
    public static string BusinessDiagnosticsReplyUnavailable => Ru ? "Отчёт получен. Краткий разбор сейчас недоступен." : "Report received. A short analysis is currently unavailable.";
}
