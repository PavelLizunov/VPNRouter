namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessRepairTitle => Ru ? "Исправить подключение" : "Fix connection";
    public static string BusinessRepairConnectedTitle => Ru ? "Проблемы со связью?" : "Connection trouble?";
    public static string BusinessRepairAnalyze => Ru ? "Отправить отчёт в DeepSeek" : "Send report to DeepSeek";
    public static string BusinessRepairRetrySend => Ru ? "Повторить отправку" : "Retry sending";
    public static string BusinessRepairRetrySearch => Ru ? "Повторить поиск" : "Search again";
    public static string BusinessRepairCancel => Ru ? "Отмена" : "Cancel";
    public static string BusinessRepairClose => Ru ? "Закрыть" : "Close";
    public static string BusinessRepairStopWaiting => Ru ? "Не ждать ответ" : "Stop waiting";
    public static string BusinessRepairConsentTitle => Ru ? "Отправить диагностику?" : "Send diagnostics?";
    public static string BusinessRepairConsent => Ru
        ? "Поддержка компании получит логи с именами ПК и пользователя, возможны адреса и названия приложений."
        : "Company support will receive logs with your PC and user names. They may include addresses and app names.";
    public static string BusinessRepairConsentOnline => Ru
        ? "VPN останется включённым. Поддержка компании получит логи с именами ПК и пользователя, возможны адреса и названия приложений."
        : "VPN will stay connected. Company support will receive logs with your PC and user names. They may include addresses and app names.";
    public static string BusinessRepairChecking => Ru ? "Ищем рабочий сервер…" : "Finding a working server…";
    public static string BusinessRepairCheckingDetail => Ru
        ? "Обновляем список и проверяем серверы. Логи не отправляются."
        : "Refreshing list and testing servers. No logs are sent.";
    public static string BusinessRepairFailed => Ru ? "Не удалось подключиться." : "Couldn’t connect.";
    public static string BusinessRepairNoSupportDetail => Ru
        ? "Ни один сервер не ответил. Попробуйте позже или выберите сервер вручную."
        : "No server answered. Try again later or choose a server manually.";
    public static string BusinessRepairSendingTitle => Ru ? "Отправка диагностики" : "Sending diagnostics";
    public static string BusinessRepairSendFailedTitle => Ru ? "Не удалось отправить отчёт" : "Couldn’t send report";
    public static string BusinessRepairCollectFailedTitle => Ru ? "Не удалось собрать отчёт" : "Couldn’t collect report";
    public static string BusinessRepairReceiptTitle => Ru ? "Отчёт получен поддержкой" : "Report received";
    public static string BusinessRepairReceiptPending => Ru ? "Получаем короткий ответ…" : "Getting a short reply…";
    public static string BusinessRepairReceiptWaitCancelled => Ru ? "Ожидание разбора отменено." : "Waiting for analysis was cancelled.";
    public static string BusinessRepairReceiptQueued => Ru ? "Разбор ещё готовится." : "Analysis is still pending.";
    public static string BusinessRepairReceiptUnavailable => Ru ? "Краткий разбор сейчас недоступен." : "A short analysis is currently unavailable.";
    public static string BusinessRepairAdviceTitle => Ru ? "Отчёт получен · рекомендация" : "Report received · advice";
    public static string BusinessRepairAdviceFooter => Ru ? "Настройки VPN не изменены." : "VPN settings were not changed.";
}
