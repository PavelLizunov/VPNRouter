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
            // Wave 39 follow-up (BR-1, brat 2026-05-19): the F-12
            // "no subscription has fetched any servers" branch USED to
            // throw here even when the user had a manual Vless.Server
            // legacy scalar or Vless.Servers list to fall back on. That
            // short-circuited VlessServersResolver's documented fallback
            // path (visible in v2.32.2 logs as
            // "[WRN] [VlessServersResolver] config_mode=subscribe but no
            //  enabled subscription has servers. Falling back to
            //  manually-configured Vless.Servers / Vless.Server.").
            //
            // The resolver runs RIGHT AFTER this check inside
            // StartupPipeline.ExecuteAsync (line ~518), and it will
            // throw on empty aggregate via the
            // `ConfigGenerator empty servers` hard guard further
            // downstream. So we keep the subs.Count == 0 + every-sub-
            // disabled checks (defense-in-depth: those are AppSettings
            // model invariants, not resolver-decidable). The third
            // branch — subs registered, some enabled, none with servers
            // — is deliberately removed: it's the case the resolver was
            // designed to handle via manual fallback. If no manual exists
            // either, the empty-aggregate ConfigGenerator guard catches
            // it with a clear "VLESS servers list is empty" error.
            //
            // The locals below stay referenced by the isGenerated branch
            // so removing them isn't quite a "delete unused" — leaving
            // them where they are makes the diff diff-only-the-branch.
            _ = enabledSubsWithServers;
            _ = manualServerCount;
            _ = hasLegacyVlessServer;
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

    public static ValidationResult ValidateConfig(SingBoxConfig config, AppSettings? settings = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Bug-r9-F-DEFENSIVE Fix-2 (2026-05-11): warn if any proxy outbound
        // dials a server that isn't in the user's subscription / manual VLESS
        // list. Catches stale Custom Config Mode placeholders + silent leak
        // class where a pasted JSON points at a dead / hostile IP.
        //
        // Bug-r10-F-D (2026-05-11) — scope-aware refinement on top of Fix-2.
        // The original Fix-2 united subscription servers AND vless.servers
        // into one set, which let stas-class leaks pass: when a legacy
        // <c>vless.servers[]</c> placeholder shadowed a working subscription,
        // the placeholder IP was still in the union → no warning. F-D
        // tightens the predicate per config_mode (see
        // <see cref="ValidateOutboundServersScopeAware"/>). Settings are
        // optional so existing test callers (without AppSettings) don't
        // break.
        if (settings != null)
            ValidateOutboundServersScopeAware(config, settings, errors, warnings);

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

    /// <summary>
    /// Bug-r10-F-D (2026-05-11): scope-aware outbound validation. Replaces
    /// the post-r9 union-based check (vless.servers ∪ subscriptions[*].servers)
    /// which let stas-class leaks pass: when a legacy placeholder lived in
    /// <c>vless.servers[]</c> alongside a working subscription, the
    /// placeholder IP was still in the union → no warning.
    ///
    /// <para>F-D narrows the allow-list per <c>ConfigMode</c>:</para>
    /// <list type="bullet">
    /// <item><c>custom</c> — user pasted the sing-box JSON directly, so we
    ///   don't sanity-check the server against <c>config.yaml</c> (they're
    ///   the authority on what JSON we ship). We do still verify that a
    ///   <c>proxy</c> outbound exists with non-empty <c>server</c> + a
    ///   positive <c>server_port</c>; an absent / malformed proxy outbound
    ///   would route everything to <c>direct</c> silently.</item>
    /// <item><c>generated</c> or <c>subscribe</c> WITH any enabled
    ///   subscription that has cached servers — the allow-list is the
    ///   ENABLED subscriptions' servers only. <c>vless.servers[]</c> are
    ///   treated as legacy noise and intentionally NOT trusted. An outbound
    ///   server NOT matching any active subscription entry by
    ///   <c>(server, port, uuid)</c> tuple emits a critical leak error.</item>
    /// <item><c>generated</c> or <c>subscribe</c> WITHOUT enabled
    ///   subscriptions (or none has servers yet) — the allow-list falls back
    ///   to <c>vless.servers[]</c> + legacy <c>vless.server</c>. This is the
    ///   pre-subscriptions direct-VLESS path; legacy entries are the only
    ///   source of truth there.</item>
    /// </list>
    ///
    /// <para>Skipped outbound types: <c>direct</c>, <c>block</c>, <c>dns</c>,
    /// <c>selector</c>, <c>urltest</c> — none have a <c>server</c> field of
    /// their own. <c>dns-direct</c> tag is also skipped (internal resolver
    /// outbound emitted by <c>CustomConfigInjector</c>).</para>
    /// </summary>
    private static void ValidateOutboundServersScopeAware(
        SingBoxConfig config, AppSettings settings,
        List<string> errors, List<string> warnings)
    {
        if (config?.Outbounds == null || config.Outbounds.Count == 0)
            return;

        var configMode = (settings.App?.ConfigMode ?? "generated").Trim();
        var isCustom = configMode.Equals("custom", StringComparison.OrdinalIgnoreCase);

        if (isCustom)
        {
            ValidateCustomModeProxyOutbound(config, errors);
            return;
        }

        var allowed = BuildScopedAllowedServers(settings, out var hasEnabledSubsWithServers);

        foreach (var ob in config.Outbounds)
        {
            if (!IsProxyLikeOutbound(ob))
                continue;

            var server = ob.Server?.Trim();
            if (string.IsNullOrEmpty(server))
                continue;

            var port = ob.ServerPort ?? 0;
            var uuid = ob.Uuid?.Trim() ?? string.Empty;

            if (!IsAllowed(allowed, server, port, uuid))
            {
                if (hasEnabledSubsWithServers)
                {
                    // Generated/Subscribe + enabled subs scope — outbound
                    // MUST be from a subscription. Anything else is a
                    // probable leak (legacy vless.servers shadow override
                    // → silent traffic-routing into a dead/hostile IP).
                    errors.Add(
                        $"[LeakProtection] Outbound '{ob.Tag}' points to " +
                        $"{server}:{port} which is not in the active subscription " +
                        $"scope (subscription={true}). Possible legacy vless.servers " +
                        $"leak — placeholder entries are shadowing live subscription " +
                        $"servers. Review config.yaml app.subscriptions[*].servers " +
                        $"and remove stale vless.servers entries.");
                }
                else
                {
                    // Legacy direct-VLESS scope — outbound must match
                    // vless.servers. Keep this as a warning (existing
                    // behaviour) so we don't break the legacy direct-mode
                    // setup, but flag it loudly.
                    warnings.Add(
                        $"[LeakProtection] Outbound '{ob.Tag}' points to " +
                        $"{server}:{port} which is not in your VLESS server list. " +
                        $"Possible leak from stale configuration or placeholder.");
                }
            }
        }
    }

    /// <summary>
    /// Custom-mode validator. The user pastes the entire sing-box JSON, so
    /// we don't sanity-check server IPs against <c>config.yaml</c>. We only
    /// verify the <c>proxy</c> outbound exists and is well-formed enough to
    /// actually carry traffic — an absent / empty <c>server</c> would route
    /// everything to <c>direct</c> silently.
    /// </summary>
    private static void ValidateCustomModeProxyOutbound(
        SingBoxConfig config, List<string> errors)
    {
        var proxy = config.Outbounds.FirstOrDefault(o =>
            string.Equals(o.Tag, "proxy", StringComparison.OrdinalIgnoreCase));

        if (proxy == null)
        {
            errors.Add(
                "[LeakProtection] config_mode=custom but no 'proxy' outbound " +
                "exists in the pasted JSON. Traffic would route to 'direct' " +
                "silently. Add a proxy outbound or switch ConfigMode.");
            return;
        }

        // urltest / selector group outbounds are valid even without a server
        // field of their own — they delegate to children.
        var type = (proxy.Type ?? string.Empty).ToLowerInvariant();
        if (type == "selector" || type == "urltest")
            return;

        if (string.IsNullOrWhiteSpace(proxy.Server))
            errors.Add(
                "[LeakProtection] config_mode=custom: proxy outbound has an " +
                "empty 'server' field. Traffic would fail-to-direct silently.");
        if ((proxy.ServerPort ?? 0) <= 0)
            errors.Add(
                "[LeakProtection] config_mode=custom: proxy outbound has an " +
                "invalid 'server_port' (must be > 0).");
    }

    private static bool IsProxyLikeOutbound(SingBoxOutbound ob)
    {
        var type = (ob.Type ?? string.Empty).ToLowerInvariant();
        if (type == "direct" || type == "block" || type == "dns"
            || type == "selector" || type == "urltest")
            return false;

        // CustomConfigInjector emits 'dns-direct' as a non-empty direct
        // shim with udp_fragment=true — exempt by tag.
        if (string.Equals(ob.Tag, "dns-direct", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Tuple-based allowed-server matcher. We compare against
    /// <c>(server, port, uuid)</c> to defend against an IP that's known
    /// but the port or uuid is different (probably a different physical
    /// server). Empty uuid in the allow-list entry matches any uuid (so
    /// Hysteria2 / Shadowsocks entries — which have no uuid — still match).
    /// </summary>
    private static bool IsAllowed(
        List<VlessServerEntry> allowed, string server, int port, string uuid)
    {
        if (allowed.Count == 0)
            return false;

        foreach (var entry in allowed)
        {
            var entryServer = entry?.Server?.Trim();
            if (string.IsNullOrEmpty(entryServer))
                continue;

            if (!string.Equals(entryServer, server, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry!.Port != port && port > 0 && entry.Port > 0)
                continue;

            var entryUuid = entry.Uuid?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(entryUuid)
                && !string.IsNullOrEmpty(uuid)
                && !string.Equals(entryUuid, uuid, StringComparison.OrdinalIgnoreCase))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the scope-aware allow-list from settings. If any enabled
    /// subscription has at least one server, returns only subscription
    /// servers + cached subscription servers (legacy <c>vless.servers</c>
    /// are NOT included — they may be stale placeholders). Otherwise
    /// returns <c>vless.servers</c> + legacy single-server fallback.
    /// </summary>
    /// <param name="hasEnabledSubsWithServers">
    /// True when the returned set came from the subscription scope (used
    /// by the caller to choose Error vs Warning severity).
    /// </param>
    internal static List<VlessServerEntry> BuildScopedAllowedServers(
        AppSettings settings, out bool hasEnabledSubsWithServers)
    {
        var subscriptionServers = new List<VlessServerEntry>();

        var subs = settings.App?.Subscriptions;
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (sub == null || !sub.Enabled) continue;
                if (sub.Servers == null) continue;
                foreach (var s in sub.Servers)
                {
                    if (s != null && !string.IsNullOrWhiteSpace(s.Server))
                        subscriptionServers.Add(s);
                }
            }
        }

        // Cached subscription servers — same trust tier as live subs
        // (used during offline startup, populated by SubscriptionResolver
        // before live subs land).
        var cached = settings.App?.SubscriptionServers;
        if (cached != null)
        {
            foreach (var s in cached)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server))
                    subscriptionServers.Add(s);
            }
        }

        hasEnabledSubsWithServers = subscriptionServers.Count > 0;

        // r10 r7 (Bug-r10-E parallel fix, 2026-05-11) — generated-mode
        // users should be able to switch between subscription servers
        // AND their own manually-added entries (Free Configs, paste-ins).
        // Pre-r7 F-D returned ONLY subscription when sub was enabled,
        // which rejected legitimate manual VLESS picks with "scope" Error.
        // Brat's case: ConfigMode=generated, sub enabled, active=Free
        // Config US entry → outbound.server = 193.233.217.174 → F-D pre-r7
        // saw "not in subscription scope" → Error. Now in generated mode
        // we UNION subscription + vless.servers so both are allowed.
        // Stas-class placeholders are still caught by F-A resolver (swap
        // to subscription) + F-E ConfigSanityCheck (pre-start placeholder
        // detection), so we don't need F-D to also be a placeholder gate.
        var configMode = (settings.App?.ConfigMode ?? "generated").Trim();
        var isGenerated = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);

        if (hasEnabledSubsWithServers && !isGenerated)
            return subscriptionServers;

        if (hasEnabledSubsWithServers && isGenerated)
        {
            // Union subscription + non-placeholder vless.servers. We
            // include manual entries so brat-class users (Free Configs
            // picked, real IPs / pubkeys) can connect, but exclude any
            // known-placeholder entries so the stas defense-in-depth
            // gate still rejects them at validation level (even if
            // somehow a placeholder reached ConfigGenerator).
            var union = new List<VlessServerEntry>(subscriptionServers);
            var manualForUnion = settings.Vless?.Servers;
            if (manualForUnion != null)
            {
                foreach (var s in manualForUnion)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.Server)) continue;
                    if (VlessServersResolver.IsPlaceholderEntry(s)) continue;
                    union.Add(s);
                }
            }
            return union;
        }

        // Fallback: legacy direct-VLESS scope.
        var legacy = new List<VlessServerEntry>();

        var manual = settings.Vless?.Servers;
        if (manual != null)
        {
            foreach (var s in manual)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server))
                    legacy.Add(s);
            }
        }

        var legacyServer = settings.Vless?.Server;
        if (!string.IsNullOrWhiteSpace(legacyServer))
        {
            legacy.Add(new VlessServerEntry
            {
                Server = legacyServer!.Trim(),
                Port = settings.Vless?.Port ?? 0,
                Uuid = settings.Vless?.Uuid ?? string.Empty,
            });
        }

        return legacy;
    }
}
