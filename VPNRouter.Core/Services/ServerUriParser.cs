using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Web;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class ServerUriParser
{
    public static bool NaiveRuntimeAvailable { get; internal set; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static bool SlipstreamRuntimeAvailable { get; internal set; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static VlessServerEntry Parse(string uri)
    {
        uri = (uri ?? string.Empty).Trim();
        if (uri.Length == 0)
            throw new FormatException("Empty URI");

        if (IsWireGuardConf(uri))
        {
            if (!SingBoxFeatures.AwgAvailable)
                throw new FormatException(
                    "AmneziaWG / WireGuard requires a sing-box-vpnctl (with_awg) build. " +
                    "This build bundles upstream sing-box — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            return ParseWireGuardConf(uri);
        }

        if (uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return VlessUriParser.Parse(uri);

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
            if (!NaiveRuntimeAvailable)
                throw new FormatException(
                    "NaiveProxy is supported only on Windows and Linux (it needs sing-box's Cronet runtime). " +
                    "This platform can't use naive servers — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            entry = ParseNaive(uri);
        }
        else if (uri.StartsWith("dns-tunnel://", StringComparison.OrdinalIgnoreCase))
        {
            if (!SlipstreamRuntimeAvailable)
                throw new FormatException(
                    "DNS-tunnel servers are supported only on Windows and Linux (they need the slipstream-client sidecar). " +
                    "This platform can't use dns-tunnel servers — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            entry = ParseDnsTunnel(uri);
        }
        else if (uri.StartsWith("amneziawg://", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("amneziawg3://", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("awg://", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("awg3://", StringComparison.OrdinalIgnoreCase))
        {
            if (!SingBoxFeatures.AwgAvailable)
                throw new FormatException(
                    "AmneziaWG requires a sing-box-lx / sing-box-vpnctl (with_awg) build. This build bundles " +
                    "upstream sing-box — use a VLESS / Hysteria2 / TUIC / Shadowsocks server instead.");
            entry = ParseAmneziaWg(uri);
        }
        else
            throw new FormatException($"Unsupported URI scheme. Expected vless:// / hysteria2:// / hy2:// / tuic:// / ss:// / naive:// / dns-tunnel:// / awg:// / awg3://. Got: {Truncate(CanaryPolicy.RedactUrl(uri), 40)}");

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

    public static VlessServerEntry? TryParse(string uri)
    {
        try { return Parse(uri); }
        catch (PlaceholderConfigException) { return null; }
        catch (FormatException) { return null; }
        catch { return null; }
    }

    public static List<VlessServerEntry> ParseMultiple(string text)
    {
        var result = new List<VlessServerEntry>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        if (IsWireGuardConf(text))
        {
            if (SingBoxFeatures.AwgAvailable)
            {
                try { result.Add(ParseWireGuardConf(text)); } catch { }
            }
            return result;
        }

        foreach (var lineSpan in MemoryExtensions.EnumerateLines(text.AsSpan()))
        {
            var trimmedSpan = lineSpan.Trim();
            if (trimmedSpan.IsEmpty || !IsSupportedScheme(trimmedSpan)) continue;
            var trimmed = trimmedSpan.ToString();
            try { result.Add(Parse(trimmed)); } catch { }
        }
        return result;
    }

    public static bool IsSupportedScheme(string line) => IsSupportedScheme(line.AsSpan());

    public static bool IsSupportedScheme(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("naive://",       StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("naive+https://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("naive+quic://",  StringComparison.OrdinalIgnoreCase))
            return NaiveRuntimeAvailable;

        if (line.StartsWith("dns-tunnel://", StringComparison.OrdinalIgnoreCase))
            return SlipstreamRuntimeAvailable;

        if (line.StartsWith("amneziawg://",  StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("amneziawg3://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("awg://",        StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("awg3://",       StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("[Interface]",   StringComparison.OrdinalIgnoreCase))
            return SingBoxFeatures.AwgAvailable;

        return line.StartsWith("vless://",     StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("hy2://",       StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("tuic://",      StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("ss://",        StringComparison.OrdinalIgnoreCase);
    }

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
                    if (IsSystemResolverSentinel(t)) { useSystemResolver = true; continue; }
                    resolvers.Add(t);
                }
                if (useSystemResolver || resolvers.Count > 0) break;
            }

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
                if (authoritative.Count > 0) break;
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

        cert = cert.Trim();
        if (cert.Length == 0)
            throw new FormatException("dns-tunnel: missing 'cert' (server leaf PEM)");
        if (!cert.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            throw new FormatException("dns-tunnel: 'cert' is not a PEM certificate (no BEGIN CERTIFICATE marker)");

        return new VlessServerEntry
        {
            Protocol = "dns-tunnel",
            Name = string.IsNullOrWhiteSpace(fragName) ? domain : fragName!,
            Server = domain,
            DnsDomain = domain,
            DnsResolvers = resolvers,
            DnsUseSystemResolver = useSystemResolver,
            DnsAuthoritative = authoritative,
            DnsLeafCertPem = cert,
            DnsLeafFingerprint = fingerprint,
            Uuid = uuid,
        };
    }

    private static bool IsSystemResolverSentinel(string s) =>
        s.Equals("system", StringComparison.OrdinalIgnoreCase)
        || s.Equals("auto", StringComparison.OrdinalIgnoreCase)
        || s.Equals("os", StringComparison.OrdinalIgnoreCase)
        || s.Equals("device", StringComparison.OrdinalIgnoreCase);

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

    private static VlessServerEntry ParseHysteria2(string uri)
    {
        var normalized = uri;
        if (normalized.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            normalized = "hysteria2://" + normalized.Substring("hy2://".Length);
        var fake = "https://" + normalized.Substring("hysteria2://".Length);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid hysteria2 URI: cannot parse");

        var password = Uri.UnescapeDataString(parsed.UserInfo);
        if (password.Length == 0)
            throw new FormatException("Invalid hysteria2 URI: password missing (expected hysteria2://password@host:port)");

        var server = VlessUriParser.NormalizeHost(parsed.Host);
        var port = (parsed.Port > 0 && parsed.Port <= 65535) ? parsed.Port : 443;
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
            PairGroup = query["pair"] ?? string.Empty,
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? server,
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

        entry.HysteriaUpMbps = ParseMbps(query["up"] ?? query["upmbps"] ?? query["up_mbps"]);
        entry.HysteriaDownMbps = ParseMbps(query["down"] ?? query["downmbps"] ?? query["down_mbps"]);

        return entry;
    }

    private static int ParseMbps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var digits = new string(raw.TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) && v > 0 ? v : 0;
    }

    private static VlessServerEntry ParseAmneziaWg(string uri)
    {
        var rest = uri.Trim();
        if (rest.StartsWith("awg3://", StringComparison.OrdinalIgnoreCase))
            rest = rest.Substring("awg3://".Length);
        else if (rest.StartsWith("awg://", StringComparison.OrdinalIgnoreCase))
            rest = rest.Substring("awg://".Length);
        else if (rest.StartsWith("amneziawg3://", StringComparison.OrdinalIgnoreCase))
            rest = rest.Substring("amneziawg3://".Length);
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

        var query = ParseQueryPreservingPlus(rawQuery);
        var name = Uri.UnescapeDataString(fragment);
        var addr = query["address"] ?? query["addr"] ?? string.Empty;
        var privateKey = query["private_key"] ?? query["pk"] ?? string.Empty;
        if (string.IsNullOrEmpty(privateKey))
            throw new FormatException("Invalid amneziawg URI: private_key is required");
        if (string.IsNullOrWhiteSpace(addr))
            throw new FormatException("Invalid amneziawg URI: address is required (e.g. address=10.13.13.2/32)");

        static bool ParseBoolParam(string? s) =>
            !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase));

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
                HeaderProtectionKey    = query["header_protection_key"] ?? query["hpk"] ?? query["headerprotectionkey"] ?? string.Empty,
                ContentPaddingAddition = query["content_padding_addition"] ?? query["cpa"] ?? query["contentpaddingaddition"] ?? string.Empty,
                RandomTrailers         = ParseBoolParam(query["random_trailers"] ?? query["rt"] ?? query["randomtrailers"]),
                DisableCookies         = ParseBoolParam(query["disable_cookies"] ?? query["dc"] ?? query["disablecookies"]),
            },
        };
    }

    public static bool IsWireGuardConf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.AsSpan().Trim();
        return span.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
               span.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);
    }

    public static VlessServerEntry ParseWireGuardConf(string text, string? defaultName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Invalid WireGuard/AmneziaWG config: text is empty");

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string currentSection = string.Empty;
        var ifaceDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();

            if (currentSection.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                ifaceDict[key] = val;
            else if (currentSection.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                peerDict[key] = val;
        }

        if (!ifaceDict.TryGetValue("PrivateKey", out var privateKey) || string.IsNullOrEmpty(privateKey))
            throw new FormatException("Invalid WireGuard/AmneziaWG config: PrivateKey is required in [Interface]");

        if (!peerDict.TryGetValue("PublicKey", out var publicKey) || string.IsNullOrEmpty(publicKey))
            throw new FormatException("Invalid WireGuard/AmneziaWG config: PublicKey is required in [Peer]");

        if (!peerDict.TryGetValue("Endpoint", out var endpoint) || string.IsNullOrEmpty(endpoint))
            throw new FormatException("Invalid WireGuard/AmneziaWG config: Endpoint is required in [Peer]");

        string server;
        var port = 51820;
        if (endpoint.StartsWith("["))
        {
            var close = endpoint.IndexOf(']');
            server = close > 0 ? endpoint.Substring(1, close - 1) : endpoint;
            var after = close >= 0 ? endpoint.Substring(close + 1) : string.Empty;
            if (after.StartsWith(":") && int.TryParse(after.Substring(1), out var p6) && p6 > 0) port = p6;
        }
        else
        {
            var colonIdx = endpoint.LastIndexOf(':');
            if (colonIdx >= 0)
            {
                server = endpoint.Substring(0, colonIdx);
                if (int.TryParse(endpoint.Substring(colonIdx + 1), out var p) && p > 0) port = p;
            }
            else server = endpoint;
        }

        if (string.IsNullOrEmpty(server))
            throw new FormatException("Invalid WireGuard/AmneziaWG config: Endpoint host is missing");

        ifaceDict.TryGetValue("Address", out var addrStr);
        peerDict.TryGetValue("PresharedKey", out var psk);
        peerDict.TryGetValue("PersistentKeepalive", out var keepaliveStr);

        static bool ParseBool(string? s) =>
            !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase));

        static string Val(Dictionary<string, string> d, string k) =>
            d.TryGetValue(k, out var v) ? v : string.Empty;

        var awg = new AwgConfig
        {
            PrivateKey    = privateKey,
            PeerPublicKey = publicKey,
            PresharedKey  = psk ?? string.Empty,
            Address       = string.IsNullOrWhiteSpace(addrStr) ? new List<string> { "10.13.13.2/32" }
                            : addrStr.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
            Keepalive     = ParseMbps(keepaliveStr),
            Jc   = ParseMbps(Val(ifaceDict, "Jc")),
            Jmin = ParseMbps(Val(ifaceDict, "Jmin")),
            Jmax = ParseMbps(Val(ifaceDict, "Jmax")),
            S1   = ParseMbps(Val(ifaceDict, "S1")),
            S2   = ParseMbps(Val(ifaceDict, "S2")),
            S3   = ParseMbps(Val(ifaceDict, "S3")),
            S4   = ParseMbps(Val(ifaceDict, "S4")),
            H1   = Val(ifaceDict, "H1"),
            H2   = Val(ifaceDict, "H2"),
            H3   = Val(ifaceDict, "H3"),
            H4   = Val(ifaceDict, "H4"),
            I1   = Val(ifaceDict, "I1"),
            I2   = Val(ifaceDict, "I2"),
            I3   = Val(ifaceDict, "I3"),
            I4   = Val(ifaceDict, "I4"),
            I5   = Val(ifaceDict, "I5"),
            HeaderProtectionKey    = Val(ifaceDict, "HeaderProtectionKey"),
            ContentPaddingAddition = Val(ifaceDict, "ContentPaddingAddition"),
            RandomTrailers         = ParseBool(Val(ifaceDict, "RandomTrailers")),
            DisableCookies         = ParseBool(Val(ifaceDict, "DisableCookies")),
        };

        var name = !string.IsNullOrEmpty(defaultName) ? defaultName : $"awg-{server}-{port}";

        return new VlessServerEntry
        {
            Name = name,
            Protocol = "amneziawg",
            Server = server,
            Port = port,
            Awg = awg,
        };
    }

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

    private static VlessServerEntry ParseTuic(string uri)
    {
        var fake = "https://" + uri.Substring("tuic://".Length);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid tuic URI: cannot parse");

        var userinfo = Uri.UnescapeDataString(parsed.UserInfo);
        if (userinfo.Length == 0)
            throw new FormatException("Invalid tuic URI: uuid:password missing");

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

        var server = VlessUriParser.NormalizeHost(parsed.Host);
        var port = (parsed.Port > 0 && parsed.Port <= 65535) ? parsed.Port : 443;
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
                Insecure = query["insecure"] == "1"
                        || query["allowInsecure"] == "1"
                        || string.Equals(query["allow_insecure"], "true",
                                         StringComparison.OrdinalIgnoreCase),
                Alpn = query["alpn"] ?? "h3",
            },
        };
    }

    private static VlessServerEntry ParseShadowsocks(string uri)
    {
        var fake = "https://" + uri.Substring("ss://".Length);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid ss URI: cannot parse");

        var userinfo = parsed.UserInfo;
        if (userinfo.Length == 0)
            throw new FormatException("Invalid ss URI: userinfo missing");

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

        var server = VlessUriParser.NormalizeHost(parsed.Host);
        var port = (parsed.Port > 0 && parsed.Port <= 65535) ? parsed.Port : 443;
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

        var plugin = query["plugin"];
        if (!string.IsNullOrEmpty(plugin))
        {
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

    private static VlessServerEntry ParseNaive(string uri)
    {
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            throw new FormatException("Invalid naive URI: missing scheme separator");
        var fake = "https://" + uri.Substring(schemeEnd + 3);

        if (!Uri.TryCreate(fake, UriKind.Absolute, out var parsed))
            throw new FormatException("Invalid naive URI: cannot parse");

        var userinfo = Uri.UnescapeDataString(parsed.UserInfo);
        if (userinfo.Length == 0)
            throw new FormatException("Invalid naive URI: user:password missing (expected naive+https://user:pass@host:port)");

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

        var server = VlessUriParser.NormalizeHost(parsed.Host);
        var port = (parsed.Port > 0 && parsed.Port <= 65535) ? parsed.Port : 443;
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
            PairGroup = query["pair"] ?? string.Empty,
            NaiveQuic = uri.StartsWith("naive+quic://", StringComparison.OrdinalIgnoreCase),
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? server,
            },
        };
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }
}
