#nullable enable
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal static class SingBoxJsonSubscription
{
    private static readonly HashSet<string> StructuralTypes =
        new(StringComparer.OrdinalIgnoreCase) { "selector", "urltest", "direct", "block", "dns" };

    internal static bool LooksLikeSingBoxConfig(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("outbounds", out var outbounds)
        && outbounds.ValueKind == JsonValueKind.Array;

    internal static List<string> ParseOutboundsToUris(JsonElement root, ILogger? logger = null) =>
        ParseOutboundsToUris(root, out _, logger);

    internal static List<string> ParseOutboundsToUris(JsonElement root, out bool skippedChains, ILogger? logger = null)
    {
        skippedChains = false;
        if (!LooksLikeSingBoxConfig(root)) return new List<string>();

        var rows = new NativeSubscriptionRows();
        // Endpoints share the tag namespace with outbounds.
        if (root.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var endpoint in endpoints.EnumerateArray())
                rows.Add(Str(endpoint, "tag"), null, false, null);
        }

        var proxies = 0;
        foreach (var ob in root.GetProperty("outbounds").EnumerateArray())
        {
            if (ob.ValueKind != JsonValueKind.Object) continue;
            string? tag = null;
            string? via = null;
            string? uri = null;
            bool canChain = false;
            try
            {
                tag = Str(ob, "tag");
                var type = Str(ob, "type")?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(type) && !StructuralTypes.Contains(type))
                {
                    proxies++;
                    via = ReadDetour(ob);
                    uri = MapOutbound(ob, type, out var skipReason);
                    if (uri is null)
                        logger?.Information("[SingBox] Skipped outbound ({Reason})", skipReason);
                    else
                        canChain = type switch
                        {
                            "vless" => CanChainVless(ob),
                            "vmess" => IsMappableVmess(ob),
                            "turnrelay" => CanChainTurnRelay(ob),
                            _ => false,
                        };
                }
            }
            catch (Exception)
            {
                uri = null;
                logger?.Warning("[SingBox] Skipped invalid outbound");
            }
            rows.Add(tag, via, canChain, uri);
        }

        var uris = rows.ToShareUris(out skippedChains);
        logger?.Debug("[SingBox] mapped {Count}/{Total} proxy outbounds to share URIs", uris.Count, proxies);
        return uris;
    }

    private static string? ReadDetour(JsonElement ob)
    {
        string? tag = null;
        var seen = false;
        foreach (var field in ob.EnumerateObject())
        {
            if (!field.Name.Equals("detour", StringComparison.OrdinalIgnoreCase)) continue;
            // A non-null empty dependency rejects an ambiguous or malformed declaration.
            if (seen || !field.NameEquals("detour")) return string.Empty;
            seen = true;
            if (field.Value.ValueKind == JsonValueKind.Null) continue;
            if (field.Value.ValueKind != JsonValueKind.String) return string.Empty;
            tag = field.Value.GetString();
        }
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    // Only link hops whose explicit dial, TLS and transport options survive the VLESS share-link mapping.
    private static bool CanChainVless(JsonElement ob) =>
        NativeSubscriptionRows.IsValidHost(Str(ob, "server")) && ob.EnumerateObject().All(p => p.Name switch
        {
            "type" or "tag" or "server" or "server_port" or "uuid" or "detour" => true,
            "flow" => p.Value.ValueKind == JsonValueKind.String,
            "packet_encoding" => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "xudp",
            "tcp_keep_alive" or "tcp_keep_alive_interval" =>
                p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "30s",
            "tls" => CanChainTls(p.Value),
            "transport" => CanChainTransport(p.Value),
            _ => false,
        });

    private static bool CanChainTransport(JsonElement transport)
    {
        if (transport.ValueKind == JsonValueKind.Null) return true;
        if (transport.ValueKind != JsonValueKind.Object) return false;
        return Str(transport, "type") switch
        {
            null or "" or "tcp" => transport.EnumerateObject().All(p =>
                p.Name == "type" && p.Value.ValueKind == JsonValueKind.String),
            "grpc" => transport.EnumerateObject().All(p =>
                (p.Name is "type" or "service_name") && p.Value.ValueKind == JsonValueKind.String),
            "ws" => transport.EnumerateObject().All(p => p.Name == "headers"
                ? CanChainWsHeaders(p.Value)
                : (p.Name is "type" or "path") && p.Value.ValueKind == JsonValueKind.String),
            _ => false,
        };
    }

    private static bool CanChainWsHeaders(JsonElement headers) =>
        headers.ValueKind == JsonValueKind.Object && headers.EnumerateObject().Count() <= 1 &&
        headers.EnumerateObject().All(p => p.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
            (p.Value.ValueKind == JsonValueKind.String || (p.Value.ValueKind == JsonValueKind.Array &&
                p.Value.GetArrayLength() == 1 && p.Value[0].ValueKind == JsonValueKind.String)));

    private static bool CanChainTls(JsonElement tls)
    {
        if (tls.ValueKind == JsonValueKind.Null) return true;
        if (tls.ValueKind != JsonValueKind.Object) return false;
        var reality = Bool(Obj(tls, "reality") ?? default, "enabled");
        if (reality && (!Bool(tls, "enabled") || !Bool(Obj(tls, "utls") ?? default, "enabled")))
            return false;
        return tls.EnumerateObject().All(p => p.Name switch
        {
            "enabled" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "server_name" => p.Value.ValueKind == JsonValueKind.String,
            "insecure" => p.Value.ValueKind == JsonValueKind.False ||
                (!reality && p.Value.ValueKind == JsonValueKind.True),
            "alpn" => !reality && (CanMapAlpn(p.Value) ||
                (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().All(CanMapAlpn))),
            "reality" => p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject()
                .All(field => field.Name switch
                {
                    "enabled" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "public_key" => field.Value.ValueKind == JsonValueKind.String,
                    "short_id" => field.Value.ValueKind == JsonValueKind.String &&
                        VlessUriParser.IsValidRealityShortId(field.Value.GetString()),
                    _ => false,
                }),
            "utls" => p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject().All(field =>
                (field.Name == "fingerprint" && field.Value.ValueKind == JsonValueKind.String) ||
                (field.Name == "enabled" && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)) &&
                Bool(p.Value, "enabled") == !string.IsNullOrEmpty(Str(p.Value, "fingerprint")),
            _ => false,
        });
    }

    private static bool CanMapAlpn(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s &&
        !s.Contains(',') && s.Trim() == s;

    private static string? MapOutbound(JsonElement ob, string type, out string? skipReason)
    {
        skipReason = null;

        var server = Str(ob, "server");
        if (string.IsNullOrWhiteSpace(server)) { skipReason = "missing server"; return null; }

        if (Int(ob, "server_port") is not { } port || port < 1 || port > 65535)
        {
            skipReason = "missing or invalid server_port";
            return null;
        }

        var name = Str(ob, "tag");
        if (string.IsNullOrWhiteSpace(name)) name = server;

        var host = server.Contains(':') && !server.StartsWith('[') ? $"[{server}]" : server;
        var hostPort = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";

        return type switch
        {
            "vless" => MapVless(ob, hostPort, name, out skipReason),
            "vmess" => MapVmess(ob, server, port, name, out skipReason),
            "hysteria2" => MapHysteria2(ob, hostPort, name, out skipReason),
            "tuic" => MapTuic(ob, hostPort, name, out skipReason),
            "shadowsocks" => MapShadowsocks(ob, hostPort, name, out skipReason),
            "turnrelay" => MapTurnRelay(ob, server, port, name, out skipReason),
            _ => Unsupported(out skipReason),
        };
    }

    private static string? Unsupported(out string? skipReason)
    {
        skipReason = "unsupported protocol";
        return null;
    }

    private static string? MapVless(JsonElement ob, string hostPort, string name, out string? skipReason)
    {
        skipReason = null;
        var uuid = Str(ob, "uuid");
        if (string.IsNullOrEmpty(uuid)) { skipReason = "missing uuid"; return null; }

        var q = new List<(string Key, string? Val)>();

        var transport = Obj(ob, "transport") ?? default;
        var transportType = Str(transport, "type")?.ToLowerInvariant();
        switch (transportType)
        {
            case null:
            case "":
            case "tcp":
                q.Add(("type", "tcp"));
                break;
            case "ws":
                q.Add(("type", "ws"));
                q.Add(("path", Str(transport, "path")));
                q.Add(("host", HeaderValue(Obj(transport, "headers"), "Host")));
                break;
            case "grpc":
                q.Add(("type", "grpc"));
                q.Add(("serviceName", Str(transport, "service_name")));
                break;
            case "xhttp":
                q.Add(("type", "xhttp"));
                q.Add(("mode", Str(transport, "mode")));
                q.Add(("path", Str(transport, "path")));
                q.Add(("host", Str(transport, "host") ?? HeaderValue(Obj(transport, "headers"), "Host")));
                q.Add(("x_padding_bytes", Str(transport, "x_padding_bytes")));
                if (Bool(transport, "no_grpc_header")) q.Add(("no_grpc_header", "1"));
                break;
            default:
                skipReason = "unsupported transport";
                return null;
        }

        var tls = Obj(ob, "tls") ?? default;
        var reality = Obj(tls, "reality") ?? default;
        var fingerprint = Str(Obj(tls, "utls") ?? default, "fingerprint");

        if (Bool(reality, "enabled"))
        {
            q.Add(("security", "reality"));
            q.Add(("sni", Str(tls, "server_name")));
            q.Add(("fp", fingerprint));
            q.Add(("pbk", Str(reality, "public_key")));
            q.Add(("sid", Str(reality, "short_id")));
        }
        else if (Bool(tls, "enabled"))
        {
            q.Add(("security", "tls"));
            q.Add(("sni", Str(tls, "server_name")));
            q.Add(("fp", fingerprint));
            q.Add(("alpn", StrList(tls, "alpn")));
            if (Bool(tls, "insecure")) q.Add(("allowInsecure", "1"));
        }
        else
        {
            q.Add(("security", "none"));
        }

        q.Add(("flow", Str(ob, "flow")));

        return $"vless://{Enc(uuid)}@{hostPort}{Query(q)}#{Enc(name)}";
    }

    private static string? MapVmess(
        JsonElement ob, string server, int port, string name, out string? skipReason)
    {
        skipReason = null;
        if (!IsMappableVmess(ob))
        {
            skipReason = "unsupported VMess option";
            return null;
        }

        var uuid = Str(ob, "uuid");
        if (!Guid.TryParse(uuid, out _)) { skipReason = "invalid uuid"; return null; }
        if (!VmessUriParser.TryNormalizeAeadSecurity(Str(ob, "security"), out var security))
        {
            skipReason = "non-AEAD VMess security";
            return null;
        }

        var transport = Obj(ob, "transport") ?? default;
        var transportType = Str(transport, "type")?.ToLowerInvariant();
        var net = "tcp";
        var path = string.Empty;
        var host = string.Empty;
        if (transportType is "ws")
        {
            net = "ws";
            path = Str(transport, "path") ?? "/";
            host = HeaderValue(Obj(transport, "headers"), "Host") ?? string.Empty;
        }

        var tls = Obj(ob, "tls") ?? default;
        var tlsEnabled = Bool(tls, "enabled");
        var sni = Str(tls, "server_name") ?? string.Empty;
        if (string.IsNullOrEmpty(host) && tlsEnabled) host = sni;

        var entry = new VlessServerEntry
        {
            Name = name,
            Protocol = "vmess",
            Server = server,
            Port = port,
            Uuid = uuid!,
            Security = tlsEnabled ? "tls" : "none",
            VmessSecurity = security,
            AlterId = 0,
            Tls = new VlessTlsConfig
            {
                Enabled = tlsEnabled,
                ServerName = tlsEnabled
                    ? (string.IsNullOrEmpty(sni) ? server : sni)
                    : string.Empty,
                Insecure = tlsEnabled && Bool(tls, "insecure"),
                Fingerprint = Str(Obj(tls, "utls") ?? default, "fingerprint") ?? string.Empty,
                Alpn = StrList(tls, "alpn") ?? string.Empty,
            },
            Transport = new VlessTransportConfig
            {
                Type = net,
                Path = net == "ws" ? (string.IsNullOrEmpty(path) ? "/" : path) : string.Empty,
                Headers = net == "ws" && !string.IsNullOrEmpty(host)
                    ? new Dictionary<string, string> { ["Host"] = host }
                    : new Dictionary<string, string>(),
            },
        };

        return VmessUriParser.BuildShareUri(entry);
    }

    private static bool IsMappableVmess(JsonElement ob) =>
        ob.EnumerateObject().All(p => p.Name switch
        {
            "type" or "tag" or "server" or "server_port" or "uuid" or "detour" => true,
            "security" => p.Value.ValueKind == JsonValueKind.String &&
                VmessUriParser.TryNormalizeAeadSecurity(p.Value.GetString(), out _),
            "alter_id" => IsZero(p.Value),
            "global_padding" => p.Value.ValueKind == JsonValueKind.False,
            "authenticated_length" => p.Value.ValueKind == JsonValueKind.False,
            "domain_resolver" => p.Value.ValueKind == JsonValueKind.String &&
                p.Value.GetString() == "local-dns",
            "packet_encoding" => p.Value.ValueKind == JsonValueKind.String &&
                string.IsNullOrEmpty(p.Value.GetString()),
            "tcp_keep_alive" or "tcp_keep_alive_interval" =>
                p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "30s",
            "tls" => CanMapVmessTls(p.Value),
            "transport" => CanMapVmessTransport(p.Value),
            _ => false,
        });

    private static bool IsZero(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n == 0;
        return value.ValueKind == JsonValueKind.String && value.GetString() == "0";
    }

    private static bool CanMapVmessTls(JsonElement tls)
    {
        if (tls.ValueKind == JsonValueKind.Null) return true;
        if (tls.ValueKind != JsonValueKind.Object) return false;
        return tls.EnumerateObject().All(p => p.Name switch
        {
            "enabled" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "server_name" => p.Value.ValueKind == JsonValueKind.String,
            "insecure" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "alpn" => p.Value.ValueKind == JsonValueKind.String ||
                (p.Value.ValueKind == JsonValueKind.Array &&
                    p.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)),
            "utls" => p.Value.ValueKind == JsonValueKind.Object &&
                p.Value.EnumerateObject().All(field =>
                    (field.Name == "fingerprint" && field.Value.ValueKind == JsonValueKind.String) ||
                    (field.Name == "enabled" && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)) &&
                Bool(p.Value, "enabled") == !string.IsNullOrEmpty(Str(p.Value, "fingerprint")),
            _ => false,
        });
    }

    private static bool CanMapVmessTransport(JsonElement transport)
    {
        if (transport.ValueKind == JsonValueKind.Null) return true;
        if (transport.ValueKind != JsonValueKind.Object) return false;
        return Str(transport, "type")?.ToLowerInvariant() switch
        {
            null or "" or "tcp" => transport.EnumerateObject().All(p =>
                p.Name == "type" && p.Value.ValueKind == JsonValueKind.String),
            "ws" => transport.EnumerateObject().All(p => p.Name switch
            {
                "type" or "path" => p.Value.ValueKind == JsonValueKind.String,
                "headers" => CanChainWsHeaders(p.Value),
                "max_early_data" => IsZero(p.Value),
                _ => false,
            }),
            _ => false,
        };
    }

    // Only a complete turnrelay document that survives the URI round trip may
    // become a chain upstream. Unknown fields reject the hop instead of silently
    // dropping dialer, TLS or credential options.
    private static bool CanChainTurnRelay(JsonElement ob)
    {
        if (!TurnRelayConfig.IsLiteralIp(Str(ob, "server")) ||
            Int(ob, "server_port") is not { } port || port is < 1 or > 65535 ||
            string.IsNullOrEmpty(Str(ob, "password")) ||
            string.IsNullOrEmpty(Str(ob, "turn_username")) ||
            string.IsNullOrEmpty(Str(ob, "turn_password")) ||
            !TurnRelayUriParserRules.IsValidHostPort(Str(ob, "turn_server")) ||
            Str(ob, "server_fingerprint") is not { } fingerprint ||
            !TurnRelayConfig.IsFingerprint(fingerprint))
            return false;

        var mode = Str(ob, "mode");
        if (!string.IsNullOrEmpty(mode) && mode is not ("srtp" or "dtls")) return false;
        var provider = Str(ob, "provider");
        if (!string.IsNullOrEmpty(provider) && provider != TurnRelayConfig.DefaultProvider) return false;
        var serverType = Str(ob, "server_type");
        if (!string.IsNullOrEmpty(serverType) && serverType != TurnRelayConfig.DefaultServerType) return false;
        if (Int(ob, "connections") is { } connections && (connections < 0 || connections > 60)) return false;

        return ob.EnumerateObject().All(property => property.Name switch
        {
            "type" or "tag" or "server" or "turn_server" or "turn_username" or
                "turn_password" or "password" or "server_fingerprint" or "provider" or
                "server_type" or "mode" => property.Value.ValueKind == JsonValueKind.String,
            "server_port" or "connections" => property.Value.ValueKind == JsonValueKind.Number,
            "udp" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "domain_resolver" => CanMapTurnRelayResolver(property.Value),
            _ => false,
        });
    }

    private static bool CanMapTurnRelayResolver(JsonElement resolver) =>
        (resolver.ValueKind == JsonValueKind.String && resolver.GetString() == "local-dns") ||
        (resolver.ValueKind == JsonValueKind.Object &&
            resolver.EnumerateObject().All(property => property.Name switch
            {
                "server" => property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() == "local-dns",
                "strategy" => property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() == "prefer_ipv4",
                _ => false,
            }));

    private static string? MapTurnRelay(
        JsonElement ob, string server, int port, string name, out string? skipReason)
    {
        skipReason = null;
        if (!TurnRelayUriParser.IsValidId(Str(ob, "tag")))
        {
            skipReason = "turnrelay tag is not a valid outbound id";
            return null;
        }
        if (!CanChainTurnRelay(ob))
        {
            skipReason = "unsupported turnrelay option";
            return null;
        }
        if (!TurnRelayConfig.IsLiteralIp(server))
        {
            skipReason = "turnrelay server is not an IP literal";
            return null;
        }

        var connections = Int(ob, "connections") ?? 0;
        if (connections == 0) connections = TurnRelayConfig.DefaultConnections;
        var mode = Str(ob, "mode");
        if (string.IsNullOrEmpty(mode)) mode = TurnRelayConfig.DefaultMode;
        var provider = Str(ob, "provider");
        if (string.IsNullOrEmpty(provider)) provider = TurnRelayConfig.DefaultProvider;
        var serverType = Str(ob, "server_type");
        if (string.IsNullOrEmpty(serverType)) serverType = TurnRelayConfig.DefaultServerType;

        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteString("id", Str(ob, "tag"));
            writer.WriteString("provider", provider);
            writer.WriteString("server_type", serverType);
            writer.WriteString("server", server);
            writer.WriteNumber("server_port", port);
            writer.WriteString("turn_server", Str(ob, "turn_server"));
            writer.WriteString("turn_username", Str(ob, "turn_username"));
            writer.WriteString("turn_password", Str(ob, "turn_password"));
            writer.WriteString("password", Str(ob, "password"));
            writer.WriteString("server_fingerprint", Str(ob, "server_fingerprint")!.ToLowerInvariant());
            writer.WriteNumber("connections", connections);
            writer.WriteString("mode", mode);
            writer.WriteBoolean("udp", !ob.TryGetProperty("udp", out var udpNode) || udpNode.GetBoolean());
            writer.WriteEndObject();
            writer.Flush();
            return $"{TurnRelayUriParser.Prefix}{TurnRelayUriParser.Base64UrlEncode(buffer.WrittenSpan)}#{Enc(name)}";
        }
        finally
        {
            buffer.Clear();
        }
    }

    private static string? MapHysteria2(JsonElement ob, string hostPort, string name, out string? skipReason)
    {
        skipReason = null;
        var password = Str(ob, "password");
        if (string.IsNullOrEmpty(password)) { skipReason = "missing password"; return null; }

        var tls = Obj(ob, "tls") ?? default;
        var obfs = Obj(ob, "obfs") ?? default;
        var q = new List<(string Key, string? Val)>
        {
            ("sni", Str(tls, "server_name")),
            ("obfs", Str(obfs, "type")),
            ("obfs-password", Str(obfs, "password")),
        };
        if (Bool(tls, "insecure")) q.Add(("insecure", "1"));
        var up = Int(ob, "up_mbps");
        if (up is > 0) q.Add(("up", up.Value.ToString(CultureInfo.InvariantCulture)));
        var down = Int(ob, "down_mbps");
        if (down is > 0) q.Add(("down", down.Value.ToString(CultureInfo.InvariantCulture)));

        return $"hysteria2://{Enc(password)}@{hostPort}{Query(q)}#{Enc(name)}";
    }

    private static string? MapTuic(JsonElement ob, string hostPort, string name, out string? skipReason)
    {
        skipReason = null;
        var uuid = Str(ob, "uuid");
        if (string.IsNullOrEmpty(uuid)) { skipReason = "missing uuid"; return null; }

        var tls = Obj(ob, "tls") ?? default;
        var q = new List<(string Key, string? Val)>
        {
            ("sni", Str(tls, "server_name")),
            ("congestion_control", Str(ob, "congestion_control")),
            ("udp_relay_mode", Str(ob, "udp_relay_mode")),
            ("alpn", StrList(tls, "alpn")),
        };
        if (Bool(tls, "insecure")) q.Add(("insecure", "1"));

        return $"tuic://{Enc(uuid)}:{Enc(Str(ob, "password") ?? string.Empty)}@{hostPort}{Query(q)}#{Enc(name)}";
    }

    private static string? MapShadowsocks(JsonElement ob, string hostPort, string name, out string? skipReason)
    {
        skipReason = null;
        var method = Str(ob, "method");
        var password = Str(ob, "password");
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(password))
        {
            skipReason = "missing method or password";
            return null;
        }

        var plugin = Str(ob, "plugin");
        var pluginOpts = Str(ob, "plugin_opts");
        var q = new List<(string Key, string? Val)>();
        if (!string.IsNullOrEmpty(plugin))
            q.Add(("plugin", string.IsNullOrEmpty(pluginOpts) ? plugin : $"{plugin};{pluginOpts}"));

        return $"ss://{Enc(method)}:{Enc(password)}@{hostPort}{Query(q)}#{Enc(name)}";
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement? Obj(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    private static bool Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static string? StrList(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind != JsonValueKind.Array) return null;
        var items = v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrEmpty(s));
        var joined = string.Join(",", items);
        return joined.Length == 0 ? null : joined;
    }

    private static string? HeaderValue(JsonElement? headers, string name)
    {
        if (headers is not { ValueKind: JsonValueKind.Object } h) return null;
        foreach (var p in h.EnumerateObject())
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in p.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
                        return item.GetString();
            }
        }
        return null;
    }

    private static string Enc(string s) => Uri.EscapeDataString(s);

    private static string Query(List<(string Key, string? Val)> parts)
    {
        var joined = string.Join("&", parts
            .Where(kv => !string.IsNullOrEmpty(kv.Val))
            .Select(kv => $"{kv.Key}={Enc(kv.Val!)}"));
        return joined.Length == 0 ? string.Empty : "?" + joined;
    }
}
