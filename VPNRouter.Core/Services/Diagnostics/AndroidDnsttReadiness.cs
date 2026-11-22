using System.Text.RegularExpressions;

namespace VPNRouter.Core.Services.Diagnostics;

internal static partial class AndroidDnsttReadiness
{
    internal static string? Validated(string? summary) =>
        summary is { Length: > 0 and <= 160 } && SummaryPattern().IsMatch(summary) ? summary : null;

    [GeneratedRegex(
        @"\ADNSTT readiness: command=(connecting|connected|requesting-test|test-requested) outbounds=(no-snapshot|proxy-available|proxy-pending|proxy-missing) positiveCallback=(true|false) elapsedMs=[0-9]{1,8}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();
}
