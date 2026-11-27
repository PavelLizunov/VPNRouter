namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string AssistantTitle => Ru ? "Помощник VPNRouter" : "VPNRouter Assistant";
    internal static string AssistantDiagnostics => Ru ? "Диагностика и восстановление" : "Diagnostics and repair";
    public static string AssistantGoalHint => Ru ? "Что сделать с VPN?" : "What would you like to do?";
    public static string AssistantDataNotice => Ru
        ? "Для онлайн-запросов сервис передаёт текст, язык, состояние VPN и список действий поставщику модели."
        : "For online requests, the service shares your text, language, VPN state and available actions with its model provider.";
    internal static string AssistantCrashNotice => Ru
        ? "Отправляются только 4 кода последнего сбоя и состояние VPN (без конфигов и сырых логов)."
        : "Sends only 4 classified crash codes and VPN state (no configs or raw logs).";
    public static string AssistantDiagnoseCrash => Ru ? "Разобрать сбой" : "Analyze crash";
    public static string AssistantDiagnosingCrash => Ru ? "Проверяю отчёт о последнем сбое…" : "Checking the last crash report…";
    public static string AssistantNoCrash => Ru ? "Сохранённый отчёт о сбое не найден." : "No saved crash report was found.";
    public static string AssistantPlan => Ru ? "Отправить" : "Send";
    public static string AssistantPlanning => Ru ? "Готовлю ответ…" : "Preparing a response…";
    public static string AssistantConfirm => Ru ? "Применить" : "Apply";
    public static string AssistantSingleCommand => Ru
        ? "Что именно вы хотите изменить? Опишите одно действие."
        : "What would you like to change? Describe one action.";
    internal static string AssistantSuggestedCommand(string command) => Ru
        ? $"Настройки не изменены. Если это ваша цель, введите отдельную команду «{command}»."
        : $"Your settings are unchanged. If this is what you intended, enter the separate command “{command}”.";
    internal static string? AssistantCommandExample(string name, bool? value, string? app) => name switch
    {
        "SetVpnConnected" => value == true ? (Ru ? "Включи VPN" : "Connect VPN") : (Ru ? "Отключи VPN" : "Disconnect VPN"),
        "SetStrictDns" => value == true ? (Ru ? "Включи строгий DNS" : "Enable strict DNS") : (Ru ? "Отключи строгий DNS" : "Disable strict DNS"),
        "SetFlushDnsOnStart" => value == true ? (Ru ? "Включи сброс DNS при запуске" : "Enable DNS flushing on startup")
            : (Ru ? "Отключи сброс DNS при запуске" : "Disable DNS flushing on startup"),
        "OpenServerSettings" => Ru ? "Открой серверы" : "Open server settings",
        "OpenSubscriptions" => Ru ? "Открой подписки" : "Open subscriptions",
        "OpenNetworkSettings" => Ru ? "Открой сеть" : "Open network settings",
        "OpenApplications" => Ru ? "Открой приложения" : "Open applications",
        "OpenTools" => Ru ? "Открой инструменты" : "Open tools",
        "OpenFreeConfigurations" => Ru ? "Открой бесплатные конфигурации" : "Open free configurations",
        "RouteOnlyApplicationForDefaultBrowser" or "RouteOnlyApplicationForResolvedTarget" when app != null =>
            Ru ? $"Только {app} через VPN" : $"Route only {app} through the VPN",
        "RouteApplicationDirectForDefaultBrowser" or "RouteApplicationDirectForResolvedTarget" when app != null =>
            Ru ? $"Пусти {app} напрямую" : $"Route {app} directly",
        _ => null,
    };
    public static string AssistantProposalMismatch => Ru
        ? "Сервис предложил другое действие. Уточните команду."
        : "The service suggested a different action. Please rephrase your command.";
    internal static string AssistantChangeDescription(string control, bool? desired, string? app, bool connected) =>
        control switch
        {
            "SetVpnConnected" when desired == true => Ru ? "Подключить VPN с текущими настройками?" : "Connect the VPN with your current settings?",
            "SetVpnConnected" => Ru ? "Отключить текущее VPN-соединение?" : "Disconnect the current VPN connection?",
            "SetFlushDnsOnStart" when desired == true => Ru ? "Очищать кэш DNS при запуске VPN?" : "Flush the DNS cache when the VPN starts?",
            "SetFlushDnsOnStart" => Ru ? "Сохранять кэш DNS при запуске VPN?" : "Keep the DNS cache when the VPN starts?",
            "SetStrictDns" => (desired == true
                ? (Ru ? "Включить строгий DNS?" : "Enable strict DNS?")
                : (Ru ? "Отключить строгий DNS?" : "Disable strict DNS?")) + RestartNotice(connected),
            "RouteOnlyApplication" => (Ru ? $"Заменить список приложений VPN? Только {app} будет использовать VPN. Остальные приложения пойдут напрямую."
                : $"Replace the VPN app list? Only {app} will use the VPN. Other apps will connect directly.")
                + (connected ? RestartNotice(true) : (Ru ? " VPN подключится." : " The VPN will connect.")),
            "RouteApplicationDirect" => (Ru ? $"Направить трафик {app} в обход VPN?" : $"Send {app} traffic directly, outside the VPN?") + RestartNotice(connected),
            "OpenServerSettings" => Ru ? "Перейти в расширенный режим и выбрать вкладку ручной настройки серверов?" : "Switch to Advanced mode and select manual server settings?",
            "OpenSubscriptions" => Ru ? "Перейти в расширенный режим и выбрать вкладку подписок?" : "Switch to Advanced mode and select subscriptions?",
            "OpenNetworkSettings" => OpenAssistantSection(TabSettings),
            "OpenApplications" => OpenAssistantSection(TabApps),
            "OpenTools" => OpenAssistantSection(TabAdvTools),
            "OpenFreeConfigurations" => OpenAssistantSection(TabFreeConfigs),
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

    private static string OpenAssistantSection(string section) =>
        Ru ? $"Открыть раздел «{section}» в расширенном режиме?" : $"Open {section} in Advanced mode?";

    private static string RestartNotice(bool connected) => connected
        ? (Ru ? " VPN применит настройки к текущему соединению." : " The VPN will reapply settings to the current connection.")
        : string.Empty;
    public static string AssistantUndo => Ru ? "Отменить изменения" : "Undo changes";
    internal static string AssistantUndoWorking => Ru ? "Восстанавливаю прежние настройки…" : "Restoring previous settings…";
    public static string AssistantCancel => Ru ? "Прервать" : "Cancel";
    public static string AssistantWorking => Ru ? "Настраиваю и проверяю маршрут…" : "Applying and checking the route…";
    public static string AssistantBusy => Ru
        ? "Завершите текущее изменение. Помощник работает с подключениями из этого окна."
        : "Finish the current change. The assistant works with connections started in this window.";
    public static string AssistantServiceManaged => Ru
        ? "Сейчас VPN управляет служба. Помощник может изменить подключение, запущенное из этого окна."
        : "The service is managing this VPN connection. The assistant can change a connection started in this window.";
    public static string AssistantSubscription => Ru ? "Выберите действующую подписку vpnctl." : "Select an active vpnctl subscription.";
    public static string AssistantAccessDenied => Ru ? "Доступ к помощнику ограничен. Проверьте подписку." : "Assistant access is restricted. Check your subscription.";
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
    public static string AssistantRepair => Ru ? "Восстановить VPN на устройстве" : "Repair VPN on this device";
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
    public static string AssistantDecline => Ru ? "Оставить как есть" : "Keep current settings";
    public static string AssistantNoChanges => Ru ? "Ничего не изменено." : "Nothing was changed.";
    internal static string AssistantSplitProfileChange => Ru ? " VPN переключится на профиль раздельной маршрутизации." : " The VPN will switch to the split-routing profile.";
    internal static string AssistantRegionalBypassChange => Ru ? " Исключение для российского трафика будет отключено." : " The Russian traffic bypass will be turned off.";
    public static string AssistantRateLimited => Ru ? "Лимит запросов исчерпан. Попробуйте позже." : "Request limit reached. Try again later.";
    public static string AssistantInvalidGoal => Ru ? "Введите короткую команду без данных подписки." : "Enter a short request without subscription details.";
    public static string AssistantUnavailable => Ru ? "Онлайн-помощник недоступен. Попробуйте позже." : "The online assistant is unavailable. Try again later.";
    public static string AssistantEndpointUnavailable => Ru
        ? "Сервер подписки пока не поддерживает эту версию помощника."
        : "The subscription server does not support this assistant version yet.";
    public static string AssistantCancelled => Ru ? "Команда прервана. Проверьте состояние VPN." : "Request cancelled. Check the VPN status.";
}
