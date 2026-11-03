namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    public static string BusinessSplit => Ru ? "Выбранные приложения и сайты" : "Selected apps and sites";
    public static string BusinessFull => Ru ? "Всё, кроме российских ресурсов и UIS" : "Everything except Russian resources and UIS";
    public static string BusinessHint => Ru
        ? "Российские ресурсы и телефония UIS направляются напрямую."
        : "Russian resources and UIS telephony are routed directly.";
    public static string BusinessConfigMissing => Ru
        ? "Не удалось подготовить корпоративную конфигурацию. Обратитесь к администратору."
        : "The Business configuration could not be prepared. Contact your administrator.";
    public static string BusinessGeoMissing => Ru
        ? "Нет данных для обхода российских ресурсов. Переустановите корпоративную сборку."
        : "Russian bypass data is missing. Reinstall the Business build.";
    public static string BusinessDiagnostics => Ru ? "Сохранить диагностику" : "Save diagnostics";
    public static string BusinessOpen => Ru ? "Открыть VPNRouter Business" : "Open VPNRouter Business";
    public static string BusinessServer => Ru ? "Сервер VPN" : "VPN server";
    public static string BusinessServerHint => Ru
        ? "Если VPN недоступен: отключитесь, выберите другой сервер и подключитесь снова. Выбор сохраняется."
        : "If VPN is unavailable, disconnect, choose another server and reconnect. Your choice is saved.";
    public static string BusinessServerSaveFailed => Ru
        ? "Не удалось сохранить выбранный сервер. Проверьте права доступа к настройкам."
        : "The selected server could not be saved. Check access to the settings folder.";
}
