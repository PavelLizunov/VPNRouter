namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string AssistantTitle => Ru ? "Настроить с DeepSeek" : "Set up with DeepSeek";
    public static string AssistantGoalHint => Ru ? "Что настроить в VPNRouter?" : "What should VPNRouter do?";
    public static string AssistantDataNotice => Ru
        ? "Текст команды отправляется в DeepSeek."
        : "Your request is sent to DeepSeek.";
    public static string AssistantApply => Ru ? "Настроить" : "Apply";
    public static string AssistantUndo => Ru ? "Отменить изменения" : "Undo changes";
    public static string AssistantCancel => Ru ? "Прервать" : "Cancel";
    public static string AssistantWorking => Ru ? "Настраиваю и проверяю маршрут…" : "Applying and checking the route…";
    public static string AssistantBusy => Ru
        ? "Завершите текущее изменение. Помощник работает с подключениями из этого окна."
        : "Finish the current change. The assistant works with connections started in this window.";
    public static string AssistantServiceManaged => Ru
        ? "Сейчас VPN управляет служба. Помощник может изменить подключение, запущенное из этого окна."
        : "The service is managing this VPN connection. The assistant can change a connection started in this window.";
    public static string AssistantSubscription => Ru ? "Выберите действующую подписку vpnctl." : "Select an active vpnctl subscription.";
    public static string AssistantAccessDenied => Ru ? "Подписка не даёт доступа к DeepSeek." : "This subscription does not grant DeepSeek access.";
    public static string AssistantTunnelBusy => Ru ? "VPN-адаптер занят. Попробуйте ещё раз." : "The VPN adapter is busy. Try again.";
    public static string AssistantSettingsChanged => Ru ? "Настройки изменились. Запустите команду заново." : "Settings changed. Run the command again.";
    public static string AssistantRuleConflict => Ru ? "Вручную заданные правила могут изменить этот маршрут." : "Custom rules can change this route.";
    public static string AssistantAlreadySet => Ru ? "Для приложения уже настроен этот маршрут." : "The application already has this route policy.";
    public static string AssistantApplyFailed => Ru ? "Применение маршрута не подтверждено. Проверьте состояние VPN." : "The route change could not be confirmed. Check the VPN status.";
    public static string AssistantApplied => Ru ? "Правило для приложения применено. VPN готов." : "The application routing rule is active. The VPN is ready.";
    public static string AssistantAppliedWithNewerSettings => Ru
        ? "Правило для приложения применено. Более поздние правки настроек сохранены."
        : "The application routing rule is active. Your later settings edits were kept.";
    public static string AssistantUndone => Ru ? "Прежние настройки восстановлены." : "The previous settings were restored.";
    public static string AssistantUndoPreservedNewerSettings => Ru
        ? "Маршрут помощника отключён. Более поздние правки настроек сохранены."
        : "The assistant's route was stopped. Your later settings edits were kept.";
    public static string AssistantAppNeeded => Ru ? "Укажите имя приложения." : "Specify the application name.";
    public static string AssistantUnsupportedGoal => Ru ? "Пока можно настроить Что настроить в VPNRouter?." : "Specify what you want VPNRouter to do.";
    public static string AssistantFinished => Ru ? "Готово." : "Done.";
    public static string AssistantNoChanges => Ru ? "Изменений не потребовалось." : "No changes were needed.";
    public static string AssistantStepLimit => Ru ? "Достигнут лимит шагов. Уточните оставшуюся задачу." : "The step limit was reached. Specify what remains.";
    public static string AssistantRateLimited => Ru ? "Лимит запросов исчерпан. Попробуйте позже." : "Request limit reached. Try again later.";
    public static string AssistantInvalidGoal => Ru ? "Введите короткую команду без данных подписки." : "Enter a short request without subscription details.";
    public static string AssistantUnavailable => Ru ? "DeepSeek недоступен. Попробуйте позже." : "DeepSeek is unavailable. Try again later.";
    public static string AssistantCancelled => Ru ? "Команда прервана. Проверьте состояние VPN." : "Request cancelled. Check the VPN status.";
}
