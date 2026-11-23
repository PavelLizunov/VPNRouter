namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string AssistantTitle => Ru ? "Настроить с DeepSeek" : "Set up with DeepSeek";
    public static string AssistantGoalHint => Ru ? "Что настроить в VPNRouter?" : "What should VPNRouter do?";
    public static string AssistantDataNotice => Ru
        ? "Текст команды отправляется в DeepSeek."
        : "Your request is sent to DeepSeek.";
    public static string AssistantDiagnoseCrash => Ru ? "Разобрать сбой" : "Analyze crash";
    public static string AssistantDiagnosingCrash => Ru ? "Проверяю отчёт о последнем сбое…" : "Checking the last crash report…";
    public static string AssistantNoCrash => Ru ? "Сохранённый отчёт о сбое не найден." : "No saved crash report was found.";
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
    public static string AssistantRouteScanNeeded => Ru ? "Сначала обновите список приложений для маршрутизации." : "Refresh the routing application list first.";
    public static string AssistantAppNeeded => Ru
        ? "Укажите один браузер или приложение, например Edge или Telegram. Если имя не распознаётся, запустите программу и укажите имя процесса."
        : "Name one browser or app, such as Edge or Telegram. If the name is unclear, open the app and enter its process name.";
    public static string AssistantUnsupportedGoal => Ru ? "Уточните, что нужно сделать в VPNRouter." : "Specify what you want VPNRouter to do.";
    public static string AssistantPageOpened => Ru ? "Раздел открыт." : "Section opened.";
    public static string AssistantVpnActive => Ru ? "VPN подключён." : "VPN connected.";
    public static string AssistantVpnStopped => Ru ? "VPN отключён." : "VPN disconnected.";
    public static string AssistantRepair => Ru ? "Починить VPN" : "Repair VPN";
    public static string AssistantRepairWorking => Ru ? "Проверяю настройки и подключение…" : "Checking settings and connectivity…";
    public static string AssistantRepairFailed => Ru ? "Проверка VPN завершилась ошибкой." : "The VPN check failed.";
    public static string AssistantRepairInvalidSettings => Ru ? "Конфигурация не прошла проверку. Откройте настройки сети." : "The configuration did not pass validation. Open Network settings.";
    public static string AssistantRepairNoServers => Ru ? "Для этого профиля нет доступных серверов. Проверьте серверы или обновите подписку." : "This profile has no available servers. Check Servers or refresh the subscription.";
    public static string AssistantRepairStartupFailed => Ru ? "Не удалось подтвердить запуск VPN. Подробности запуска есть в журнале." : "VPN startup could not be verified. See the startup log for details.";
    public static string AssistantRepairCustom => Ru ? "Для ремонта пользовательского конфига нужен разбор его правил." : "The custom configuration needs a rule review.";
    public static string AssistantRepairAutoRoute => Ru ? "Автоматическая маршрутизация TUN отключена. Проверьте её в настройках сети." : "TUN automatic routing is off. Check it in Network settings.";
    public static string AssistantRepairEmptyInclude => Ru ? "Режим «Только выбранные» включён. Выберите приложения для VPN." : "Selected-only routing is enabled. Choose the apps that need VPN.";
    public static string AssistantRepairReachable => Ru ? "Проверочный запрос через VPN прошёл." : "The VPN connectivity check succeeded.";
    public static string AssistantRepairRunningUnreachable => Ru ? "Туннель запущен, но проверочный запрос через VPN не прошёл." : "The tunnel is running, but the VPN connectivity check failed.";
    public static string AssistantRepairMtu(int mtu) => Ru ? $"VPN проверен с MTU {mtu}." : $"VPN connectivity verified with MTU {mtu}.";
    public static string AssistantRepairAlternate(int? mtu) => mtu is int value
        ? (Ru ? $"VPN проверен на другом сервере с MTU {value}." : $"VPN connectivity verified on another server with MTU {value}.")
        : (Ru ? "VPN проверен на другом сервере этой подписки." : "VPN connectivity verified on another server in this subscription.");
    public static string AssistantRepairNoAutomaticServer => Ru
        ? "Проверка связи через VPN не прошла. В текущем выборе нет другого сервера для автоподключения."
        : "The VPN check failed. The current selection has no eligible automatic alternative.";
    public static string AssistantRepairNoReachableServer => Ru
        ? "Проверенные альтернативы не подтвердили связь. Сохранены прежние настройки."
        : "The tested alternatives did not verify connectivity. Existing settings were kept.";
    public static string AssistantDirectFullConflict => Ru
        ? "В режиме «Весь трафик» сохранены исключения. Выберите нужные исключения в настройках приложений."
        : "Full tunnel has saved exclusions. Choose which apps to exclude in application settings.";
    public static string AssistantPreferenceSaved => Ru ? "Настройка сохранена и проверена." : "The saved setting was verified.";
    public static string AssistantUnconfirmed => Ru ? "Помощник не подтвердил результат команды." : "The assistant could not verify the requested outcome.";
    public static string AssistantNoChanges => Ru ? "Изменений не потребовалось." : "No changes were needed.";
    public static string AssistantStepLimit => Ru ? "Достигнут лимит шагов. Уточните оставшуюся задачу." : "The step limit was reached. Specify what remains.";
    public static string AssistantRateLimited => Ru ? "Лимит запросов исчерпан. Попробуйте позже." : "Request limit reached. Try again later.";
    public static string AssistantInvalidGoal => Ru ? "Введите короткую команду без данных подписки." : "Enter a short request without subscription details.";
    public static string AssistantUnavailable => Ru ? "DeepSeek недоступен. Попробуйте позже." : "DeepSeek is unavailable. Try again later.";
    public static string AssistantEndpointUnavailable => Ru
        ? "Сервер подписки пока не поддерживает эту версию помощника."
        : "The subscription server does not support this assistant version yet.";
    public static string AssistantCancelled => Ru ? "Команда прервана. Проверьте состояние VPN." : "Request cancelled. Check the VPN status.";
}
