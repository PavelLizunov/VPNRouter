namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string NinaZapretTitle => "NINA (zapret2)";
    public static string NinaZapretRepair => Ru ? "Исправить Discord с ИИ" : "Repair Discord with AI";
    public static string NinaZapretHint => Ru ? "Проверка подключения к Discord Gateway." : "Checks the connection to Discord Gateway.";
    public static string NinaZapretStop => Ru ? "Остановить NINA" : "Stop NINA";
    public static string NinaZapretChecking => Ru ? "Проверяю подключение…" : "Checking the connection…";
    public static string NinaZapretCancelling => Ru ? "Восстанавливаю прежние настройки…" : "Restoring previous settings…";
    public static string NinaZapretTrying(string profile) => Ru ? $"Проверяю профиль {profile}…" : $"Testing profile {profile}…";
    public static string NinaZapretRepaired(string profile) => Ru ? $"Discord Gateway доступен. Сохранён профиль {profile}." : $"Discord Gateway is reachable. Saved profile {profile}.";
    public static string NinaZapretAlreadyWorking => Ru ? "Discord Gateway уже доступен." : "Discord Gateway is already reachable.";
    public static string NinaZapretNoImprovement => Ru ? "Прежние настройки восстановлены. Доступ к Discord Gateway не улучшился." : "Previous settings restored. Discord Gateway access did not improve.";
    public static string NinaZapretCancelled => Ru ? "Ремонт прерван. Прежние настройки восстановлены." : "Repair cancelled. Previous settings restored.";
    public static string NinaZapretStopped => Ru ? "NINA остановлена." : "NINA stopped.";
    public static string NinaZapretBusy => Ru ? "Дождитесь завершения текущей операции." : "Wait for the current operation to finish.";
    public static string NinaZapretEngineConflict => Ru ? "Остановите запущенный «Запрет», чтобы проверить профили NINA." : "Stop the active Zapret process before testing NINA profiles.";
    public static string NinaZapretVpnActive => Ru ? "Отключите VPN, чтобы проверить прямое подключение." : "Disconnect the VPN to test the direct connection.";
    public static string NinaZapretServiceDisabled => Ru ? "Служба NINA отключена в Windows." : "The NINA service is disabled in Windows.";
    public static string NinaZapretServiceUnavailable => Ru ? "Служба NINA недоступна." : "The NINA service is unavailable.";
    public static string NinaZapretPermissionDenied => Ru ? "Для управления службой NINA нужны права администратора." : "Administrator access is required to manage the NINA service.";
    public static string NinaZapretRuntimeUnsupported => Ru ? "Установите более новую версию службы NINA." : "Update the NINA service to a newer version.";
    public static string NinaZapretAssistantNotDeployed => Ru ? "Сервис подбора профилей ещё не подключён." : "The profile picker service is not yet deployed.";
    public static string NinaZapretAssistantRequestRejected => Ru ? "Сервис не принял запрос на подбор профиля." : "The profile picker rejected the request.";
    public static string NinaZapretRollbackFailed => Ru ? "NINA не смогла восстановить прежние настройки." : "NINA could not restore the previous settings.";
    public static string NinaZapretNetworkChanged => Ru ? "Подключение сменилось. Запустите проверку ещё раз." : "The network changed. Run the check again.";
    public static string NinaZapretNetworkUnknown => Ru ? "NINA не смогла определить прямое подключение." : "NINA could not identify the direct network.";
    public static string NinaZapretControlsFailed => Ru ? "Контрольные сайты недоступны. Проверьте интернет-подключение." : "The control sites are unreachable. Check your internet connection.";
    public static string NinaZapretFailed => Ru ? "Не удалось подтвердить результат NINA. Проверьте состояние службы." : "Could not confirm the NINA result. Check the service status.";
}
