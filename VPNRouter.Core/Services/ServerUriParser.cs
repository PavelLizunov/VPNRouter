using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Web;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Multi-protocol share-link URI parser. v2.30.1-r3.
///
/// <para>Dispatches by scheme to per-protocol parsers and produces a
/// populated <see cref="VlessServerEntry"/> with the correct
/// <see cref="VlessServerEntry.Protocol"/> discriminator. Subscription
/// fetchers and Simple-mode paste both call into this entry point so a
/// pasted Hysteria2 / TUIC / Shadowsocks URI ends up as a regular row
/// in the Servers list, just like a VLESS URI.</para>
///
/// <para>Supported schemes:</para>
/// <list type="bullet">
/// <item><c>vless://</c> — delegates to <see cref="VlessUriParser"/></item>
/// <item><c>hysteria2://</c> (also <c>hy2://</c>) — Hysteria2 with optional Salamander obfs</item>
/// <item><c>tuic://</c> — TUIC v5 (uuid:password userinfo)</item>
/// <item><c>ss://</c> — Shadowsocks (incl. 2022 ciphers + ShadowTLS plugin opts)</item>
/// <item><c>naive://</c> (also <c>naive+https://</c> / <c>naive+quic://</c>) — NaiveProxy (Windows + Linux only — needs libcronet)</item>
/// </list>
///
/// <para>v2.32.3 input gate (2026-05-17): the vless:// branch inherits
/// placeholder rejection from <see cref="VlessUriParser.Parse"/>. The
/// Hysteria2 / TUIC / Shadowsocks branches additionally pipe their
/// output through <see cref="PlaceholderGuard.Inspect(VlessServerEntry?)"/>
/// — placeholder server-IPs in particular can land in non-VLESS URIs
/// too (Z:\kanareik-class incident).</para>
/// </summary>
public static class ServerUriParser
{
    /// <summary>
    /// Whether NaiveProxy is usable on the current platform. sing-box's naive
    /// outbound needs Chromium Cronet (<c>libcronet</c>), which SagerNet ships
    /// only for Windows + Linux desktop — never macOS (any version) and never
    /// the Android libbox build. When false, naive URIs are refused at intake:
    /// subscription lines are silently skipped (<see cref="IsSupportedScheme"/>
    /// returns false) and a manual paste throws a clear <see cref="FormatException"/>.
    /// This guarantees a naive server can never reach config generation and
    /// FATAL sing-box at start on a platform that can't run it.
    /// <para>Settable so tests can simulate an unsupported platform on a
    /// Windows test host (reset it in a finally).</para>
    /// </summary>
    public static bool NaiveRuntimeAvailable { get; internal set; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// dns-tunnel (slipstream) needs the native slipstream-client sidecar, which
    /// exists for Windows + Linux only (Rust/picoquic). macOS / Android refuse the
    /// scheme at intake so it can never reach config-gen. Settable for tests.
    /// </summary>
    public static bool SlipstreamRuntimeAvailable { get; internal set; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>Parse any supported share-link URI. Throws <see cref="FormatException"/> on unsupported scheme or malformed input.</summary>
    /// <remarks>
    /// v2.32.3: VLESS goes through <see cref="VlessUriParser.Parse"/> which
    /// has its own placeholder check. Non-VLESS branches get an explicit
    /// gate here. <see cref="PlaceholderConfigException"/> is distinct from
    /// <see cref="FormatException"/> so callers can render "fix your VPN
    /// provider URL" rather than "fix your typo" guidance.
    /// </remarks>
    public static VlessServerEntry Parse(string uri)
    {
        uri = (uri ?? string.Empty).Trim();
        if (uri.Length == 0)
            throw new FormatException("Empty URI");

        if (uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return VlessUriParser.Parse(uri); // VLESS path checks placeholder internally.

        VlessServerEntry entry;
        if (uri.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            entry = ParseHysteria2(uri);
        else if (uri.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase))
            entry = ParseTuic(uri);
        else if (uri.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            entry = ParseShadowsocks(uri);
        else if (uri.StartsWith("naive://", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("naive+https://", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("naive+quic://", StringComparison.OrdinalIgnoreCase))
        {
            // Platform gate: naive needs sing-box's Cronet runtime (Win/Linux
            // only). Refuse at intake on macOS / Android so it can never reach
            // config-gen and FATAL sing-box. ParseMultiple pre-filters via
            // IsSupportedScheme; this guards the direct / manual-paste path.
            if (!NaiveRuntimeAvailable)
                throw new FormatException(
                    "NaiveProxy is supported only on Windows and Linux (it needs sing-box's Cronet runtime). " +
                    "This platform can't use naive servers — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            entry = ParseNaive(uri);
        }
        else if (uri.StartsWith("dns-tunnel://", StringComparison.OrdinalIgnoreCase))
        {
            // Platform gate (mirror naive): the slipstream-client sidecar is
            // Windows/Linux only. Refuse at intake on macOS/Android.
            if (!SlipstreamRuntimeAvailable)
                throw new FormatException(
                    "DNS-tunnel servers are supported only on Windows and Linux (they need the slipstream-client sidecar). " +
                    "This platform can't use dns-tunnel servers — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            entry = ParseDnsTunnel(uri);
        }
        else
            throw new FormatException($"Unsupported URI scheme. Expected vless:// / hysteria2:// / hy2:// / tuic:// / ss:// / naive:// / dns-tunnel://. Got: {Truncate(uri, 40)}");

        // v2.32.3 input gate (2026-05-17) — placeholder fingerprints can
        // surface in any protocol's server IP. Reject before the entry
        // escapes the parser. Z:\kanareik-class incident: placeholder
        // credential leaked through subscription cache, F-E (runtime)
        // caught it at Connect but user was already stuck.
        var offendingField = PlaceholderGuard.Inspect(entry);
        if (offendingField != null)
        {
            var offendingValue = offendingField switch
            {
                "reality.public_key" => entry.Reality?.PublicKey ?? string.Empty,
                "reality.short_id"   => entry.Reality?.ShortId ?? string.Empty,
                "server"             => entry.Server,
                _                    => string.Empty,
            };
            throw new PlaceholderConfigException(offendingField, offendingValue);
        }

        return entry;
    }

    /// <summary>Try variant — returns null on any error.</summary>
    /// <remarks>
    /// v2.32.3: also returns null for
    /// <see cref="PlaceholderConfigException"/>. Callers that want the
    /// typed reason should use <see cref="Parse(string)"/>.
    /// </remarks>
    public static VlessServerEntry? TryParse(string uri)
    {
        try { return Parse(uri); }
        catch (PlaceholderConfigException) { return null; }
        catch (FormatException) { return null; }
        catch { return null; }
    }

    /// <summary>
    /// Parse multiple URIs from a multi-line blob. Skips empty lines and
    /// any line that doesn't start with one of the supported schemes.
    /// Per-line parse failures are silently dropped — the same forgiving
    /// behaviour as <see cref="VlessUriParser.ParseMultiple"/>.
    /// </summary>
    public static List<VlessServerEntry> ParseMultiple(string text)
    {
        var result = new List<VlessServerEntry>();
        foreach (var line in (text ?? string.Empty).Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!IsSupportedScheme(trimmed)) continue;
            try { result.Add(Parse(trimmed)); } catch { /* skip malformed */ }
        }
        return result;
    }

    /// <summary>Cheap scheme-prefix probe — used by SubscriptionFetcher's per-line filter.</summary>
    public static bool IsSupportedScheme(string line)
    {
        // naive only counts as supported where the Cronet runtime exists
        // (Win/Linux). On macOS / Android this returns false so subscription
        // parsing (ParseMultiple) silently drops naive lines.
        if (line.StartsWith("naive://",       StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("naive+https://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("naive+quic://",  StringComparison.OrdinalIgnoreCase))
            return NaiveRuntimeAvailable;

        // dns-tunnel (slipstream) — Win/Linux only, same intake gate as naive.
        if (line.StartsWith("dns-tunnel://", StringComparison.OrdinalIgnoreCase))
            return SlipstreamRuntimeAvailable;

        return line.StartsWith("vless://",     StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("hy2://",       StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("tuic://",      StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("ss://",        StringComparison.OrdinalIgnoreCase);
    }

    // ─── DNS-tunnel (slipstream) ───────────────────────────────────────────
    //
    // Link: dns-tunnel://<base64url-JSON>[#name]
    //   Production server schema (v2 — SHORT keys, authoritative):
    //     { "d": "<domain>", "r": ["195.208.4.1:53", ...], "cert": "<leaf PEM>",
    //       "fp": "<sha256 leaf hex, colon-separated ok>",
    //       "uuid": "<per-user uuid>", "v": 2 }
    //   The long spellings (domain/resolvers/fingerprint) are also accepted so
    //   our own test fixtures + any verbose emitter keep working. "v" is ignored
    //   (forward-compat). The VLESS uuid is reused as-is; the outbound is later
    //   generated against 127.0.0.1:<localPort> (slipstream-client front), so
    //   Server holds the domain only for dedup/display identity.
    private static VlessServerEntry ParseDnsTunnel(string uri)
    {
        var body = uri.Substring("dns-tunnel://".Length);

        string? fragName = null;
        var hashIdx = body.IndexOf('#');
        if (hashIdx >= 0)
        {
            fragName = Uri.UnescapeDataString(body.Substring(hashIdx + 1));
            body = body.Substring(0, hashIdx);
        }
        body = body.Trim();
        if (body.Length == 0)
            throw new FormatException("dns-tunnel: empty base64url payload");

        byte[] jsonBytes;
        try { jsonBytes = DecodeBase64UrlBytes(body); }
        catch { throw new FormatException("dns-tunnel: payload is not valid base64url"); }

        string domain = string.Empty, uuid = string.Empty, fingerprint = string.Empty, cert = string.Empty;
        var resolvers = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("dns-tunnel: payload JSON must be an object");

            // Production server emits short keys (d/r/fp); accept the long
            // spellings too. First present key wins.
            domain      = ReadJsonString(root, "d", "domain");
            uuid        = ReadJsonString(root, "uuid");
            fingerprint = ReadJsonString(root, "fp", "fingerprint");
            cert        = ReadJsonString(root, "cert");
            foreach (var key in new[] { "r", "resolvers" })
            {
                if (!root.TryGetProperty(key, out var r) || r.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in r.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) resolvers.Add(v!.Trim());
                }
                if (resolvers.Count > 0) break; // first non-empty array wins
            }
        }
        catch (JsonException)
        {
            throw new FormatException("dns-tunnel: payload is not valid JSON");
        }

        if (string.IsNullOrWhiteSpace(domain))
            throw new FormatException("dns-tunnel: missing 'domain'");
        if (resolvers.Count == 0)
            throw new FormatException("dns-tunnel: missing 'resolvers'");
        if (string.IsNullOrWhiteSpace(uuid))
            throw new FormatException("dns-tunnel: missing 'uuid'");

        // The leaf PEM is load-bearing: slipstream-client verifies the server
        // against it via --cert. Required + must look like a PEM (full X.509
        // validation is slipstream-client's job, not ours).
        cert = cert.Trim();
        if (cert.Length == 0)
            throw new FormatException("dns-tunnel: missing 'cert' (server leaf PEM)");
        if (!cert.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            throw new FormatException("dns-tunnel: 'cert' is not a PEM certificate (no BEGIN CERTIFICATE marker)");

        return new VlessServerEntry
        {
            Protocol = "dns-tunnel",
            Name = string.IsNullOrWhiteSpace(fragName) ? domain : fragName!,
            Server = domain,   // identity for dedup/display; outbound targets 127.0.0.1
            DnsDomain = domain,
            DnsResolvers = resolvers,
            DnsLeafCertPem = cert,
            DnsLeafFingerprint = fingerprint,
            Uuid = uuid,
        };
    }

    /// <summary>First present string property among <paramref name="names"/>
    /// (tried in order), or empty. Lets one parser accept both the short
    /// production keys (d/r/fp) and the verbose spellings.</summary>
    private static string ReadJsonString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String)
            {
                var v = e.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        return string.Empty;
    }

    /// <summary>base64url (RFC 4648 §5, <c>-_</c>, optional padding) → bytes.</summary>
    private static byte[] DecodeBase64UrlBytes(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
    }

    // ─── Hysteria2 ─────────────────────────────────────────────────────────
    //
    // Spec: https://v2.hysteria.network/docs/developers/URI-Scheme/
    //   hysteria2://password@host:port/?sni=...&insecure=0&obfs=salamander
    //                       &obfs-password=...&pinSHA256=...#name
    //
    // The password is the URL userinfo (NO username, just the password).
    // Reality is not used — Hysteria2 has its own QUIC + TLS layer with
    // optional Salamander obfuscation. ALPN defaults to ["h3"] which we
    // emit at outbound-generation time.

    private static VlessServerEntry ParseHysteria2(string uri)
    {
        // Normalize hy2:// -> hysteria2:// then swap to https:// so System.Uri can parse.
        var normalized = uri;
        if (normalized.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            normalized = "hysteria2://" + normalized.Substring("hy2://".Length);
        var fake = "https://" + normalized.Substring("hysteria2://".Length);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid hysteria2 URI: cannot parse");

        var password = Uri.UnescapeDataString(parsed.UserInfo);
        if (password.Length == 0)
            throw new FormatException("Invalid hysteria2 URI: password missing (expected hysteria2://password@host:port)");

        var server = parsed.Host;
        var port = parsed.Port > 0 ? parsed.Port : 443;
        if (server.Length == 0)
            throw new FormatException("Invalid hysteria2 URI: host missing");

        var query = HttpUtility.ParseQueryString(parsed.Query);
        var name = Uri.UnescapeDataString(parsed.Fragment.TrimStart('#'));

        var entry = new VlessServerEntry
        {
            Name = name.Length > 0 ? name : $"hysteria2-{server}-{port}",
            Protocol = "hysteria2",
            Server = server,
            Port = port,
            Password = password,
            PairGroup = query["pair"] ?? string.Empty, // r5: UDP-sibling pairing tag
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? server,
                // Accept the same insecure spellings as TUIC: insecure=1,
                // allowInsecure=1 (Clash forks), allow_insecure=true (spec).
                Insecure = query["insecure"] == "1"
                        || query["allowInsecure"] == "1"
                        || string.Equals(query["allow_insecure"], "true", StringComparison.OrdinalIgnoreCase),
            },
        };

        var obfs = query["obfs"];
        if (!string.IsNullOrEmpty(obfs))
        {
            entry.ObfsType = obfs;
            entry.ObfsPassword = query["obfs-password"] ?? string.Empty;
        }

        return entry;
    }

    // ─── TUIC v5 ───────────────────────────────────────────────────────────
    //
    // Common community URI form (sing-box / NekoBox compatible):
    //   tuic://uuid:password@host:port?sni=...&congestion_control=bbr
    //                                  &udp_relay_mode=native&alpn=h3#name

    private static VlessServerEntry ParseTuic(string uri)
    {
        var fake = "https://" + uri.Substring("tuic://".Length);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid tuic URI: cannot parse");

        var userinfo = Uri.UnescapeDataString(parsed.UserInfo);
        if (userinfo.Length == 0)
            throw new FormatException("Invalid tuic URI: uuid:password missing");

        // Split uuid:password — exactly one colon. If no colon, treat the
        // whole userinfo as uuid (some servers issue passwordless TUIC).
        string uuid;
        string password;
        var colon = userinfo.IndexOf(':');
        if (colon < 0)
        {
            uuid = userinfo;
            password = string.Empty;
        }
        else
        {
            uuid = userinfo.Substring(0, colon);
            password = userinfo.Substring(colon + 1);
        }

        var server = parsed.Host;
        var port = parsed.Port > 0 ? parsed.Port : 443;
        if (server.Length == 0)
            throw new FormatException("Invalid tuic URI: host missing");

        var query = HttpUtility.ParseQueryString(parsed.Query);
        var name = Uri.UnescapeDataString(parsed.Fragment.TrimStart('#'));

        return new VlessServerEntry
        {
            Name = name.Length > 0 ? name : $"tuic-{server}-{port}",
            Protocol = "tuic",
            Server = server,
            Port = port,
            Uuid = uuid,
            Password = password,
            CongestionControl = query["congestion_control"] ?? "bbr",
            UdpRelayMode = query["udp_relay_mode"] ?? "native",
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? server,
                // v3.0 Phase 6.4 (2026-05-04) — accept all 3 spelling
                // variants seen in the wild. v2rayN/v2rayNG/Hiddify share
                // links emit "insecure=1"; older Clash forks use
                // "allowInsecure=1"; the TUIC spec itself documents
                // "allow_insecure=true". sing-box only takes a single
                // bool, so we set Insecure=true if ANY of these reads
                // truthy. Pre-6.4 only allowInsecure was checked, which
                // meant `tuic://...?insecure=1` URIs always hit
                // "x509: certificate signed by unknown authority"
                // because we silently flipped insecure → false.
                Insecure = query["insecure"] == "1"
                        || query["allowInsecure"] == "1"
                        || string.Equals(query["allow_insecure"], "true",
                                         StringComparison.OrdinalIgnoreCase),
                Alpn = query["alpn"] ?? "h3",
            },
        };
    }

    // ─── Shadowsocks (incl. 2022 + ShadowTLS v3) ──────────────────────────
    //
    // Two URL forms in the wild:
    //   1. Modern (RFC 8089-style):
    //        ss://method:password@host:port?plugin=...#name
    //   2. Legacy (base64-encoded userinfo):
    //        ss://BASE64(method:password)@host:port#name
    // Both forms share query (?plugin=...) and fragment (#name) handling.
    // sing-box understands plugin=shadow-tls;version=3;... directly.

    private static VlessServerEntry ParseShadowsocks(string uri)
    {
        var fake = "https://" + uri.Substring("ss://".Length);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid ss URI: cannot parse");

        var userinfo = parsed.UserInfo;
        if (userinfo.Length == 0)
            throw new FormatException("Invalid ss URI: userinfo missing");

        // Try plain "method:password" first, then base64 fallback.
        string method;
        string password;
        if (userinfo.Contains(':'))
        {
            var colon = userinfo.IndexOf(':');
            method = Uri.UnescapeDataString(userinfo.Substring(0, colon));
            password = Uri.UnescapeDataString(userinfo.Substring(colon + 1));
        }
        else
        {
            // base64 userinfo. Restore base64 padding if missing
            // ("Shadowrocket"-style links sometimes drop trailing '=').
            var padded = userinfo.PadRight(userinfo.Length + (4 - userinfo.Length % 4) % 4, '=');
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded)); }
            catch (Exception ex)
            {
                throw new FormatException($"Invalid ss URI: userinfo is neither plain method:password nor base64 ({ex.Message})");
            }
            var colon2 = decoded.IndexOf(':');
            if (colon2 < 0)
                throw new FormatException("Invalid ss URI: decoded userinfo missing colon separator");
            method = decoded.Substring(0, colon2);
            password = decoded.Substring(colon2 + 1);
        }

        var server = parsed.Host;
        var port = parsed.Port > 0 ? parsed.Port : 443;
        if (server.Length == 0)
            throw new FormatException("Invalid ss URI: host missing");

        var query = HttpUtility.ParseQueryString(parsed.Query);
        var name = Uri.UnescapeDataString(parsed.Fragment.TrimStart('#'));

        var entry = new VlessServerEntry
        {
            Name = name.Length > 0 ? name : $"ss-{server}-{port}",
            Protocol = "shadowsocks",
            Server = server,
            Port = port,
            Method = method,
            Password = password,
        };

        // Shadowsocks plugin (e.g. ShadowTLS v3) — sing-box accepts the
        // plugin string verbatim.
        var plugin = query["plugin"];
        if (!string.IsNullOrEmpty(plugin))
        {
            // Plugin syntax: "shadow-tls;version=3;password=...;host=..."
            // The plugin NAME is the first ';'-separated token; the rest
            // is plugin-specific options.
            var firstSemi = plugin.IndexOf(';');
            if (firstSemi < 0)
            {
                entry.Plugin = plugin;
                entry.PluginOpts = string.Empty;
            }
            else
            {
                entry.Plugin = plugin.Substring(0, firstSemi);
                entry.PluginOpts = plugin.Substring(firstSemi + 1);
            }
        }

        return entry;
    }

    // ─── NaiveProxy (HTTP/2 CONNECT, or HTTP/3 / QUIC, via Chromium Cronet) ─
    //
    // Community share-link form (NekoBox / sing-box subscription import):
    //   naive+https://user:pass@host:port?sni=...#name   (HTTP/2 over TCP)
    //   naive+quic://user:pass@host:port#name            (HTTP/3 over QUIC)
    //   naive://user:pass@host:port#name                 (bare; treated as https)
    //
    // r7 #1: the +quic hint selects HTTP/3 over QUIC (stored as NaiveQuic, emitted
    // as the outbound's `quic` boolean — sing-box's naive has no `network` field), so
    // we just lift credentials + host + sni and emit a minimal naive
    // outbound. Runtime needs libcronet next to sing-box → Windows + Linux
    // only; macOS gating happens at config-generation / UI time, not here
    // (the parser stays platform-neutral so a mac user can still SEE the
    // server in their list with an "unsupported on macOS" marker).

    private static VlessServerEntry ParseNaive(string uri)
    {
        // Strip the scheme (naive / naive+https / naive+quic) up to "://" and
        // swap in https:// so System.Uri can split userinfo/host/port/query.
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            throw new FormatException("Invalid naive URI: missing scheme separator");
        var fake = "https://" + uri.Substring(schemeEnd + 3);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid naive URI: cannot parse");

        var userinfo = Uri.UnescapeDataString(parsed.UserInfo);
        if (userinfo.Length == 0)
            throw new FormatException("Invalid naive URI: user:password missing (expected naive+https://user:pass@host:port)");

        // Split user:password on the FIRST colon. A passwordless form
        // (just a username) is tolerated, mirroring the TUIC branch.
        string username;
        string password;
        var colon = userinfo.IndexOf(':');
        if (colon < 0)
        {
            username = userinfo;
            password = string.Empty;
        }
        else
        {
            username = userinfo.Substring(0, colon);
            password = userinfo.Substring(colon + 1);
        }

        var server = parsed.Host;
        var port = parsed.Port > 0 ? parsed.Port : 443;
        if (server.Length == 0)
            throw new FormatException("Invalid naive URI: host missing");

        var query = HttpUtility.ParseQueryString(parsed.Query);
        var name = Uri.UnescapeDataString(parsed.Fragment.TrimStart('#'));

        return new VlessServerEntry
        {
            Name = name.Length > 0 ? name : $"naive-{server}-{port}",
            Protocol = "naive",
            Server = server,
            Port = port,
            Username = username,
            Password = password,
            PairGroup = query["pair"] ?? string.Empty, // r5: UDP-sibling pairing tag
            NaiveQuic = uri.StartsWith("naive+quic://", StringComparison.OrdinalIgnoreCase), // r7 #1: HTTP/3 over QUIC
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? server,
                // naive rejects insecure/uTLS/alpn — nothing else to carry.
            },
        };
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }
}
