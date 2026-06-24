using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>
/// r9 Phase 2 — persisted state for the wgturn-core emergency channel.
/// Stored in <c>config.yaml</c> alongside the rest of AppSettings so
/// the user doesn't have to re-paste the share URL every launch. The
/// VK link is also persisted but typically gets re-pasted per session
/// since each VK call uses a fresh invite.
/// </summary>
public class EmergencyChannelSettings
{
    /// <summary>True ⇒ user has opted into the emergency channel
    /// feature. Default false — Phase 3 UI flips this when the user
    /// connects for the first time.</summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Share URL from the wgturn server (<c>wgturn://...</c>).
    /// Nullable so an empty config doesn't serialise a placeholder.</summary>
    [YamlMember(Alias = "wgturn_url")]
    public string? WgturnUrl { get; set; }

    /// <summary>VK Calls invite (<c>https://vk.com/call/join/...</c>).
    /// Optional — typically supplied at runtime per session.</summary>
    [YamlMember(Alias = "vk_link")]
    public string? VkLink { get; set; }

    /// <summary>
    /// W-4 — list of named wgturn share URLs the user has saved (e.g.
    /// <c>Operator-A</c>, <c>Operator-B</c>, <c>Personal</c>). Surfaced
    /// in the Tools tab Emergency Channel card as a ComboBox so the
    /// user can pick one per session without re-pasting the
    /// <c>wgturn://</c> URL each time. Empty until the user adds their
    /// first entry via <c>+ Add</c>.
    /// </summary>
    [YamlMember(Alias = "configs")]
    public List<WgturnEntry> Configs { get; set; } = new();

    /// <summary>
    /// W-4 — name of the entry from <see cref="Configs"/> that should
    /// be pre-selected when the user opens the Tools tab. Empty when
    /// no entry is selected (e.g. first-run, after deleting the active
    /// one).
    /// </summary>
    [YamlMember(Alias = "active_config")]
    public string ActiveConfig { get; set; } = string.Empty;

    /// <summary>
    /// W-4 — last VK Calls invite link the user pasted into the
    /// Emergency Channel card. Persisted so reopening the app
    /// pre-fills the input. Each call typically needs a fresh link, but
    /// keeping the last one saves a paste during quick reconnect.
    /// </summary>
    [YamlMember(Alias = "last_vk_link")]
    public string LastVkLink { get; set; } = string.Empty;
}
