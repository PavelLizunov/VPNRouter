namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    // ── Zapret tools ──
    public static string ToolsSection => Ru ? "Инструменты" : "Tools";
    public static string RunDiagnostics => Ru ? "Запустить диагностику" : "Run diagnostics";
    public static string ClearDiscordCache => Ru ? "Очистить кэш Discord" : "Clear Discord cache";
    public static string UpdateHostsFile => Ru ? "Обновить hosts (Flowseal)" : "Update hosts (Flowseal)";
    public static string OpenServiceMenu => Ru
        ? "Открыть меню service.bat"
        : "Open service.bat menu";
    // v2.31.0-r4 (F-15): tooltip on the service.bat menu button.
    public static string TipOpenServiceMenu => Ru
        ? "Открыть служебное меню Zapret (winws.exe service.bat) — установка/удаление службы и переключение стратегии."
        : "Open the Zapret service menu (winws.exe service.bat) — install/remove service and switch strategy.";

    // ── Zapret sections (master-detail) ──
    public static string ZapretSecStatus       => Ru ? "Статус" : "Status";
    public static string ZapretSecStrategy     => Ru ? "Стратегия" : "Strategy";
    public static string ZapretSecHosts        => "Hosts";
    public static string ZapretSecFilters      => Ru ? "Фильтры" : "Filters";
    public static string ZapretSecUpdates      => Ru ? "Обновления" : "Updates";
    public static string ZapretSecDiagnostics  => Ru ? "Диагностика" : "Diagnostics";
    public static string ZapretSecAdvanced     => Ru ? "Дополнительно" : "Advanced";

    // v2.31.6-r7 — section descriptions for the Zapret master-detail.
    // Iter#3 audit (2026-05-04) flagged the page as «возможно слишком
    // сложная» — 5 unlabelled sections looked intimidating to first-run
    // users who only wanted to click Start DPI Bypass. Adding a 1-line
    // hint under each section header so first-time visitors understand
    // each section's purpose at a glance and can ignore power-user
    // sections without feeling they're missing something. Status keeps
    // its existing LblDpiDescription which already serves this role.
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

    // Filters
    public static string GameFilter => Ru ? "Игровой фильтр (диапазон 1024-65535)" : "Game filter (port range 1024-65535)";
    public static string GameFilterOff => Ru ? "Выкл" : "Off";
    public static string GameFilterAll => Ru ? "TCP + UDP" : "TCP + UDP";
    public static string GameFilterTcp => "TCP";
    public static string GameFilterUdp => "UDP";

    public static string IpSetFilter => Ru ? "IPSet фильтр" : "IPSet filter";
    // v2.30.7-r4 — F-13 fix: "Any" / "Loaded" were left as English in the
    // RU dropdown, mixing inside an otherwise-Russian sub-section
    // (D1 violation). Localized while keeping the parenthetical
    // explainers intact.
    public static string IpSetAny => Ru ? "Все (весь трафик)" : "Any (all traffic)";
    public static string IpSetLoaded => Ru ? "Из файла (список загружен)" : "Loaded (from list file)";
    // v2.30.4-r1 (UX-51 fix): align off-state copy with GameFilterOff
    // ("Выкл" / "Off"). Pre-r1 had "None (отключено)" inconsistent with
    // the dropdown sibling.
    public static string IpSetNone => Ru ? "Выкл" : "Off";

    // Updates
    public static string UpdateIpSet => Ru ? "Обновить IPSet список" : "Update IPSet list";
    // v2.30.4-r1 (UX-52 fix): align case with the sub-tab name "Zapret"
    // (capitalized). Pre-r1 had "zapret" lowercase here while everywhere
    // else it's "Zapret" — inconsistent.
    public static string AutoUpdateCheckLabel => Ru
        ? "Авто-проверка обновлений Zapret"
        : "Auto-check Zapret updates";

    // Advanced
    public static string RunTestsLabel => Ru ? "Запустить тесты сети" : "Run network tests";
    public static string RemoveServiceLabel => Ru ? "Удалить службу Zapret" : "Remove Zapret service";

    public static string ApplyChanges => Ru ? "↻  Применить изменения" : "↻  Apply changes";
    public static string ChangesApplied => Ru ? "Изменения применены" : "Changes applied";
    public static string ApplyFailed => Ru ? "Не удалось применить" : "Apply failed";

    public static string AddCategory => Ru ? "+ Новая категория" : "+ New category";
    public static string EnableWholeGroup => Ru ? "Включить всю группу" : "Enable whole group";
    public static string CategoryNamePrompt => Ru ? "Имя категории:" : "Category name:";
    public static string AddAppHint => Ru ? "имя процесса (например Discord)" : "process name (e.g. Discord)";

}
