namespace VPNRouter.Core.Models;

/// <summary>
/// r9 Phase 2 — typed configuration for the wgturn-core emergency
/// fallback channel. Carries the share URL issued by the wgturn server
/// (<c>wgturn://...</c>) plus the runtime VK Calls invite link
/// (<c>https://vk.com/call/join/...</c>).
///
/// <para>Phase-2 scope: this is a transport object only — Engine /
/// Manager pass the URL through to <c>wgturn-cli.exe connect-url</c>
/// verbatim. We deliberately do NOT decode the base64 payload here;
/// that's wgturn-cli's responsibility (and re-implementing its codec
/// in C# would couple us to its on-the-wire schema). All we need on
/// the desktop side is "is this shape syntactically a wgturn URL"
/// for paste-time validation in the future Phase 3 UI.</para>
///
/// <para>VK link is intentionally NOT part of the share URL — it's a
/// runtime parameter (different room per call). The URL bundles every
/// key + endpoint needed to bring the tunnel up; the user pastes a
/// fresh VK invite per session.</para>
/// </summary>
public class EmergencyChannelConfig
{
    /// <summary>
    /// Share URL from the wgturn server. Format: <c>wgturn://&lt;base64-payload&gt;[#label]</c>.
    /// The base64 payload encodes server pubkey, client privkey, endpoint,
    /// allowed-IPs, address, optional DNS / MTU / keepalive. See
    /// <c>wgturn-core/pkg/wgshare/share.go</c> for the wire format.
    /// </summary>
    public string WgturnUrl { get; set; } = string.Empty;

    /// <summary>
    /// VK Calls invite link. Format: <c>https://vk.com/call/join/&lt;id&gt;</c>.
    /// Required at <c>StartAsync</c> time but may be empty in saved config
    /// (user pastes a fresh one per session).
    /// </summary>
    public string VkLink { get; set; } = string.Empty;

    /// <summary>
    /// Optional human label parsed from the URL fragment (after <c>#</c>).
    /// Cosmetic only — used for UI display in Phase 3.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Best-effort parse of a wgturn share URL plus optional VK link.
    /// Phase-2 scope: validates the surface structure only:
    /// <list type="bullet">
    /// <item>Scheme must be <c>wgturn://</c></item>
    /// <item>Payload (base64-url-encoded JSON) must be non-empty</item>
    /// <item>Optional <c>#label</c> fragment is captured into <see cref="Label"/></item>
    /// </list>
    /// We do NOT decode the base64 payload — wgturn-cli.exe owns the
    /// authoritative parser (<c>wgshare.Parse</c>). If the payload is
    /// corrupt, wgturn-cli will surface the error at connect time.
    ///
    /// <para>Returns <c>true</c> on success and populates <paramref name="config"/>;
    /// <c>false</c> on any structural failure (config is left unset).
    /// VK link is treated as optional in this method — Engine validates
    /// it at <c>StartAsync</c>.</para>
    /// </summary>
    public static bool TryParse(string serialized, string vkLink, out EmergencyChannelConfig config)
    {
        config = new EmergencyChannelConfig();

        if (string.IsNullOrWhiteSpace(serialized))
            return false;

        var trimmed = serialized.Trim();
        const string scheme = "wgturn://";
        if (!trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmed.Substring(scheme.Length);

        string? label = null;
        var hashIdx = rest.IndexOf('#');
        if (hashIdx >= 0)
        {
            label = hashIdx + 1 < rest.Length ? rest.Substring(hashIdx + 1) : null;
            rest = rest.Substring(0, hashIdx);
        }

        if (string.IsNullOrWhiteSpace(rest))
            return false;

        config.WgturnUrl = trimmed;
        config.VkLink = vkLink ?? string.Empty;
        config.Label = string.IsNullOrWhiteSpace(label) ? null : Uri.UnescapeDataString(label);
        return true;
    }

    /// <summary>
    /// Convenience overload: TryParse without an explicit VK link.
    /// Used by saved-config code paths where the VK link is stored
    /// separately and may be empty until the user pastes one.
    /// </summary>
    public static bool TryParse(string serialized, out EmergencyChannelConfig config)
        => TryParse(serialized, vkLink: string.Empty, out config);
}
