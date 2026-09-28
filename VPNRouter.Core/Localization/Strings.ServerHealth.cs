using VPNRouter.Core.Services;

namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string HealthVerdictLabel(ServerHealthVerdict verdict) => verdict switch
    {
        ServerHealthVerdict.Healthy => Ru
            ? "Работает через VPN"
            : "Works via VPN",
        ServerHealthVerdict.HostUnreachable => Ru
            ? "Хост недоступен"
            : "Host unreachable",
        ServerHealthVerdict.TcpOpenProtocolUntested => Ru
            ? "TCP открыт, VPN-протокол не проверен"
            : "TCP open, VPN protocol untested",
        ServerHealthVerdict.ProtocolHandshakeBlockedLikely => Ru
            ? "Хост доступен, но VPN-протокол не прошёл проверку"
            : "Host reachable, but the VPN protocol failed verification",
        ServerHealthVerdict.ProxyStartedButHttpFailed => Ru
            ? "Прокси стартовал, но HTTP через VPN не прошёл"
            : "Proxy started, but HTTP via VPN failed",
        ServerHealthVerdict.OnlyControlWorks => Ru
            ? "Туннель работает, обход блокировки не подтверждён"
            : "Tunnel up, censorship bypass unproven",
        ServerHealthVerdict.UdpOrAppProfileFailed => Ru
            ? "Веб через VPN работает, UDP/приложения — нет"
            : "Web via VPN works, UDP/apps fail",
        _ => Ru
            ? "Не проверен"
            : "Not verified",
    };

    public static string HealthAsnHighRisk => Ru
        ? "ASN под риском блокировки"
        : "ASN at high block risk";

    public static string HealthRuBlockWarning => Ru
        ? "Сервер доступен по сети, но VPN-протокол не проходит.\n"
          + "В России такое бывает при блокировке протокола, IP или подсети хостера через DPI/ТСПУ.\n"
          + "Ping/SSH в этом случае могут работать, но VLESS/Reality/AWG/HY2 — нет.\n"
          + "Попробуйте другой хостинг/ASN, страну или транспорт: XHTTP/gRPC/Naive/HY2/AWG 2.0."
        : "The server is reachable on the network, but the VPN protocol does not get through.\n"
          + "In Russia this happens when the protocol, IP, or the hoster's subnet is blocked via DPI/TSPU.\n"
          + "Ping/SSH may still work while VLESS/Reality/AWG/HY2 do not.\n"
          + "Try another hosting/ASN, country, or transport: XHTTP/gRPC/Naive/HY2/AWG 2.0.";

    public static string HealthCanaryFailedWarning => Ru
        ? "VPN подключился, но проверка заблокированного сервиса не прошла.\n"
          + "Обычный интернет через VPN работает, но этот сервер/транспорт может не обходить блокировку в вашей сети.\n"
          + "Попробуйте другой сервер, ASN/хостинг или транспорт."
        : "The VPN connected, but the blocked-service check did not pass.\n"
          + "Regular internet works through the VPN, but this server/transport may not bypass the block on your network.\n"
          + "Try another server, ASN/hosting, or transport.";

    public static string HealthYoutubeCanaryCaveat => Ru
        ? "YouTube — полезная проверка, но не абсолютная. В России его доступность может "
          + "отличаться по провайдерам, регионам, приложениям и временным правилам блокировки."
        : "YouTube is a useful check, but not an absolute one. In Russia its availability can "
          + "vary by ISP, region, app, and temporary blocking rules.";

    public static string HealthCanaryPartialNote => Ru
        ? "Часть заблокированных сервисов открывается через VPN, часть — нет. Обход подтверждён частично."
        : "Some blocked services open via the VPN, others do not. Bypass is only partially proven.";

    public static string HealthProviderHighRisk => Ru
        ? "Подсеть хостера под риском блокировки: несколько серверов этой подсети не проходят "
          + "проверку VPN-протокола, при этом другой хостинг работает. Авто-выбор исключает её."
        : "Hoster subnet at block risk: several servers on this subnet fail VPN-protocol "
          + "verification while another hosting works. Auto-select excludes it.";

    public static string HealthCheckedAgo(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1)
            return Ru ? "Проверено только что" : "Checked just now";
        if (age.TotalHours < 1)
        {
            var m = (int)age.TotalMinutes;
            return Ru ? $"Проверено {m} мин назад" : $"Checked {m} min ago";
        }
        var h = (int)age.TotalHours;
        return Ru ? $"Проверено {h} ч назад" : $"Checked {h} h ago";
    }
}
