using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>
/// W-4 (UI chip stub) — saved wgturn share URL with a human-readable
/// label. Persisted as part of <c>AppConfig.WgturnConfigs</c> so the
/// user can keep multiple operator profiles (e.g. <c>Operator-A</c>,
/// <c>Operator-B</c>, a personal fallback) and pick one per session
/// from the Emergency Channel card on the Tools tab.
///
/// <para>This is intentionally a thin DTO — the authoritative parser
/// for the <c>wgturn://</c> share URL lives in <see cref="EmergencyChannelConfig.TryParse"/>.
/// We don't decode the base64 payload here; the UI just stores what
/// the user pasted and replays it through <see cref="EmergencyChannelConfig"/>
/// at connect time.</para>
///
/// <para>NOTE: This stub mirrors the contract described in the W-1 chip
/// (see <c>plans/wgturn-on-demand-download.md</c> §12). If W-1 lands
/// first with its own definition, the merge resolution should keep
/// W-1's version and drop this file.</para>
/// </summary>
public class WgturnEntry
{
    /// <summary>
    /// Display name for the entry (e.g. <c>Operator-A</c>). Surface
    /// of the ComboBox label in the Tools tab. Free-form — user types
    /// it via <c>+ Add</c>.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The full <c>wgturn://...</c> share URL as pasted by the user.
    /// Passed verbatim to <see cref="EmergencyChannelConfig.TryParse"/>
    /// at connect time.
    /// </summary>
    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of when the user added this entry. Cosmetic only —
    /// future "sort by recent" or "stale entry pruning" can lean on
    /// this. Initialised to <see cref="DateTimeOffset.UtcNow"/> by the
    /// UI when the user clicks <c>+ Add</c>.
    /// </summary>
    [YamlMember(Alias = "added_at")]
    public DateTimeOffset AddedAt { get; set; }
}
