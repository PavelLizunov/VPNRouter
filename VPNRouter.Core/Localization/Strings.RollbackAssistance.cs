namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string RollbackAssistanceTitle => Ru
        ? "Возможно, предыдущая версия работала лучше"
        : "A previous version may work better";

    public static string RollbackAssistanceDiagnosticsTitle => Ru
        ? "Подключение не удалось три раза"
        : "Connection failed three times";

    public static string RollbackAssistanceBody(int failures, string currentVersion, string previousVersion) => Ru
        ? $"Эта конфигурация не подключилась {failures} раз подряд на v{currentVersion}. Для v{previousVersion} есть подтверждённая ранее рабочая сборка."
        : $"This configuration failed {failures} times in a row on v{currentVersion}. v{previousVersion} is the previously verified build.";

    public static string RollbackAssistanceAttestationTitle => Ru
        ? "На предыдущей версии подключение работало?"
        : "Did the previous version work?";

    public static string RollbackAssistanceAttestationBody(int failures, string currentVersion, string previousVersion) => Ru
        ? $"Эта конфигурация не подключилась {failures} раз подряд на v{currentVersion}. Перед обновлением была установлена v{previousVersion}. На v{previousVersion} с теми же настройками подключение работало?"
        : $"This configuration failed {failures} times in a row on v{currentVersion}. The previous installation was v{previousVersion}. On v{previousVersion}, with the same settings, did the connection work?";

    public static string RollbackAssistanceAttestationYes => Ru
        ? "Да, работало"
        : "Yes, it worked";

    public static string RollbackAssistanceAttestationNoOrUnsure => Ru
        ? "Нет или не уверен"
        : "No or not sure";

    public static string RollbackAssistanceBodyAttested(int failures, string currentVersion, string previousVersion) => Ru
        ? $"Вы подтвердили, что на v{previousVersion} с теми же настройками подключение работало. Текущая сборка v{currentVersion} не подключилась {failures} раз подряд. Уверенность средняя: это ваш отзыв, а не локальная запись об успехе."
        : $"You confirmed that v{previousVersion} worked with the same settings. The current build v{currentVersion} failed {failures} times in a row. Confidence is medium because this is your attestation, not a local success record.";

    public static string RollbackAssistanceBodyKnownRegression(int failures, string currentVersion, string previousVersion) => Ru
        ? $"Эта конфигурация не подключилась {failures} раз подряд на v{currentVersion}. Регрессия этой сборки уже известна; предыдущая версия — v{previousVersion}."
        : $"This configuration failed {failures} times in a row on v{currentVersion}. This build has a documented regression; the previous version is v{previousVersion}.";

    public static string RollbackAssistanceIncompatibleBody(string detailCode)
    {
        var detail = Ru
            ? (detailCode switch
            {
                "legacy-telegram-autostart" => "обновите Telegram-прокси или отключите его автозапуск перед откатом.",
                "active-dnstt" => "активный сервер или цепочка использует DNSTT.",
                "active-turnrelay" => "активная цепочка использует TurnRelay.",
                "active-native-transport" => "активный профиль использует TrustTunnel или Mieru.",
                "active-vmess" => "активный сервер использует VMess или новые поля VMess.",
                "active-single-outbound" => "активный сервер закреплён за одним outbound.",
                "active-row-ambiguous" => "в активной подписке несколько серверов с одним именем, и старая сборка выберет другой.",
                "active-row-missing" => "активная строка подписки не найдена.",
                "active-subscription-ambiguous" => "активную подписку нельзя однозначно определить.",
                "active-subscription-disabled" => "активная подписка отключена.",
                "active-subscription-missing" => "активная подписка не найдена.",
                "active-chain-ambiguous" => "цепочка активного сервера неоднозначна.",
                "active-custom-missing" => "файл активного пользовательского конфига не найден.",
                "active-custom-unreadable" => "файл активного пользовательского конфига не читается.",
                "active-custom-unverified" => "активный пользовательский конфиг не удалось проверить.",
                _ => "активная конфигурация несовместима со старой сборкой.",
            })
            : (detailCode switch
            {
                "legacy-telegram-autostart" => "update the Telegram proxy or disable its autostart before rollback.",
                "active-dnstt" => "the active server or chain uses DNSTT.",
                "active-turnrelay" => "the active chain uses TurnRelay.",
                "active-native-transport" => "the active profile uses TrustTunnel or Mieru.",
                "active-vmess" => "the active server uses VMess or the new VMess fields.",
                "active-single-outbound" => "the active server is pinned to a single outbound.",
                "active-row-ambiguous" => "the active subscription has duplicate server names and the old build would choose another row.",
                "active-row-missing" => "the active subscription row was not found.",
                "active-subscription-ambiguous" => "the active subscription cannot be determined unambiguously.",
                "active-subscription-disabled" => "the active subscription is disabled.",
                "active-subscription-missing" => "the active subscription was not found.",
                "active-chain-ambiguous" => "the active server chain is ambiguous.",
                "active-custom-missing" => "the active custom-config file is missing.",
                "active-custom-unreadable" => "the active custom-config file cannot be read.",
                "active-custom-unverified" => "the active custom config could not be verified.",
                _ => "the active configuration is not compatible with the older build.",
            });
        return Ru
            ? $"Откат не предложен: {detail} Соберите локальную диагностику."
            : $"Rollback was not offered: {detail} Run local diagnostics.";
    }

    public static string RollbackAssistanceDiagnosticsBody(int failures, string currentVersion) => Ru
        ? $"Эта конфигурация не подключилась {failures} раз подряд на v{currentVersion}. Проверенной предыдущей сборки для неё нет. Соберите локальную диагностику вместо догадок."
        : $"This configuration failed {failures} times in a row on v{currentVersion}. There is no verified earlier build for it. Run local diagnostics instead of guessing.";

    public static string RollbackAssistanceConfidenceHigh => Ru
        ? "Уверенность: высокая"
        : "Confidence: high";

    public static string RollbackAssistanceConfidenceMedium => Ru
        ? "Уверенность: средняя"
        : "Confidence: medium";

    public static string RollbackAssistanceViewPrevious => Ru
        ? "Посмотреть предыдущую версию"
        : "View previous version";

    public static string RollbackAssistanceRunDiagnostics => Ru
        ? "Запустить локальную диагностику"
        : "Run local diagnostics";

    public static string RollbackAssistanceNotNow => Ru
        ? "Не сейчас"
        : "Not now";

    public static string RollbackAssistanceBusy => Ru
        ? "Проверяю официальную историю выпусков..."
        : "Checking the official release history...";

    public static string RollbackAssistanceNoCompatible => Ru
        ? "Подходящей предыдущей сборки с проверенным происхождением нет. Можно собрать локальную диагностику."
        : "No compatible previous build with verified provenance is available. You can run local diagnostics.";

    public static string RollbackAssistanceExpired => Ru
        ? "Настройки подключения или его состояние изменились. Проверьте подключение ещё раз."
        : "The connection settings or state changed. Check the connection again.";

    public static string RollbackAssistanceUnavailable => Ru
        ? "Автоматический откат доступен только в Windows Standard x64."
        : "Automatic rollback is available only on Windows Standard x64.";

    public static string RollbackAssistanceManifestUnavailable => Ru
        ? "Встроенный манифест восстановления не прошёл проверку подписи. Автоматический откат отключён."
        : "The embedded recovery manifest did not verify. Automatic rollback stays disabled.";

    public static string RollbackAssistanceConfirmation(string currentVersion, string targetVersion)
    {
        var confirmation = Ru
            ? $"Установить v{targetVersion} вместо v{currentVersion}? Приложение сохранит копию настроек и перезапустится."
            : $"Install v{targetVersion} in place of v{currentVersion}? The app will back up your settings and restart.";
        if (targetVersion != "2.50.5") return confirmation;
        return confirmation + (Ru
            ? " При новой установке Telegram-прокси эта версия загружает устаревший набор сертификатов."
            : " A fresh installation of the Telegram proxy on this version downloads an outdated CA bundle.");
    }

    public static string RollbackAssistanceInstall => Ru
        ? "Установить предыдущую версию"
        : "Install previous version";

    public static string RollbackAssistanceFailed(string reason) => Ru
        ? $"Откат не начался: {reason}"
        : $"Rollback did not start: {reason}";

    public static string RollbackAssistanceCompleted(string targetVersion) => Ru
        ? $"Откат на v{targetVersion} подтверждён после перезапуска."
        : $"Rollback to v{targetVersion} was confirmed after restart.";

    public static string RollbackAssistanceNotConfirmed(string targetVersion, string observedVersion) => Ru
        ? $"Вы запросили установку v{targetVersion}. Сейчас запущена v{observedVersion}. Подтверждение запуска v{targetVersion} отсутствует."
        : $"You requested v{targetVersion}. This session is running v{observedVersion}. Startup of v{targetVersion} is unconfirmed.";

    public static string RollbackAssistanceStatusDismiss => Ru
        ? "Скрыть"
        : "Dismiss";

    public static string RollbackAssistanceProvenance => Ru
        ? "Встроенный манифест издателя, точный размер и SHA-256 проверяются клиентом."
        : "The embedded publisher-signed recovery manifest, exact size and SHA-256 are checked by the client.";

    public static string RollbackAssistanceRevoked => Ru
        ? "Целевая версия отозвана или ниже минимума безопасности."
        : "The target version is revoked or below the security floor.";

}
