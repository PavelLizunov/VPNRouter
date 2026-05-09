using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public static class LeakProtection
{
    /// <summary>
    /// Pre-generation invariant check on the AppSettings model. Catches
    /// inconsistent <c>ConfigMode</c> + <c>Subscriptions</c> + <c>Vless.Servers</c>
    /// states that would otherwise produce a silent leak (sing-box config
    /// generated with no proxy outbound usable, traffic falling through to
    /// direct).
    ///
    /// <para><strong>F-12 (parity audit P0, 2026-05-09)</strong> backstop:
    /// this is a defense-in-depth net for any future silent <c>ConfigMode</c>
    /// flip we miss in the UI layer. Same failure class as v2.28.2 silent
    /// leak — there the invariant violation lived inside <c>VpnEngine.Apply</c>;
    /// here we pin it at the model level so any caller (CLI, Service, future
    /// admin overlay) gets the same protection without needing to remember.</para>
    ///
    /// <para>Errors raised:</para>
    /// <list type="bullet">
    /// <item><c>ConfigMode == "subscribe"</c> AND no enabled subscription has
    ///   any <c>VlessServerEntry</c> AND <c>Vless.Servers</c> is empty →
    ///   the engine would generate a config with empty proxy outbounds and
    ///   traffic would fall through to direct.</item>
    /// <item><c>ConfigMode == "generated"</c> AND <c>Vless.Servers</c> is empty
    ///   AND no enabled subscription has servers — engine has nothing to
    ///   route through.</item>
    /// </list>
    ///
    /// Callers should run this BEFORE generating the sing-box config and
    /// abort if <c>IsValid</c> is false. <see cref="VpnEngine.StartAsync"/>
    /// invokes this at the top of its non-custom branch.
    /// </summary>
    public static ValidationResult ValidateAppSettings(AppSettings settings)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings == null)
        {
            errors.Add("AppSettings is null");
            return new ValidationResult { Errors = errors, Warnings = warnings };
        }

        var configMode = (settings.App?.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        var isGenerated = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);
        // Custom mode loads JSON from disk — out of scope for this check.

        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();
        var enabledSubs = subs.Where(s => s != null && s.Enabled).ToList();
        var enabledSubsWithServers = enabledSubs
            .Where(s => s.Servers != null && s.Servers.Count > 0)
            .ToList();
        var manualServerCount = settings.Vless?.Servers?.Count ?? 0;
        var hasLegacyVlessServer = !string.IsNullOrWhiteSpace(settings.Vless?.Server);

        if (isSubscribe)
        {
            if (subs.Count == 0)
            {
                errors.Add(
                    "ConfigMode=subscribe but no subscriptions are registered. " +
                    "Either register a subscription URL (Subscribe tab) or switch ConfigMode " +
                    "back to 'generated'/'custom' before connecting.");
            }
            else if (enabledSubs.Count == 0)
            {
                errors.Add(
                    "ConfigMode=subscribe but every subscription is disabled. " +
                    "Enable at least one subscription before connecting.");
            }
            else if (enabledSubsWithServers.Count == 0 && manualServerCount == 0 && !hasLegacyVlessServer)
            {
                // This is the F-12 silent-leak class. We have an enabled
                // subscription but no servers fetched yet and no manual
                // fallback — generating a config now would leave proxy
                // outbounds empty.
                errors.Add(
                    "ConfigMode=subscribe but no subscription has fetched any servers and " +
                    "no manual VLESS server is configured as a fallback. " +
                    "Click 'Refresh All' on the Subscribe tab to fetch servers " +
                    "before connecting (F-12 silent-leak guard, parity audit).");
            }
        }
        else if (isGenerated)
        {
            if (manualServerCount == 0 && !hasLegacyVlessServer && enabledSubsWithServers.Count == 0)
            {
                errors.Add(
                    "ConfigMode=generated but no VLESS server is configured. " +
                    "Add a server in the Servers tab or switch to subscribe mode.");
            }
        }

        return new ValidationResult { Errors = errors, Warnings = warnings };
    }

    public static ValidationResult ValidateConfig(SingBoxConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. DNS strategy must be ipv4_only
        if (config.Dns.Strategy != "ipv4_only")
            errors.Add($"dns.strategy must be 'ipv4_only', got '{config.Dns.Strategy}'");

        // 2. strict_route must be false (dual stack protection)
        foreach (var inbound in config.Inbounds)
        {
            if (inbound.StrictRoute)
                errors.Add($"inbound '{inbound.Tag}': strict_route must be false to avoid dual stack errors");

            // 3. No IPv6 address in TUN
            if (inbound.Address == null || inbound.Address.Count == 0)
                errors.Add($"inbound '{inbound.Tag}': address is missing");
        }

        // 4. Every process in route rules must have a DNS rule (sing-box 1.12+ action format)
        var processesInRouteRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0
                     && (r.Outbound == "proxy" || r.Action == "route"))
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();

        // DNS rules use action="route" + server (vpn-dns or local-dns depending on dns_mode)
        // Smart mode uses local-dns, vpn_only uses vpn-dns — both are valid leak protection
        var processesInDnsRules = config.Dns.Rules
            .Where(r => (r.Server == "vpn-dns" || r.Server == "local-dns") && r.Action == "route")
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in processesInRouteRules)
        {
            if (!processesInDnsRules.Contains(proc))
                warnings.Add($"Process '{proc}' is routed through proxy but has no DNS rule — DNS may leak");
        }

        // 4b. Full tunnel mode checks
        var isFullTunnel = config.Route.Final == "proxy";
        if (isFullTunnel)
        {
            // In full tunnel, DNS final should be vpn-dns
            if (config.Dns.Final != "vpn-dns")
                warnings.Add("Full tunnel mode: DNS final is not 'vpn-dns' — DNS may bypass VPN");
        }

        // 5. Proxy outbound must exist
        var hasProxy = config.Outbounds.Any(o => o.Tag == "proxy");
        if (!hasProxy)
            errors.Add("No 'proxy' outbound defined");

        // 6. Direct outbound must exist
        // Note: "block" outbound removed in sing-box 1.12+ — now use action: "reject" in route rules
        if (!config.Outbounds.Any(o => o.Tag == "direct"))
            errors.Add("No 'direct' outbound defined");

        // Check that DNS hijack rule exists (replaces legacy "dns" outbound)
        var hasDnsHijack = config.Route.Rules.Any(r => r.Action == "hijack-dns");
        if (!hasDnsHijack)
            warnings.Add("No 'hijack-dns' route rule — DNS traffic may not be handled correctly");

        // 7. Validate proxy outbounds — both "proxy" and optional "proxy-udp"
        foreach (var proxyTag in new[] { "proxy", "proxy-udp" })
        {
            var proxyOutbound = config.Outbounds.FirstOrDefault(o => o.Tag == proxyTag);
            if (proxyOutbound == null) continue;

            // v2.30.1-r4: dispatch validation by outbound type — VLESS,
            // Hysteria2, TUIC, Shadowsocks have different "well-formed"
            // schemas (e.g. Hysteria2 has no uuid, Shadowsocks has no
            // uuid + needs method+password). Pre-r4 the validator
            // unconditionally called ValidateVlessOutbound on every
            // urltest child, which rejected valid Hysteria2 / TUIC / SS
            // entries with "uuid is empty" errors.
            //
            // User report 2026-05-01: pasted hy2://… → Servers connect
            // failed with "VLESS outbound 'vless-is-01-hy2-test':
            // uuid is empty" because the Hysteria2 entry was a child of
            // the urltest selector (multi-server proxy group) and the
            // VLESS validator ran on it.
            ValidateProxyOutbound(proxyOutbound, config, errors, proxyTag);
        }

        return new ValidationResult { Errors = errors, Warnings = warnings };
    }

    private static void ValidateProxyOutbound(
        SingBoxOutbound outbound,
        SingBoxConfig config,
        List<string> errors,
        string proxyTag)
    {
        if (outbound.Type == "urltest")
        {
            if (outbound.Outbounds == null || outbound.Outbounds.Count < 2)
                errors.Add($"urltest outbound '{proxyTag}': must have at least 2 child outbounds");

            var outboundTags = config.Outbounds.Select(o => o.Tag).ToHashSet();
            foreach (var childTag in outbound.Outbounds ?? new())
            {
                if (!outboundTags.Contains(childTag))
                {
                    errors.Add($"urltest '{proxyTag}' references non-existent outbound '{childTag}'");
                    continue;
                }
                var child = config.Outbounds.First(o => o.Tag == childTag);
                ValidateConcreteOutbound(child, errors);
            }
            return;
        }

        ValidateConcreteOutbound(outbound, errors);
    }

    /// <summary>
    /// Per-protocol "well-formed" check. Each branch validates the fields
    /// sing-box requires for that outbound type. Unknown types pass
    /// through silently — sing-box will reject them at startup if they're
    /// truly malformed, which gives a clearer error than us guessing.
    /// </summary>
    private static void ValidateConcreteOutbound(SingBoxOutbound o, List<string> errors)
    {
        var type = (o.Type ?? string.Empty).ToLowerInvariant();
        switch (type)
        {
            case "vless":
                ValidateVlessOutbound(o, errors);
                break;
            case "hysteria2":
                ValidateHysteria2Outbound(o, errors);
                break;
            case "tuic":
                ValidateTuicOutbound(o, errors);
                break;
            case "shadowsocks":
                ValidateShadowsocksOutbound(o, errors);
                break;
            default:
                // Unknown / future protocol — basic sanity only.
                if (string.IsNullOrWhiteSpace(o.Server))
                    errors.Add($"{o.Type} outbound '{o.Tag}': server is empty");
                if (o.ServerPort is null or <= 0)
                    errors.Add($"{o.Type} outbound '{o.Tag}': server_port is invalid");
                break;
        }
    }

    private static void ValidateVlessOutbound(SingBoxOutbound vless, List<string> errors)
    {
        var label = $"VLESS outbound '{vless.Tag}'";
        if (string.IsNullOrWhiteSpace(vless.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(vless.Uuid))
            errors.Add($"{label}: uuid is empty");
        if (vless.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }

    private static void ValidateHysteria2Outbound(SingBoxOutbound hy2, List<string> errors)
    {
        var label = $"Hysteria2 outbound '{hy2.Tag}'";
        if (string.IsNullOrWhiteSpace(hy2.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(hy2.Password))
            errors.Add($"{label}: password is empty");
        if (hy2.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }

    private static void ValidateTuicOutbound(SingBoxOutbound tuic, List<string> errors)
    {
        var label = $"TUIC outbound '{tuic.Tag}'";
        if (string.IsNullOrWhiteSpace(tuic.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(tuic.Uuid))
            errors.Add($"{label}: uuid is empty");
        // TUIC v5 password is sometimes empty — only warn if the server
        // explicitly required it via the share-link, which we don't
        // currently track. So no password check.
        if (tuic.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }

    private static void ValidateShadowsocksOutbound(SingBoxOutbound ss, List<string> errors)
    {
        var label = $"Shadowsocks outbound '{ss.Tag}'";
        if (string.IsNullOrWhiteSpace(ss.Server))
            errors.Add($"{label}: server is empty");
        if (string.IsNullOrWhiteSpace(ss.Method))
            errors.Add($"{label}: method (cipher) is empty");
        if (string.IsNullOrWhiteSpace(ss.Password))
            errors.Add($"{label}: password is empty");
        if (ss.ServerPort is null or <= 0)
            errors.Add($"{label}: server_port is invalid");
    }
}
