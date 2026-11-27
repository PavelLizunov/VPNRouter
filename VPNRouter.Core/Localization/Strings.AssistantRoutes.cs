namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    internal static string AssistantGamesPreview(IReadOnlyList<string> processes, IReadOnlyList<string> previousExceptions,
        bool wasFull, bool wasInclude, bool connected)
    {
        string mode = wasFull
            ? (Ru ? "«Весь трафик» → «Все, кроме выбранных»" : "“Full tunnel” → “All except selected”")
            : wasInclude
                ? (Ru ? "«Только выбранные» → «Все, кроме выбранных»" : "“Only selected” → “All except selected”")
                : (Ru ? "Режим «Все, кроме выбранных»" : "“All except selected” mode");
        var lines = new List<string>
        {
            mode,
            Ru ? "Вне VPN пойдут найденные в Steam процессы игр" : "These installed Steam processes will bypass the VPN",
            string.Join(", ", processes),
        };
        if (previousExceptions.Count > 0 && (wasFull || wasInclude))
            lines.Add((Ru ? "Также включатся сохранённые исключения — " : "Saved exclusions will also take effect — ")
                + string.Join(", ", previousExceptions));
        if (wasInclude)
            lines.Add(Ru ? "Для остальных приложений маршрутом по умолчанию станет VPN."
                : "The VPN will become the default route for other apps.");
        if (wasFull) lines.Add(AssistantSplitProfileChange.TrimStart());
        if (connected) lines.Add(AssistantRecipeRestartNotice);
        return string.Join("\n", lines);
    }

    internal static string AssistantRuPreview(bool connected, bool listsReady)
    {
        string text = Ru
            ? "Напрямую пойдут домены из списка российских зон (.ru, .su, .рф) и адреса из списка российских IP. Остальной трафик сохранит текущие маршруты."
            : "Domains in the Russian zone list (.ru, .su, .рф) and addresses in the Russian IP list will bypass the VPN. Other traffic keeps its current routes.";
        if (!listsReady) text += "\n" + (connected
            ? (Ru ? "При применении VPNRouter сначала скачает списки." : "VPNRouter will download the lists when you apply.")
            : (Ru ? "VPNRouter попробует загрузить списки при следующем подключении." : "VPNRouter will try to download the lists at the next connection."));
        if (connected) text += "\n" + AssistantRecipeRestartNotice;
        return text;
    }

    internal static string AssistantLocalNotice => Ru ? "Запрос обрабатывается на этом устройстве." : "This request is handled on this device.";
    internal static string AssistantGamesSaved => Ru
        ? "Сохранён режим «Все, кроме выбранных». Игры из списка пойдут вне VPN при следующем подключении."
        : "“All except selected” saved. The listed games will bypass the VPN at the next connection.";
    internal static string AssistantGamesApplied => Ru
        ? "Готово. В активных правилах найденные игры направлены вне VPN."
        : "Done. The active rules send the detected games outside the VPN.";
    internal static string AssistantRuSaved => Ru
        ? "Обход включён в настройках. При следующем подключении VPNRouter применит доступные списки доменов и IP."
        : "Bypass saved. VPNRouter will apply the available domain and IP lists at the next connection.";
    internal static string AssistantRuApplied => Ru
        ? "Готово. В активных правилах российские домены и IP из списков направлены вне VPN."
        : "Done. The active rules send listed Russian domains and IPs outside the VPN.";
    internal static string AssistantRecipeAppliedKeptNewer => Ru
        ? "Изменение применено. Последующие правки настроек сохранены."
        : "The change was applied. Your later edits were kept.";
    internal static string AssistantRecipeSavedKeptNewer => Ru
        ? "Настройки сохранены с учётом последующих правок."
        : "Settings saved with your later edits.";
    internal static string AssistantGamesAlreadySet => Ru
        ? "Найденные процессы игр Steam уже есть в сохранённых исключениях VPN."
        : "The detected Steam processes are already in your saved VPN exclusions.";
    internal static string AssistantGamesNotFound => Ru
        ? "В библиотеках Steam не удалось найти процессы игр. Другие игры можно добавить в разделе «Приложения»."
        : "No game processes were found in your Steam libraries. You can add other games under Applications.";
    internal static string AssistantGamesScanFailed => Ru
        ? "Не удалось прочитать библиотеку Steam. Попробуйте ещё раз или добавьте игру в разделе «Приложения»."
        : "Could not read the Steam library. Try again or add the game under Applications.";
    internal static string AssistantGamesPlatformUnsupported => Ru
        ? "Автопоиск игр Steam доступен в Windows. На этом устройстве выберите процессы в разделе «Приложения»."
        : "Automatic Steam game detection is available on Windows. Select processes under Applications on this device.";
    internal static string AssistantRuAlreadyOn => Ru ? "Обход российского трафика уже включён в настройках." : "Russian traffic bypass is already enabled in settings.";
    internal static string AssistantRuRuleSetsPending => Ru
        ? "Обход уже включён в настройках. При следующем подключении VPNRouter попробует загрузить списки."
        : "Bypass is already enabled in settings. VPNRouter will try to download the lists at the next connection.";
    internal static string AssistantRuRuleSetsMissing => Ru
        ? "Не удалось загрузить списки российских доменов и IP. Проверьте соединение и повторите запрос."
        : "Could not download the Russian domain and IP lists. Check your connection and try again.";
    internal static string AssistantRecipeConfigMode => Ru
        ? "Маршрут задан пользовательским конфигом. Чтобы менять правила здесь, выберите сервер или подписку."
        : "Routing is controlled by a custom configuration. Select a server or subscription to edit routing here.";
    internal static string AssistantRecipeRestartNotice => Ru
        ? "VPN переподключится при применении." : "Applying will reconnect the VPN.";
}
