namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string ToolsSection => Ru ? "Инструменты" : "Tools";
    public static string RunDiagnostics => Ru ? "Запустить диагностику" : "Run diagnostics";
    public static string ClearDiscordCache => Ru ? "Очистить кэш Discord" : "Clear Discord cache";
    public static string UpdateHostsFile => Ru ? "Обновить hosts (Flowseal)" : "Update hosts (Flowseal)";
    public static string OpenServiceMenu => Ru
        ? "Обслуживание Zapret"
        : "Zapret maintenance";
    public static string TipOpenServiceMenu => Ru
        ? "Повторно скачать пакет, настроить службу zapret для выбранной стратегии или открыть настройки автозапуска VPNRouter."
        : "Download the package again, apply an installed strategy to the zapret service or open VPNRouter Autostart settings.";

    public static string ZapretSecStatus       => Ru ? "Статус" : "Status";
    public static string ZapretSecStrategy     => Ru ? "Стратегия" : "Strategy";
    public static string ZapretSecHosts        => Ru ? "Хосты" : "Hosts";
    public static string ZapretSecFilters      => Ru ? "Фильтры" : "Filters";
    public static string ZapretSecUpdates      => Ru ? "Обновления" : "Updates";
    public static string ZapretSecDiagnostics  => Ru ? "Диагностика" : "Diagnostics";
    public static string ZapretSecAdvanced     => Ru ? "Прочее" : "Advanced";

    public static string ZapretSecStrategyDesc => Ru
        ? "Технология обхода DPI. Если одна не работает — попробуйте другую."
        : "DPI bypass technique. If one doesn't work — try another.";
    public static string ZapretSecHostsDesc => Ru
        ? "Правки файла hosts: Discord voice + Flowseal-список."
        : "Hosts-file overrides: Discord voice + Flowseal list.";
    public static string ZapretSecFiltersDesc => Ru
        ? "Какой трафик пропускать через обход DPI."
        : "Which traffic to route through DPI bypass.";
    public static string ZapretSecAdvancedDesc => Ru
        ? "Диагностика и управление службой. Большинству не нужно."
        : "Diagnostics and service controls. Not needed for most users.";

    public static string GameFilter => Ru ? "Игровой фильтр (диапазон 1024-65535)" : "Game filter (port range 1024-65535)";
    public static string GameFilterOff => Ru ? "Выкл" : "Off";
    public static string GameFilterAll => Ru ? "TCP + UDP" : "TCP + UDP";
    public static string GameFilterTcp => "TCP";
    public static string GameFilterUdp => "UDP";

    public static string IpSetFilter => Ru ? "IPSet фильтр" : "IPSet filter";
    public static string IpSetAny => Ru ? "Все (весь трафик)" : "Any (all traffic)";
    public static string IpSetLoaded => Ru ? "Из файла (список загружен)" : "Loaded (from list file)";
    public static string IpSetNone => Ru ? "Выкл" : "Off";

    public static string UpdateIpSet => Ru ? "Обновить IPSet список" : "Update IPSet list";
    public static string AutoUpdateCheckLabel => Ru
        ? "Авто-проверка обновлений Zapret"
        : "Auto-check Zapret updates";

    public static string RunTestsLabel => Ru ? "Проверить HTTPS-соединения" : "Test HTTPS connectivity";
    public static string ZapretApplyServiceStrategy => Ru
        ? "Установить службу zapret или применить стратегию"
        : "Install zapret service or apply strategy";
    internal static string ZapretSelectInstalled => Ru
        ? "Выберите стратегию из установленного пакета на вкладке «Стратегия» и повторите."
        : "Choose an installed strategy in the Strategy tab and try again.";
    internal static string ZapretServiceNeedsAdmin => Ru
        ? "Откройте VPNRouter от имени администратора для настройки службы zapret."
        : "Open VPNRouter as administrator to configure the zapret service.";
    internal static string ZapretStartServiceFailed => Ru
        ? "Стратегия сохранена. Проверьте состояние zapret в оснастке «Службы» Windows и повторите запуск."
        : "Strategy saved. Check zapret in Windows Services and retry starting it.";
    internal static string ZapretOtherHelper => Ru
        ? "Остановите активный обход в приложении, которое его запустило. Для службы zapret откройте «Службы» Windows."
        : "Stop the active bypass in the app that started it. For the zapret service, use Windows Services.";
    internal static string ZapretServiceOutcome(Services.ZapretActions.ServiceApply outcome) => outcome switch
    {
        Services.ZapretActions.ServiceApply.MissingPackage => ZapretRepairPackage,
        Services.ZapretActions.ServiceApply.MissingStrategy => ZapretSelectInstalled,
        Services.ZapretActions.ServiceApply.Created => Ru ? "Служба zapret установлена с автоматическим запуском. Windows получила запрос на запуск." : "Installed zapret for automatic startup. Windows accepted the start request.",
        Services.ZapretActions.ServiceApply.Running => Ru ? "Стратегия применена. Служба zapret запущена." : "Strategy applied. The zapret service is running.",
        Services.ZapretActions.ServiceApply.Disabled => Ru ? "Стратегия сохранена. Служба zapret отключена в настройках Windows." : "Strategy saved. The zapret service is disabled in Windows.",
        Services.ZapretActions.ServiceApply.Stopped => Ru ? "Стратегия сохранена. Запустить zapret можно в оснастке «Службы» Windows." : "Strategy saved. To start zapret, open Windows Services.",
        _ => Ru ? "Не удалось подтвердить владельца службы zapret или WinDivert. Проверьте её в приложении, которое её установило." : "Could not verify ownership of zapret or WinDivert. Manage the registration in the app that installed it.",
    };

    internal static string ZapretStopPending => Ru
        ? "Не удалось подтвердить остановку Zapret. Повторите остановку."
        : "Zapret exit is unconfirmed. Try stopping it again.";

    internal static string MainServiceOwnerConflict => Ru
        ? "Образ службы не соответствует этой редакции. Для службы в другом каталоге используйте CLI её установки."
        : "The registered service image does not match this edition. Use the CLI from its installation to manage a relocated service.";

    internal static string UpdateInstallNotStarted => Ru
        ? "Не удалось запустить установку обновления. Повторите попытку."
        : "The update installer did not start. Try again.";

    internal static string ZapretRestartPending => Ru
        ? "Дождитесь завершения текущей операции и повторите перезапуск."
        : "Wait for the current operation to finish, then retry the restart.";

    internal static string ZapretQuitPending => Ru
        ? "VPNRouter остаётся открытым. Завершение работы Zapret не подтверждено. Повторите «Выход»."
        : "VPNRouter is staying open. Zapret shutdown is unconfirmed. Choose Quit again.";

    internal static string ZapretRepairPackage => Ru
        ? "Откройте «Прочее» → «Обслуживание Zapret» и скачайте пакет заново."
        : "Open Advanced → Zapret maintenance and download the package again.";

    internal static string ZapretHttpsResult(bool reachable) => reachable
        ? (Ru ? "HTTPS доступен" : "HTTPS reachable")
        : (Ru ? "Ошибка HTTPS" : "HTTPS failed");

    public static string RemoveServiceLabel => Ru ? "Удалить службу Zapret" : "Remove Zapret service";

    public static string ApplyChanges => Ru ? "↻  Применить изменения" : "↻  Apply changes";
    public static string ChangesApplied => Ru ? "Изменения применены" : "Changes applied";
    public static string ApplyFailed => Ru ? "Не удалось применить" : "Apply failed";

    public static string AddCategory => Ru ? "+ Новая категория" : "+ New category";
    public static string EnableWholeGroup => Ru ? "Включить всю группу" : "Enable whole group";
    public static string CategoryNamePrompt => Ru ? "Имя категории:" : "Category name:";
    public static string AddAppHint => Ru ? "Discord.exe или путь к .exe" : "Discord.exe or path to .exe";

}
