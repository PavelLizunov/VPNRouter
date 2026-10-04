namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    private static bool IsRu(string lang) => lang.Equals("ru", StringComparison.OrdinalIgnoreCase);

    public static string FailoverSwitching(string server) => FailoverSwitching(server, Lang);
    internal static string FailoverSwitching(string server, string lang) => IsRu(lang)
        ? $"Переключение на сервер: {server}"
        : $"Switching to server: {server}";

    public static string FailoverCustomConfigUnusable => FailoverCustomConfigUnusableFor(Lang);
    internal static string FailoverCustomConfigUnusableFor(string lang) => IsRu(lang)
        ? "Кастомный конфиг недоступен. Проверьте JSON в Серверы → Custom — " +
          "поле server, server_port, uuid или Reality public_key выглядят неверно."
        : "The custom config is unusable. Check the JSON in Servers → Custom: " +
          "server, server_port, uuid or the Reality public_key look wrong.";

    public static string FailoverManualServerSilent(string server, string reason) =>
        FailoverManualServerSilent(server, reason, Lang);
    internal static string FailoverManualServerSilent(string server, string reason, string lang) => IsRu(lang)
        ? $"Сервер '{server}' не отвечает на probe ({reason}). VPN запущен, но прямая проверка через сервер не " +
          "проходит — возможно ложное срабатывание (Reality маскируется) или сервер действительно недоступен. " +
          "Выберите другой сервер из списка вручную или переключитесь на подписку."
        : $"Server '{server}' does not answer the probe ({reason}). The VPN is running, but a direct check " +
          "through the server fails - this may be a false alarm (Reality masks itself) or the server is really " +
          "down. Pick another server from the list yourself or switch to a subscription.";

    public static string FailoverAllServersDown(int attempts) => FailoverAllServersDown(attempts, Lang);
    internal static string FailoverAllServersDown(int attempts, string lang) => IsRu(lang)
        ? $"Все серверы недоступны ({attempts} попыток). Проверьте подписку (Обновить) или сетевое подключение."
        : $"All servers are unreachable ({attempts} attempts). Check the subscription (Refresh) or the network connection.";

    public static string FailoverNoOtherInSubscription => FailoverNoOtherInSubscriptionFor(Lang);
    internal static string FailoverNoOtherInSubscriptionFor(string lang) => IsRu(lang)
        ? "Сервер не отвечает, а других в подписке нет — возможно, провайдер блокирует его IP или сервер " +
          "недоступен. Смените сервер или попросите обновить подписку."
        : "The server does not respond and there are no others in the subscription - the provider may be " +
          "blocking its IP, or it is down. Change the server or ask for a subscription update.";

    public static string FailoverNoOtherInList => FailoverNoOtherInListFor(Lang);
    internal static string FailoverNoOtherInListFor(string lang) => IsRu(lang)
        ? "Сервер не отвечает, а других в списке VLESS нет — возможно, он заблокирован или недоступен. " +
          "Добавьте другой сервер."
        : "The server does not respond and there are no others in the VLESS list - it may be blocked or down. " +
          "Add another server.";

    public static string StartupCheckingUdpServers => Ru
        ? "Проверяем UDP-серверы для игр…"
        : "Checking UDP servers for games…";
}
