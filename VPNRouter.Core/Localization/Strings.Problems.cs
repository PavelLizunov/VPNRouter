namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string ProblemsTitle(int count) => Ru
        ? (count == 1 ? "Найдена проблема" : $"Найдены проблемы: {count}")
        : (count == 1 ? "Problem found" : $"Problems found: {count}");

    public static string ProblemGameTrafficTitle => Ru
        ? "Игры и звонки идут через VPN"
        : "Games and calls go through the VPN";
    public static string ProblemGameTrafficBody => Ru
        ? "Включён режим «Весь трафик», поэтому игры идут через далёкий сервер и могут лагать. Исправление вернёт режим «Приложения»: через VPN пойдут только выбранные программы, игры — напрямую."
        : "\"All traffic\" is on, so games go through a distant server and may lag. The fix switches back to \"Apps\": only the selected apps use the VPN, games go direct.";

    public static string ProblemMtuTitle => Ru
        ? "Слишком большой размер пакета"
        : "Packet size is too large";
    public static string ProblemMtuBody(int mtu) => Ru
        ? $"Ваша сеть не пропускает такие большие пакеты, из-за этого игры и сайты могут обрываться. Исправление уменьшит размер до {mtu}."
        : $"Your network does not pass packets this large, so games and sites may drop. The fix lowers the size to {mtu}.";

    public static string ProblemTimeoutsTitle => Ru
        ? "Сервер VPN отвечает с перебоями"
        : "The VPN server responds intermittently";
    public static string ProblemTimeoutsBody => Ru
        ? "Подключения к серверу иногда не проходят. Исправление переподключит VPN."
        : "Some connections to the server fail. The fix reconnects the VPN.";

    public static string ProblemsFix => Ru ? "Исправить" : "Fix";
    public static string ProblemsHide => Ru ? "Скрыть" : "Hide";
    public static string ProblemsUndo => Ru ? "Отменить" : "Undo";
    public static string ProblemsFixing => Ru ? "Исправляю и переподключаю VPN…" : "Fixing and reconnecting the VPN…";
    public static string ProblemsFixed => Ru
        ? "Готово. Проверьте, всё ли работает. Если стало хуже — нажмите «Отменить»."
        : "Done. Check that everything works. If it got worse, press Undo.";
    public static string ProblemsFixedOffline => Ru
        ? "Готово. Изменения применятся при следующем подключении."
        : "Done. The changes apply on the next connect.";
    public static string ProblemsMtuReverted => Ru
        ? "Режим исправлен. С новым размером пакета VPN не поднялся, поэтому размер возвращён прежний."
        : "Routing fixed. The VPN did not come up with the new packet size, so the previous size is back.";
    public static string ProblemsUndone => Ru ? "Изменения отменены." : "Changes undone.";
    public static string ProblemsFixFailed => Ru
        ? "Не удалось исправить. Попробуйте ещё раз или соберите диагностику."
        : "Could not fix it. Try again or collect diagnostics.";

    public static string ProblemsHowTo => Ru ? "Видео: что нажать" : "Video: what to press";
    public static string HelpVideosTitle => Ru ? "Видео-подсказки" : "Video guides";
    public static string HelpVideosSubtitle => Ru
        ? "Что нажать, если игры лагают, VPN не работает или нужна старая версия"
        : "What to press when games lag, the VPN fails or you need an older version";
}
