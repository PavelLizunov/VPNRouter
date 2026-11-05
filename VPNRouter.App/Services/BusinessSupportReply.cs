using System.Globalization;
using System.Text.Json;
using VPNRouter.Core.Localization;

namespace VPNRouter.App.Services;

internal sealed record BusinessSupportReply(string State, string? MessageRu = null, string? MessageEn = null, string? ReportId = null)
{
    internal static readonly BusinessSupportReply Legacy = new("legacy");
    internal static readonly BusinessSupportReply Pending = new("pending");
    internal static readonly BusinessSupportReply Unavailable = new("unavailable");
    internal string Message => State switch
    {
        "legacy" => Strings.BusinessDiagnosticsDelivered,
        "pending" => Strings.BusinessDiagnosticsReplyPending,
        "ready" when (Strings.Lang.Equals("ru", StringComparison.OrdinalIgnoreCase) ? MessageRu : MessageEn) is { } text
            => Strings.BusinessDiagnosticsReceived + " " + text,
        _ => Strings.BusinessDiagnosticsReplyUnavailable
    };

    internal static BusinessSupportReply Parse(JsonElement root)
    {
        if (!UniqueObject(root) || !root.TryGetProperty("analysis", out var analysis) || !UniqueObject(analysis) ||
            !analysis.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
            !analysis.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            return Unavailable;
        var kind = state.GetString();
        var ru = Text(analysis, "message_ru");
        var en = Text(analysis, "message_en");
        if (kind is not ("ready" or "pending" or "unavailable") || (kind == "ready" && ru == null && en == null))
            return Unavailable;
        var id = root.TryGetProperty("report_id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (id is not { Length: 64 } || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) id = null;
        return new(kind, ru, en, id);
    }

    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();

    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) || text.Length > 300 || text.Any(c => c is '<' or '>' ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            ? null : text.Trim();
    }
}
