namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string SmpMenuSetupWizard => Ru ? "Мастер настройки и диагностики" : "Setup and diagnostics wizard";
    public static string TipSmpMenuSetupWizard => Ru
        ? "Проверить подключение и безопасно восстановить MTU и режим маршрутизации."
        : "Check the connection and safely restore MTU and routing mode.";

    public static string SetupWizardTitle => Ru ? "Мастер настройки VPN" : "VPN setup wizard";
    public static string SetupWizardSubtitle => Ru
        ? "Проверит конфигурацию и поможет восстановить безопасные сетевые настройки."
        : "Checks the configuration and helps restore safe network settings.";
    public static string SetupWizardProgress(int step) => Ru ? $"Шаг {step} из 4" : $"Step {step} of 4";

    public static string SetupWizardRoutingTitle => Ru ? "Как направлять трафик?" : "How should traffic be routed?";
    public static string SetupWizardRoutingBody => Ru
        ? "Выберите режим. Мастер применит его только после явного восстановления настроек."
        : "Choose a mode. The wizard applies it only when you explicitly restore settings.";
    public static string SetupWizardSplitTitle => Ru ? "Только выбранные приложения" : "Selected apps only";
    public static string SetupWizardSplitHint => Ru
        ? "Остальной трафик идёт напрямую. Подходит для игр, мессенджеров и банков."
        : "Other traffic stays direct. Good for games, messengers and banking apps.";
    public static string SetupWizardFullTitle => Ru ? "Весь трафик" : "All traffic";
    public static string SetupWizardFullHint => Ru
        ? "Все приложения используют VPN. Проще, но может увеличить задержку."
        : "Every app uses the VPN. Simpler, but latency may increase.";
    public static string SetupWizardKillSwitchTitle => Ru ? "Блокировка при сбое" : "Block on VPN failure";
    public static string SetupWizardKillSwitchBody => Ru
        ? "Зависит от активного профиля и платформы. Мастер не меняет правила firewall и не имитирует аварию."
        : "Depends on the active profile and platform. The wizard does not change firewall rules or simulate a failure.";

    public static string SetupWizardChecksTitle => Ru ? "Проверка соединения" : "Connection check";
    public static string SetupWizardChecksBody => Ru
        ? "Проверим конфиг, TUN, DNS, сервис и доступность сети. Проверку безопасно запускать в подключённом и отключённом состоянии."
        : "Checks config, TUN, DNS, service and network reachability. It is safe while connected or disconnected.";
    public static string SetupWizardRunChecks => Ru ? "Запустить проверку" : "Run checks";
    public static string SetupWizardChecksNotRun => Ru ? "Проверка ещё не запускалась." : "Checks have not run yet.";
    public static string SetupWizardChecksRunning => Ru ? "Проверяю…" : "Checking…";
    public static string SetupWizardChecksPassed => Ru ? "Критических проблем не найдено." : "No critical problems found.";
    public static string SetupWizardChecksSummary(int warnings, int errors) => Ru
        ? $"Предупреждений: {warnings}, ошибок: {errors}."
        : $"Warnings: {warnings}, errors: {errors}.";
    public static string SetupWizardCheckFailed => Ru ? "Не удалось выполнить проверку." : "The check could not be completed.";
    public static string SetupWizardCheckOk => Ru ? "ГОТОВО" : "OK";
    public static string SetupWizardCheckWarning => Ru ? "ВНИМАНИЕ" : "WARN";
    public static string SetupWizardCheckError => Ru ? "ОШИБКА" : "ERROR";

    public static string SetupWizardRepairTitle => Ru ? "Безопасное восстановление" : "Safe network repair";
    public static string SetupWizardRepairBody => Ru
        ? "Сбросьте только MTU или примените выбранный режим вместе с безопасным MTU. Остальные настройки мастер не тронет."
        : "Reset only MTU, or apply the selected routing mode together with a safe MTU. Other settings are untouched.";
    public static string SetupWizardCurrentMtu(int mtu) => Ru ? $"Текущий MTU: {mtu}" : $"Current MTU: {mtu}";
    public static string SetupWizardMtuDefault => Ru ? "Стандартное безопасное значение — 1420." : "The safe default is 1420.";
    public static string SetupWizardMtuSuspicious => Ru
        ? "Значение нестандартное. Если связь нестабильна, верните 1420."
        : "This is a custom value. If the connection is unstable, restore 1420.";
    public static string SetupWizardResetMtu => Ru ? "Сбросить только MTU" : "Reset MTU only";
    public static string SetupWizardRestore => Ru ? "Восстановить безопасные настройки" : "Restore safe settings";
    public static string SetupWizardRestoreHint => Ru
        ? "Установит MTU 1420 и выбранный выше режим. При активном VPN потребуется переподключение."
        : "Sets MTU to 1420 and applies the selected mode. An active VPN may need to reconnect.";
    public static string SetupWizardSafeModeDifference => Ru
        ? "Безопасный режим — временный аварийный запуск без пользовательских настроек. Этот мастер исправляет выбранные настройки постоянно и позволяет отменить изменение."
        : "Safe Mode is a temporary emergency start without user settings. This wizard repairs selected settings persistently and lets you undo the change.";

    public static string SetupWizardResultTitle => Ru ? "Результат" : "Result";
    public static string SetupWizardApplied => Ru
        ? "Настройки сохранены. Переподключите VPN, если он был запущен."
        : "Settings saved. Reconnect the VPN if it was running.";
    public static string SetupWizardMtuResetApplied => Ru
        ? "MTU сброшен до 1420. Режим маршрутизации не изменён."
        : "MTU reset to 1420. Routing mode was not changed.";
    public static string SetupWizardUndone => Ru ? "Исходные настройки восстановлены." : "Original settings restored.";
    public static string SetupWizardApplyFailed => Ru
        ? "Не удалось сохранить настройки. Исходные значения оставлены без изменений."
        : "Settings could not be saved. Original values were left unchanged.";
    public static string SetupWizardUndo => Ru ? "Отменить изменение" : "Undo change";
    public static string SetupWizardExport => Ru ? "Собрать диагностику" : "Export diagnostics";

    public static string SetupWizardBack => Ru ? "Назад" : "Back";
    public static string SetupWizardNext => Ru ? "Далее" : "Next";
    public static string SetupWizardClose => Ru ? "Закрыть" : "Close";
    public static string SetupWizardFinish => Ru ? "Готово" : "Done";
}
