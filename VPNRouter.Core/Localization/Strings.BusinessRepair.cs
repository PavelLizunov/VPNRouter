namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessRepairTitle => Ru ? "Исправить подключение с DeepSeek" : "Fix connection with DeepSeek";
    public static string BusinessRepairAnalyze => Ru ? "Отправить на разбор" : "Send for analysis";
    public static string BusinessRepairConnect => Ru ? "Проверить серверы и подключиться" : "Test servers and connect";
    public static string BusinessRepairClose => Ru ? "Закрыть" : "Close";
    public static string BusinessRepairConsent => Ru
        ? "DeepSeek разберёт свежий отчёт в поддержке компании. В нём есть имена ПК и пользователя Windows, возможны адреса и названия приложений."
        : "Send a fresh report to company support for DeepSeek analysis. It includes your PC and Windows user names. Logs may contain addresses and app names.";
    public static string BusinessRepairLocalOnly => Ru
        ? "В этой сборке отправка в поддержку не настроена. Доступна локальная проверка серверов."
        : "Support upload is not configured for this build. You can test servers locally.";
    public static string BusinessRepairPlan => Ru
        ? "Можно проверить серверы и подключиться локально, без отправки отчёта."
        : "Test locally and connect through a working server. This action sends no report.";
    public static string BusinessRepairActive => Ru
        ? "VPN подключён. Чтобы попробовать другой сервер, сначала отключитесь."
        : "VPN is connected. Disconnect first to try another server.";
    public static string BusinessRepairChecking => Ru ? "Проверяем серверы и подключаемся…" : "Testing servers and connecting…";
    public static string BusinessRepairConnected => Ru ? "VPN подключён через проверенный сервер." : "VPN connected through a verified server.";
    public static string BusinessRepairFailed => Ru
        ? "Не удалось подключиться через проверенный сервер. Подробности — в статусе подключения и диагностике."
        : "Could not connect through a verified server. See the connection status and diagnostics for details.";
}
