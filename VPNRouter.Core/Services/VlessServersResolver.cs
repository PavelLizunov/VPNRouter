using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Single source of truth for resolving the effective VLESS server list
/// from any source — manual entries, legacy single-server fields, or
/// active subscriptions. Mutates <c>settings.Vless.Servers</c> in place
/// so downstream consumers (<see cref="ConfigGenerator"/>,
/// <see cref="HealthMonitor"/>) get a consistent view regardless of
/// which entry path was taken (CLI / GUI / Service / hot-reload Apply).
///
/// <para>Background — bug discovered in v2.28.1 field-test (2026-04-26):
/// <see cref="VpnEngine.StartAsync"/> had its own aggregation guard,
/// but <see cref="VpnEngine.Apply"/> (the hot-reload path) didn't.
/// On a hot-reload triggered by a settings change, Apply read fresh
/// <c>settings.Vless.Servers = []</c> straight from YAML (subscriptions
/// store servers in <c>App.Subscriptions[].Servers</c>, NOT in
/// <c>Vless.Servers</c>) and produced a sing-box JSON with NO proxy
/// outbound. sing-box loaded this config, silently ignored route rules
/// pointing at the missing <c>"proxy"</c> tag, and routed all process
/// traffic to <c>route.final: "direct"</c>. The user's sing-box also
/// kept attempting urltest probes to the upstream VLESS server, which
/// hit the server without a proper VLESS handshake and produced 249
/// "flow mismatch: expected xtls-rprx-vision but got none" errors per
/// day in the server log. This resolver eliminates that class of bug
/// by ensuring all entry points use the same aggregation logic.</para>
/// </summary>
public static class VlessServersResolver
{
    /// <summary>
    /// Resolve the effective VLESS server list and write it into
    /// <c>settings.Vless.Servers</c> (in-memory only, not persisted).
    /// Idempotent: repeated calls are safe.
    ///
    /// <para>r10 Fix-A (2026-05-11) — scope guard for subscription mode:
    /// when <c>config_mode</c> is either "subscribe" OR "generated" and at
    /// least one enabled subscription has servers, ONLY subscription
    /// servers are returned. Legacy <c>vless.servers[]</c> entries are
    /// ignored — they are remnants of direct-VLESS mode that survive
    /// after the user adds a subscription, and a stale
    /// <c>vless.active_server</c> pointing at one of them was silently
    /// shadow-overriding live subscription routing (stas's case:
    /// the legacy <c>khunrath_ln</c> placeholder entry — see
    /// <see cref="PlaceholderDefense.KnownFingerprints"/> for the literal
    /// host triple — shadowed working <c>de-01 443</c>).</para>
    ///
    /// <para>If <c>vless.active_server</c> points to a server NOT in the
    /// resulting scoped list, it is overwritten with the first scoped
    /// entry's name + a WARN log is emitted via <paramref name="logger"/>.
    /// Persisting the corrected value is the caller's responsibility
    /// (Fix-B in <c>SettingsMigrator</c>).</para>
    /// </summary>
    /// <returns>The resolved server list (same reference as <c>settings.Vless.Servers</c> after the call).</returns>
    public static List<VlessServerEntry> Resolve(AppSettings settings, ILogger? logger = null)
    {
        var configMode = (settings.App.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        var isGenerated = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);

        // r10 Fix-A scope guard: aggregate subscription servers when ANY
        // generative mode (subscribe OR generated) has enabled subs with
        // fetched servers. In generated mode this used to fall through to
        // Vless.Servers, allowing legacy direct-VLESS entries to shadow
        // the working subscription.
        var subscriptionAggregated = (settings.App.Subscriptions ?? new())
            .Where(s => s != null && s.Enabled && s.Servers != null)
            .SelectMany(s => s.Servers)
            .Where(s => !string.IsNullOrWhiteSpace(s?.Server) && s.Server != "your.server.com")
            .ToList();

        var hasActiveSubscriptionServers = subscriptionAggregated.Count > 0;

        // r10 r7 (Bug-r10-E, 2026-05-11 brat report) — refined scope-guard
        // trigger. Pre-r7 the guard fired when (subscribe||generated) &&
        // hasSubs, which silently overrode user's MANUAL VLESS choice in
        // generated mode (e.g. Free Configs entry the user clicked, US/NL
        // servers added via paste). Brat's log showed:
        //   ReconnectAsync.ManualVless: forced ConfigMode=generated,
        //     ActiveServer=⚡ [US] 193.233.217.174:443
        //   [WRN] Active server '⚡ [US]...' not in current scope.
        //         Falling back to 'de-01 443 main-brat'.
        // Symptoms: chosen server not highlighted, traffic routed via
        // subscription IP instead of the picked one.
        //
        // Differentiation between stas vs brat:
        //   - Stas case: vless.active_server points at an entry whose
        //     server/pubkey/short_id match KNOWN PLACEHOLDER lists from
        //     ConfigSanityCheck (Phase-1 F-E data) — legacy carry-over,
        //     user never explicitly chose it.
        //   - Brat case: vless.active_server points at an entry with real
        //     server + real Reality fields — user explicitly clicked via
        //     manual list / Free Configs.
        //
        // Guard fires when:
        //   - Subscribe mode + sub enabled (subscribe contract), OR
        //   - Generated mode + sub enabled AND active entry is missing
        //     (orphan / empty / "not present in vless.servers") OR matches
        //     a known placeholder pattern.
        // Otherwise we respect the manual selection.
        var activeEntry = (settings.Vless.Servers ?? new())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s?.Name)
                && s.Name.Equals(settings.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase));

        var activeIsLegitimateManual = activeEntry != null && !IsPlaceholderEntry(activeEntry);

        // Path 1: subscription wins
        if (hasActiveSubscriptionServers
            && (isSubscribe || (isGenerated && !activeIsLegitimateManual)))
        {
            settings.Vless.Servers = subscriptionAggregated;

            // Carry over the active selection from the subscribe-mode setting.
            // In SUBSCRIBE mode App.ActiveSubscriptionServer is authoritative and
            // must OVERRIDE a stale vless.active_server — diag 20260703-002353: a
            // truncated stale value ("main-brat", matching no scoped name) survived
            // here, so the r10 stale-check below fell back to scoped[0], silently
            // switching a user on "Germany AWG" onto "Germany VLESS" (a throttled
            // protocol) during the coexist-VPN route-exclude re-apply. In the
            // generated-mode fallback we still only fill when the manual pick is empty.
            if (!string.IsNullOrEmpty(settings.App.ActiveSubscriptionServer)
                && (isSubscribe || string.IsNullOrEmpty(settings.Vless.ActiveServer)))
            {
                settings.Vless.ActiveServer = settings.App.ActiveSubscriptionServer;
            }

            // r10 Fix-A core check: stale active_server falls back to scoped[0].
            // Two ways a server is "in scope": by Name match (canonical) or by
            // host:port match (when names diverge but server identity is the
            // same).
            if (!string.IsNullOrEmpty(settings.Vless.ActiveServer))
            {
                var oldActive = settings.Vless.ActiveServer;
                var matchByName = subscriptionAggregated.Any(s =>
                    !string.IsNullOrEmpty(s.Name)
                    && s.Name.Equals(oldActive, StringComparison.OrdinalIgnoreCase));

                if (!matchByName)
                {
                    var newActive = subscriptionAggregated[0].Name;
                    logger?.Warning(
                        "[VlessServersResolver] Active server '{Old}' not in current scope " +
                        "(subscription mode). Falling back to '{New}'.",
                        oldActive,
                        newActive);
                    settings.Vless.ActiveServer = newActive;
                }
            }

            logger?.Information(
                "[VlessServersResolver] Aggregated {Count} server(s) from {Subs} active subscription(s) " +
                "(mode: {Mode}, active: {Active})",
                subscriptionAggregated.Count,
                settings.App.Subscriptions?.Count(s => s != null && s.Enabled) ?? 0,
                configMode,
                string.IsNullOrEmpty(settings.Vless.ActiveServer) ? "first" : settings.Vless.ActiveServer);

            return subscriptionAggregated;
        }

        if (isSubscribe)
        {
            // Subscribe mode but no subscription servers (yet).
            // Fall through to manual list — could still be populated.
            logger?.Warning(
                "[VlessServersResolver] config_mode=subscribe but no enabled subscription has servers. " +
                "Falling back to manually-configured Vless.Servers / Vless.Server.");
        }

        // Path 2: manual VLESS config — uses Vless.Servers list, falling
        // back to legacy scalar Vless.Server fields. GetEffectiveServers
        // already handles that fallback. Reached when:
        //   - config_mode=generated AND no enabled subscriptions w/ servers
        //   - config_mode=subscribe AND no enabled subscriptions w/ servers (legacy fallback)
        //   - any other mode (custom is handled separately by VpnEngine)
        var manual = settings.Vless.GetEffectiveServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com")
            .ToList();

        // GetEffectiveServers may return Vless.Servers BY REFERENCE; only
        // re-assign if we filtered something out (otherwise it's a no-op).
        if (manual.Count != (settings.Vless.Servers?.Count ?? 0))
            settings.Vless.Servers = manual;

        if (manual.Count > 0)
        {
            logger?.Debug(
                "[VlessServersResolver] Using {Count} manually-configured VLESS server(s)",
                manual.Count);
        }

        return manual;
    }

    /// <summary>
    /// r10 r7 (Bug-r10-E) — does this entry look like a stas-class
    /// placeholder? Used by the scope guard to decide whether a
    /// generated-mode active server is a legacy orphan (placeholder →
    /// subscription wins) or a legitimate manual choice (real entry →
    /// manual respected).
    ///
    /// <para>v3.0 Phase 3D (2026-05-18): forwards to
    /// <see cref="PlaceholderDefense.LayerA_ResolverScopeGuard.IsPlaceholderEntry"/>
    /// so the F-A scope-guard logic shares a single source of truth with
    /// the rest of the 6-layer defense.</para>
    /// </summary>
    internal static bool IsPlaceholderEntry(VlessServerEntry entry) =>
        PlaceholderDefense.LayerA_ResolverScopeGuard.IsPlaceholderEntry(entry);

    /// <summary>
    /// Diagnostic helper for callers that need a clear "why is this empty"
    /// signal without throwing. Returns a localizable English-language reason
    /// the resolver couldn't find any servers, or null if the list is non-empty.
    /// </summary>
    public static string? DescribeEmptyReason(AppSettings settings)
    {
        var configMode = (settings.App.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);

        if (isSubscribe)
        {
            if (settings.App.Subscriptions == null || settings.App.Subscriptions.Count == 0)
                return "Subscribe mode is selected but no subscription URLs are configured. Add a subscription in the Subscribe tab.";

            var enabled = settings.App.Subscriptions.Where(s => s != null && s.Enabled).ToList();
            if (enabled.Count == 0)
                return "Subscribe mode is selected but every subscription is disabled. Enable at least one subscription.";

            var withServers = enabled.Where(s => s.Servers != null && s.Servers.Count > 0).ToList();
            if (withServers.Count == 0)
                return "Subscribe mode: no subscription has fetched any servers yet. Click 'Refresh All' on the Subscribe tab — if it fails, check the subscription URL.";
        }

        if ((settings.Vless.Servers == null || settings.Vless.Servers.Count == 0)
            && string.IsNullOrWhiteSpace(settings.Vless.Server))
        {
            return "VLESS server is not configured. Add a server manually in the Servers tab, or enable a subscription.";
        }

        return null;
    }
}
