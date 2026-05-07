using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.32.0 (AND-ZAPRET, 2026-05-07) — pure-JSON helper that mutates a
/// sing-box config to enable native DPI-bypass via the
/// <c>tls_fragment</c> / <c>udp_fragment</c> outbound dialer options.
/// Called from <c>VPNRouter.Android.AndroidConfigBuilder</c> after both
/// <c>BuildConfigJson(VlessServerEntry)</c> and
/// <c>BuildConfigJsonFromCustom</c> finish their normal generation, so
/// every Android config-build path picks the user's choice up.
///
/// <para>Lives in <see cref="VPNRouter.Core"/> rather than the Android
/// project because the manipulation is pure JSON — no Android API
/// surface — and putting it here lets <c>VPNRouter.Tests</c> run
/// unit tests against it without ProjectReference'ing Android (which
/// targets <c>net8.0-android</c>, incompatible with the test
/// project's <c>net8.0</c> TFM).</para>
///
/// <para><b>Strategy matrix</b> (mirrors the desktop Zapret strategy
/// picker semantically; different mechanism, same UX intent):</para>
/// <list type="table">
///   <listheader><term>mode</term><description>tls_fragment block · udp_fragment</description></listheader>
///   <item><term>off</term><description>not injected (no-op)</description></item>
///   <item><term>standard</term><description>{enabled:true, size:"10-100", sleep:"10-50"} · false</description></item>
///   <item><term>aggressive</term><description>{enabled:true, size:"5-20", sleep:"50-150"} · true</description></item>
/// </list>
///
/// <para>Only mutates outbounds whose <c>type</c> is a real upstream
/// proxy (<c>vless</c>, <c>hysteria2</c>, <c>tuic</c>,
/// <c>shadowsocks</c>/<c>ss</c>, <c>trojan</c>, <c>http</c>,
/// <c>socks</c>, <c>shadowtls</c>). Skips <c>direct</c>, <c>block</c>,
/// <c>dns</c>, <c>selector</c>, <c>urltest</c> — those don't reach a
/// remote DPI-blocked host (or are control-plane only) so fragmenting
/// them either has no effect or breaks local DNS.</para>
///
/// <para><b>Idempotent + overwrite</b>: existing
/// <c>tls_fragment</c>/<c>udp_fragment</c> on a matched outbound is
/// overwritten. Most-recent-action-wins keeps the picker's intent
/// honest: a user pasting custom JSON with their own
/// <c>tls_fragment.size</c> and then picking "aggressive" sees the
/// aggressive values applied. Escape hatch for power users: keep
/// the picker on "off", which short-circuits this helper before
/// any mutation happens.</para>
/// </summary>
public static class AndroidDpiBypassInjector
{
    /// <summary>
    /// Outbound types that route to a real upstream — these are the
    /// candidates for fragmentation. http/socks listed because users
    /// who paste a custom JSON might layer a HTTP or SOCKS proxy
    /// upstream of sing-box (rare on mobile but valid). shadowtls is
    /// a bona-fide TLS upstream so it gets fragmentation too.
    /// </summary>
    private static readonly HashSet<string> ProxyTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "vless", "hysteria2", "tuic", "shadowsocks", "ss",
            "trojan", "http", "socks", "shadowtls",
        };

    /// <summary>
    /// Apply DPI-bypass mutation to <paramref name="json"/> per
    /// <paramref name="mode"/>. Returns the input verbatim when
    /// mode is "off", null/empty, or unrecognised — so callers can
    /// pass arbitrary strings without surprising side-effects.
    /// On JSON parse failure also returns the input verbatim;
    /// libbox will surface the real shape problem with a more
    /// useful error than a partial mutation.
    /// </summary>
    /// <param name="json">A pretty-printed sing-box JSON config.</param>
    /// <param name="mode">"off" | "standard" | "aggressive"
    /// (case-insensitive).</param>
    public static string Inject(string json, string mode)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        if (string.IsNullOrEmpty(mode) ||
            string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return json;
        }

        // Strategy parameters. Values picked from sing-box upstream
        // guidance + the AND-ZAPRET handbook §7 Phase 8.4 reference.
        // No tunable knobs in the UI for now; power users can paste
        // a custom JSON with their own block + keep the picker off.
        string fragmentSize, fragmentSleep;
        bool injectUdpFragment;
        switch (mode.ToLowerInvariant())
        {
            case "aggressive":
                fragmentSize = "5-20";
                fragmentSleep = "50-150";
                injectUdpFragment = true;
                break;
            case "standard":
                fragmentSize = "10-100";
                fragmentSleep = "10-50";
                injectUdpFragment = false;
                break;
            default:
                // Unknown mode → no-op (defensive — AndroidStorage SR-1
                // already normalises to "off" but tests / direct callers
                // shouldn't be punished for typos).
                return json;
        }

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return json;
            if (root["outbounds"] is not JsonArray outbounds) return json;

            foreach (var node in outbounds)
            {
                if (node is not JsonObject ob) continue;
                var type = ob["type"]?.GetValue<string>();
                if (string.IsNullOrEmpty(type)) continue;
                if (!ProxyTypes.Contains(type!)) continue;

                // Build the tls_fragment block fresh — overwrite anything
                // that was there. Idempotent across repeated calls with
                // the same mode; mode change is reflected on next build.
                ob["tls_fragment"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["size"] = fragmentSize,
                    ["sleep"] = fragmentSleep,
                };

                if (injectUdpFragment)
                {
                    ob["udp_fragment"] = true;
                }
                else
                {
                    // Don't leave a stale udp_fragment behind from a
                    // previous "aggressive" build — explicit remove so
                    // a mode flip aggressive→standard cleans up.
                    ob.Remove("udp_fragment");
                }
            }

            return root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch
        {
            // Swallow parse / mutation errors; libbox will report a
            // clearer message on its own attempt to use the unchanged
            // input than we could from here.
            return json;
        }
    }
}
