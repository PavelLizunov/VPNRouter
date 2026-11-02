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
}
