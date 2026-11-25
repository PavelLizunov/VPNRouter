namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessRepairTitle => Ru ? "Исправить подключение" : "Fix connection";
    public static string BusinessRepairAnalyze => Ru ? "Отправить отчёт в DeepSeek" : "Send report to DeepSeek";
    public static string BusinessRepairClose => Ru ? "Закрыть" : "Close";
    public static string BusinessRepairConsent => Ru
        ? "Поддержка компании получит логи с именами ПК и пользователя, возможны адреса и названия приложений."
        : "Company support will receive logs with your PC and user names. They may include addresses and app names.";
    public static string BusinessRepairChecking => Ru ? "Ищем рабочий сервер…" : "Finding a working server…";
    public static string BusinessRepairFailed => Ru ? "Не удалось подключиться." : "Couldn’t connect.";
}
