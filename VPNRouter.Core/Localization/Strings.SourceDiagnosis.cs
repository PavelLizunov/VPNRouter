using VPNRouter.Core.Services;

namespace VPNRouter.Core.Localization;

public static partial class Strings
{
    internal static string SourceDiagnosisPreview => Ru ? "Отчёт о сбое" : "Crash report";
    internal static string SourceConnectionPreview => Ru ? "Последнее подключение" : "Last connection";
    internal static string SourceConnectionExpired => Ru ? "Состояние подключения изменилось. Выберите актуальный сбой." : "The connection state changed. Select the current failure.";
    internal static string SourceConnectionConsent(SourceConnectionFailure failure, DateTime utc)
    {
        var what = failure switch
        {
            SourceConnectionFailure.StartupTimeout => Ru ? "истекло время запуска VPN" : "VPN startup timed out",
            SourceConnectionFailure.ReadinessTimeout => Ru ? "VPN запустился, но подключение не подтвердилось вовремя" : "the VPN started but did not become ready in time",
            SourceConnectionFailure.UnexpectedStop => Ru ? "ядро остановилось во время подключения" : "the engine stopped while connecting",
            SourceConnectionFailure.TunBusy => Ru ? "адаптер уже занят" : "the tunnel adapter was already in use",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var time = utc.ToLocalTime().ToString("g", Ru
            ? System.Globalization.CultureInfo.GetCultureInfo("ru-RU") : System.Globalization.CultureInfo.CurrentCulture);
        return Ru ? $"{time} — {what}. Передать этот признак и сведения о сборке?"
            : $"{time} — {what}. Send this indicator and the build details?";
    }
    internal static string SourceDiagnosisNoReport => Ru ? "Нет доступного отчёта о последнем сбое." : "A readable crash report is unavailable.";
    internal static string SourceDiagnosisInsufficient => Ru
        ? "В отчёте не удалось определить ни тип ошибки, ни место сбоя, ни состояние подключения."
        : "The report contains no recognized error, failing component or connection signal.";
    internal static string SourceDiagnosisDoNotSend => Ru ? "Не отправлять" : "Keep on device";
    internal static string SourceDiagnosisConsent(AssistantCrashReport report)
    {
        var time = report.WrittenUtc.ToLocalTime().ToString("g", Ru
            ? System.Globalization.CultureInfo.GetCultureInfo("ru-RU") : System.Globalization.CultureInfo.CurrentCulture);
        var kind = report.Evidence.Fatal ? (Ru ? "аварийное завершение" : "fatal error") : (Ru ? "обработанный сбой" : "nonfatal error");
        return Ru ? $"Отчёт от {time} ({kind}). Отправить сведения о сборке и признаки этого сбоя для разбора?"
            : $"Report from {time} ({kind}). Send this build and the crash indicators for source analysis?";
    }

    internal static string SourceDiagnosisMismatch => Ru
        ? "Не удалось связать последний отчёт с установленной сборкой. Можно воспользоваться обычным разбором сбоя."
        : "The last report could not be matched to this installation. You can still use Analyze crash.";
    internal static string SourceDiagnosisNoMatch => Ru
        ? "Сборка найдена, но доступных фрагментов кода недостаточно для объяснения этого сбоя."
        : "This build is registered, but the available source does not explain the reported crash.";
    internal static string SourceDiagnosisUnknown => Ru
        ? "В каталоге пока нет исходников этой сборки. Доступен обычный разбор сбоя."
        : "Source for this build is unavailable in the catalog. You can still use Analyze crash.";
    internal static string SourceDiagnosisWorking => Ru ? "Сопоставляю сбой с исходниками сборки…" : "Matching the crash to this build’s source…";

    internal static string SourceDiagnosisText(AssistantSourceDiagnosis report)
    {
        var confidence = report.Confidence switch
        {
            "low" => Ru ? "низкая" : "low",
            "medium" => Ru ? "средняя" : "medium",
            "high" => Ru ? "высокая" : "high",
            _ => throw new ArgumentOutOfRangeException(nameof(report)),
        };
        var check = report.Check switch
        {
            "view_connection_status" => Ru ? "Посмотрите состояние подключения в главном окне." : "View the connection status in the main window.",
            "review_settings" => Ru ? "Сверьте текущие настройки подключения." : "Review the current connection settings.",
            "view_last_crash" => Ru ? "Сверьте вывод с последним локальным отчётом о сбое." : "Compare this explanation with your last local crash report.",
            _ => throw new ArgumentOutOfRangeException(nameof(report)),
        };
        var sources = string.Join("\n", report.Citations.Select(c => $"{c.Path} · L{c.First}–{c.Last}"));
        return Ru
            ? $"Возможная причина: {report.Hypothesis}\nОснование: {report.Basis}\nУверенность: {confidence}. {check}\n\nИсходники {report.Citations[0].Commit}\n{sources}"
            : $"Possible cause: {report.Hypothesis}\nEvidence: {report.Basis}\nConfidence: {confidence}. {check}\n\nSource {report.Citations[0].Commit}\n{sources}";
    }
}
