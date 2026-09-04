using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
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
/// output through <see cref="PlaceholderDefense.Inspect(VlessServerEntry?)"/>
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
        else if (uri.StartsWith("amneziawg://", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("awg://", StringComparison.OrdinalIgnoreCase))
        {
            // Runtime gate (mirror naive / dns-tunnel): AmneziaWG needs the
            // sing-box-lx fork (with_awg). Official builds bundle upstream
            // sing-box, which FATALs on an `endpoints` wireguard block. Refuse
            // at intake so a hostile / stale subscription line can't brick an
            // official-build tunnel. ParseMultiple pre-filters via
            // IsSupportedScheme; this guards the direct / manual-paste path.
            if (!SingBoxFeatures.AwgAvailable)
                throw new FormatException(
                    "AmneziaWG requires a sing-box-lx (with_awg) build. This build bundles " +
                    "upstream sing-box — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            entry = ParseAmneziaWg(uri);
        }
        else
            throw new FormatException($"Unsupported URI scheme. Expected vless:// / hysteria2:// / hy2:// / tuic:// / ss:// / naive:// / dns-tunnel:// / awg://. Got: {Truncate(CanaryPolicy.RedactUrl(uri), 40)}");

        // v2.32.3 input gate (2026-05-17) — placeholder fingerprints can
        // surface in any protocol's server IP. Reject before the entry
        // escapes the parser. Z:\kanareik-class incident: placeholder
        // credential leaked through subscription cache, F-E (runtime)
        // caught it at Connect but user was already stuck.
        var offendingField = PlaceholderDefense.Inspect(entry);
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
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var lineSpan in MemoryExtensions.EnumerateLines(text.AsSpan()))
        {
            var trimmedSpan = lineSpan.Trim();
            if (trimmedSpan.IsEmpty || !IsSupportedScheme(trimmedSpan)) continue;
            var trimmed = trimmedSpan.ToString();
            try { result.Add(Parse(trimmed)); } catch { /* skip malformed */ }
        }
        return result;
    }

    /// <summary>Cheap scheme-prefix probe — used by SubscriptionFetcher's per-line filter.</summary>
    public static bool IsSupportedScheme(string line) => IsSupportedScheme(line.AsSpan());

    /// <summary>Cheap scheme-prefix probe on span.</summary>
    public static bool IsSupportedScheme(ReadOnlySpan<char> line)
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

        // AmneziaWG — only where the sing-box-lx fork (with_awg) is bundled.
        // On an official build this returns false so subscription parsing
        // (SubscriptionFetcher / ParseMultiple) silently drops awg:// lines
        // instead of feeding a tunnel-bricking endpoint into config-gen.
        if (line.StartsWith("amneziawg://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("awg://", StringComparison.OrdinalIgnoreCase))
            return SingBoxFeatures.AwgAvailable;

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
        var authoritative = new List<string>();
        var useSystemResolver = false;
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
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    var t = v!.Trim();
                    // A "system"/"auto"/"os" sentinel means: use the OS/operator
                    // default resolver discovered at connect time, not a hardcoded
                    // IP. On a strict RU mobile whitelist the НСДИ IPs are
                    // L3-blocked and only the operator resolver is reachable, so
                    // this is the operator-agnostic WL-BYPASS path. Concrete IPs in
                    // the same array are kept in DnsResolvers as a fallback.
                    if (IsSystemResolverSentinel(t)) { useSystemResolver = true; continue; }
                    resolvers.Add(t);
                }
                if (useSystemResolver || resolvers.Count > 0) break; // first usable array wins
            }

            // OPTIONAL authoritative endpoint(s): query the tunnel server's NS
            // directly, bypassing the rate-limiting recursive resolver. Accept a
            // single string OR an array; short "auth" or long "authoritative".
            foreach (var key in new[] { "auth", "authoritative" })
            {
                if (!root.TryGetProperty(key, out var a)) continue;
                if (a.ValueKind == JsonValueKind.String)
                {
                    var v = a.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) authoritative.Add(v!.Trim());
                }
                else if (a.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in a.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) authoritative.Add(v!.Trim());
                    }
                }
                if (authoritative.Count > 0) break; // first present key wins
            }
        }
        catch (JsonException)
        {
            throw new FormatException("dns-tunnel: payload is not valid JSON");
        }

        if (string.IsNullOrWhiteSpace(domain))
            throw new FormatException("dns-tunnel: missing 'domain'");
        if (resolvers.Count == 0 && !useSystemResolver)
            throw new FormatException("dns-tunnel: missing 'resolvers' (provide IPs or the \"system\" sentinel)");
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
            DnsUseSystemResolver = useSystemResolver,
            DnsAuthoritative = authoritative,
            DnsLeafCertPem = cert,
            DnsLeafFingerprint = fingerprint,
            Uuid = uuid,
        };
    }

    /// <summary>True for the dns-tunnel resolver sentinel tokens that mean
    /// "use the OS/operator default resolver" instead of a hardcoded IP. On a
    /// strict RU mobile whitelist the operator resolver is the only reachable DNS,
    /// so a link publishes <c>"r":["system"]</c> to stay operator-agnostic.</summary>
    private static bool IsSystemResolverSentinel(string s) =>
        s.Equals("system", StringComparison.OrdinalIgnoreCase)
        || s.Equals("auto", StringComparison.OrdinalIgnoreCase)
        || s.Equals("os", StringComparison.OrdinalIgnoreCase)
        || s.Equals("device", StringComparison.OrdinalIgnoreCase);

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
        var schemeLen = uri.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)
            ? "hy2://".Length
            : "hysteria2://".Length;

        ShareLinkHelper.ParseComponents(
            uri.AsSpan(schemeLen),
            out var userinfoSpan,
            out var server,
            out var port,
            out var querySpan,
            out var name);

        var password = ShareLinkHelper.Unescape(userinfoSpan);
        if (password.Length == 0)
            throw new FormatException("Invalid hysteria2 URI: password missing (expected hysteria2://password@host:port)");

        if (server.Length == 0)
            throw new FormatException("Invalid hysteria2 URI: host missing");

        var query = ShareLinkHelper.ParseQuery(querySpan);

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

        // Hysteria2 Brutal CC bandwidth (Mbit/s). Accept ?up=&down= (also upmbps/downmbps).
        // Calibrate to ~70-80% of measured goodput; over-declaring self-induces loss. 0/absent
        // -> sing-box falls back to BBR (the pre-calibration default). RU realtime-game lever.
        entry.HysteriaUpMbps = ParseMbps(query["up"] ?? query["upmbps"] ?? query["up_mbps"]);
        entry.HysteriaDownMbps = ParseMbps(query["down"] ?? query["downmbps"] ?? query["down_mbps"]);

        return entry;
    }

    /// <summary>Parse a Mbit/s value from a query param ("50", "50mbps", " 50 "). Invalid/absent -> 0.</summary>
    private static int ParseMbps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var digits = new string(raw.TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) && v > 0 ? v : 0;
    }

    // ─── AmneziaWG (AWG2) ────────────────────────────────────────────────────
    //   awg://<peer_public_key>@<server>:<port>?private_key=..&address=10.13.13.2/32
    //        &preshared_key=..&keepalive=25&jc=4&jmin=40&jmax=70&s1=86&s2=574
    //        &h1=..&h2=..&h3=..&h4=..&i1=..#Name
    // Requires a sing-box-lx (with_awg) client. The server side is native amneziawg-tools
    // (sing-box-lx has no AWG inbound). h1-h4/i1-i5 are kept as raw strings (ranges/CPS).
    private static VlessServerEntry ParseAmneziaWg(string uri)
    {
        // DON'T use System.Uri here: the peer public key is STANDARD base64 and
        // frequently contains '/', which Uri treats as the authority terminator
        // — truncating the userinfo to empty ("peer public key missing"). Split
        // the components manually: strip scheme, peel the fragment, then the
        // query, then authority on the LAST '@' (base64 has no '@'), then
        // host:port. Uri.UnescapeDataString decodes %XX but preserves '/'/'+'/'='.
        var rest = uri.Trim();
        if (rest.StartsWith("awg://", StringComparison.OrdinalIgnoreCase))
            rest = rest.Substring("awg://".Length);
        else if (rest.StartsWith("amneziawg://", StringComparison.OrdinalIgnoreCase))
            rest = rest.Substring("amneziawg://".Length);

        var hashIdx = rest.IndexOf('#');
        var fragment = hashIdx >= 0 ? rest.Substring(hashIdx + 1) : string.Empty;
        if (hashIdx >= 0) rest = rest.Substring(0, hashIdx);

        var qIdx = rest.IndexOf('?');
        var rawQuery = qIdx >= 0 ? rest.Substring(qIdx + 1) : string.Empty;
        if (qIdx >= 0) rest = rest.Substring(0, qIdx);

        var atIdx = rest.LastIndexOf('@');
        if (atIdx < 0)
            throw new FormatException("Invalid amneziawg URI: peer public key missing (expected awg://<peer_public_key>@host:port)");
        var peerPub = Uri.UnescapeDataString(rest.Substring(0, atIdx));
        if (string.IsNullOrEmpty(peerPub))
            throw new FormatException("Invalid amneziawg URI: peer public key missing (expected awg://<peer_public_key>@host:port)");

        var hostPort = rest.Substring(atIdx + 1);
        string server;
        var port = 51820;
        if (hostPort.StartsWith("["))
        {
            // [IPv6]:port
            var close = hostPort.IndexOf(']');
            server = close > 0 ? hostPort.Substring(1, close - 1) : hostPort;
            var after = close >= 0 ? hostPort.Substring(close + 1) : string.Empty;
            if (after.StartsWith(":") && int.TryParse(after.Substring(1), out var p6) && p6 > 0) port = p6;
        }
        else
        {
            var colonIdx = hostPort.LastIndexOf(':');
            if (colonIdx >= 0)
            {
                server = hostPort.Substring(0, colonIdx);
                if (int.TryParse(hostPort.Substring(colonIdx + 1), out var p) && p > 0) port = p;
            }
            else server = hostPort;
        }
        if (string.IsNullOrEmpty(server))
            throw new FormatException("Invalid amneziawg URI: host missing");

        // WireGuard/AWG keys are STANDARD base64 (frequently contain '+'), and
        // HttpUtility.ParseQueryString decodes '+' as a space — silently
        // corrupting private_key / preshared_key. Parse preserving literal '+'
        // (Uri.UnescapeDataString only decodes %XX, never '+'->space).
        var query = ParseQueryPreservingPlus(rawQuery);
        var name = Uri.UnescapeDataString(fragment);
        var addr = query["address"] ?? query["addr"] ?? string.Empty;
        var privateKey = query["private_key"] ?? query["pk"] ?? string.Empty;
        if (string.IsNullOrEmpty(privateKey))
            throw new FormatException("Invalid amneziawg URI: private_key is required");
        if (string.IsNullOrWhiteSpace(addr))
            throw new FormatException("Invalid amneziawg URI: address is required (e.g. address=10.13.13.2/32)");

        return new VlessServerEntry
        {
            Name = name.Length > 0 ? name : $"amneziawg-{server}-{port}",
            Protocol = "amneziawg",
            Server = server,
            Port = port,
            Awg = new AwgConfig
            {
                PeerPublicKey = peerPub,
                PrivateKey    = privateKey,
                Address       = addr.Length == 0 ? new List<string>()
                                  : addr.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
                PresharedKey  = query["preshared_key"] ?? query["psk"] ?? string.Empty,
                Keepalive     = ParseMbps(query["keepalive"] ?? query["ka"]),
                Jc   = ParseMbps(query["jc"]),  Jmin = ParseMbps(query["jmin"]), Jmax = ParseMbps(query["jmax"]),
                S1   = ParseMbps(query["s1"]),  S2   = ParseMbps(query["s2"]),
                S3   = ParseMbps(query["s3"]),  S4   = ParseMbps(query["s4"]),
                H1   = query["h1"] ?? string.Empty, H2 = query["h2"] ?? string.Empty,
                H3   = query["h3"] ?? string.Empty, H4 = query["h4"] ?? string.Empty,
                I1   = query["i1"] ?? string.Empty, I2 = query["i2"] ?? string.Empty, I3 = query["i3"] ?? string.Empty,
                I4   = query["i4"] ?? string.Empty, I5 = query["i5"] ?? string.Empty,
            },
        };
    }

    /// <summary>
    /// Parse a URI query into a case-insensitive collection, decoding %XX but
    /// PRESERVING a literal '+'. Unlike <see cref="HttpUtility.ParseQueryString"/>
    /// (which treats '+' as a space), this keeps standard-base64 WireGuard/AWG
    /// keys (private_key / preshared_key) intact.
    /// </summary>
    private static System.Collections.Specialized.NameValueCollection ParseQueryPreservingPlus(string query)
    {
        var nv = new System.Collections.Specialized.NameValueCollection(StringComparer.OrdinalIgnoreCase);
        var q = (query ?? string.Empty).TrimStart('?');
        if (q.Length == 0) return nv;
        foreach (var pair in q.Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair.Substring(0, eq);
            var val = eq < 0 ? string.Empty : pair.Substring(eq + 1);
            nv[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(val);
        }
        return nv;
    }

    // ─── TUIC v5 ───────────────────────────────────────────────────────────
    //
    // Common community URI form (sing-box / NekoBox compatible):
    //   tuic://uuid:password@host:port?sni=...&congestion_control=bbr
    //                                  &udp_relay_mode=native&alpn=h3#name

    private static VlessServerEntry ParseTuic(string uri)
    {
        ShareLinkHelper.ParseComponents(
            uri.AsSpan("tuic://".Length),
            out var userinfoSpan,
            out var server,
            out var port,
            out var querySpan,
            out var name);

        var userinfo = ShareLinkHelper.Unescape(userinfoSpan);
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

        if (server.Length == 0)
            throw new FormatException("Invalid tuic URI: host missing");

        var query = ShareLinkHelper.ParseQuery(querySpan);

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
        ShareLinkHelper.ParseComponents(
            uri.AsSpan("ss://".Length),
            out var userinfoSpan,
            out var server,
            out var port,
            out var querySpan,
            out var name);

        if (userinfoSpan.IsEmpty)
            throw new FormatException("Invalid ss URI: userinfo missing");

        var userinfo = userinfoSpan.ToString();

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
            // base64 userinfo. Accept BOTH standard base64 and base64url
            // ("-"/"_") — SIP002 mandates base64url and our Clash-YAML emitter
            // (ClashYamlParser.MapShadowsocks) produces it, so a plain
            // Convert.FromBase64String would throw on "-"/"_" and silently drop
            // those servers. The Replace is a no-op for standard base64 (it has
            // no "-"/"_") so it's safe for both. Restore padding if missing
            // ("Shadowrocket"-style links sometimes drop trailing '=').
            var normalized = userinfo.Replace('-', '+').Replace('_', '/');
            var padded = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
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

        if (server.Length == 0)
            throw new FormatException("Invalid ss URI: host missing");

        var query = ShareLinkHelper.ParseQuery(querySpan);

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
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            throw new FormatException("Invalid naive URI: missing scheme separator");

        ShareLinkHelper.ParseComponents(
            uri.AsSpan(schemeEnd + 3),
            out var userinfoSpan,
            out var server,
            out var port,
            out var querySpan,
            out var name);

        var userinfo = ShareLinkHelper.Unescape(userinfoSpan);
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

        if (server.Length == 0)
            throw new FormatException("Invalid naive URI: host missing");

        var query = ShareLinkHelper.ParseQuery(querySpan);

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
