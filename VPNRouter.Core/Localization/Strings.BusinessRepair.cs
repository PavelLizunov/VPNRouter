namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessRepairTitle => Ru ? "Исправить подключение с DeepSeek" : "Fix connection with DeepSeek";
    public static string BusinessRepairAnalyze => Ru ? "Отправить отчёт в DeepSeek" : "Send report for DeepSeek analysis";
    public static string BusinessRepairConnect => Ru ? "Проверить серверы и подключиться" : "Test servers and connect";
    public static string BusinessRepairClose => Ru ? "Закрыть" : "Close";
    public static string BusinessRepairConsent => Ru
        ? "Для разбора DeepSeek клиент отправит свежий отчёт в поддержку вашей компании. В нём есть имена компьютера и пользователя Windows. В журналах могут быть адреса и названия приложений."
        : "For DeepSeek analysis, the client will send a fresh report to your company’s support service. It includes the computer and Windows user names. Logs may contain addresses and app names.";
    public static string BusinessRepairLocalOnly => Ru
        ? "В этой сборке отправка в поддержку не настроена. Доступна локальная проверка серверов."
        : "Support upload is not configured for this build. You can test servers locally.";
    public static string BusinessRepairPlan => Ru
        ? "Клиент может проверить корпоративные профили запросом через VPN и подключиться через работающий сервер. Для этого отправка отчёта не требуется."
        : "The client can test the company profiles with a request through the VPN and connect through a working server. Uploading a report is optional.";
    public static string BusinessRepairActive => Ru
        ? "VPN подключён. Чтобы попробовать другой сервер, сначала отключитесь."
        : "VPN is connected. Disconnect first to try another server.";
    public static string BusinessRepairChecking => Ru ? "Проверяем серверы и подключаемся…" : "Testing servers and connecting…";
    public static string BusinessRepairConnected => Ru ? "VPN подключён через проверенный сервер." : "VPN connected through a verified server.";
    public static string BusinessRepairFailed => Ru
        ? "Не удалось подключиться через проверенный сервер. Подробности — в статусе подключения и диагностике."
        : "Could not connect through a verified server. See the connection status and diagnostics for details.";
}
