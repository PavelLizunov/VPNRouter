#nullable enable
using System.Globalization;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

internal static class SingBoxJsonSubscription
{
    private static readonly HashSet<string> StructuralTypes =
        new(StringComparer.OrdinalIgnoreCase) { "selector", "urltest", "direct", "block", "dns" };

    internal static bool LooksLikeSingBoxConfig(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("outbounds", out var outbounds)
        && outbounds.ValueKind == JsonValueKind.Array;

    internal static List<string> ParseOutboundsToUris(JsonElement root, ILogger? logger = null)
    {
        var uris = new List<string>();
        if (!LooksLikeSingBoxConfig(root)) return uris;

        var proxies = 0;
        foreach (var ob in root.GetProperty("outbounds").EnumerateArray())
        {
            if (ob.ValueKind != JsonValueKind.Object) continue;
            var type = Str(ob, "type")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(type) || StructuralTypes.Contains(type)) continue;

            proxies++;
            var tag = Str(ob, "tag");
            try
            {
                var uri = MapOutbound(ob, type, out var skipReason);
                if (uri is not null)
                    uris.Add(uri);
                else
                    logger?.Information("[SingBox] skipped outbound '{Tag}' ({Type}): {Reason}", tag, type, skipReason);
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[SingBox] skipped outbound '{Tag}' ({Type})", tag, type);
            }
        }

        logger?.Debug("[SingBox] mapped {Count}/{Total} proxy outbounds to share URIs", uris.Count, proxies);
        return uris;
    }

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
            "hysteria2" => MapHysteria2(ob, hostPort, name, out skipReason),
            "tuic" => MapTuic(ob, hostPort, name, out skipReason),
            "shadowsocks" => MapShadowsocks(ob, hostPort, name, out skipReason),
            _ => Unsupported(type, out skipReason),
        };
    }

    private static string? Unsupported(string type, out string? skipReason)
    {
        skipReason = $"protocol '{type}' is not supported";
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
                skipReason = $"unsupported transport '{transportType}'";
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
