#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Serilog;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Services;

internal static class ClashYamlParser
{
    internal static bool LooksLikeClashYaml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("proxies:", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal static List<string> ParseProxiesToUris(string body, ILogger? logger = null)
    {
        var uris = new List<string>();

        const int MaxBodyChars = 8 * 1024 * 1024;
        if (body.Length > MaxBodyChars)
        {
            logger?.Warning("[Clash] YAML body too large ({Len} chars > {Max}) — skipping parse",
                body.Length, MaxBodyChars);
            return uris;
        }

        Dictionary<string, object>? root;
        try
        {
            var de = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            root = de.Deserialize<Dictionary<string, object>>(body);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[Clash] YAML deserialize failed");
            return uris;
        }

        if (root is null || !root.TryGetValue("proxies", out var proxiesObj)
            || proxiesObj is not IEnumerable<object> proxies)
        {
            return uris;
        }

        foreach (var p in proxies)
        {
            if (ToStringMap(p) is not { } proxy) continue;
            try
            {
                var uri = MapProxyToUri(proxy);
                if (!string.IsNullOrEmpty(uri)) uris.Add(uri!);
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[Clash] skipped a proxy entry: {Name}", Str(proxy, "name"));
            }
        }

        logger?.Debug("[Clash] mapped {Count}/{Total} proxies to share URIs", uris.Count, proxies.Count());
        return uris;
    }

    private static string? MapProxyToUri(Dictionary<string, object> p)
    {
        var type = Str(p, "type")?.ToLowerInvariant();
        var server = Str(p, "server");
        var port = Str(p, "port");
        var name = Str(p, "name") ?? server ?? "node";
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(port)) return null;

        return type switch
        {
            "vless" => MapVless(p, server!, port!, name),
            "hysteria2" or "hy2" => MapHysteria2(p, server!, port!, name),
            "tuic" => MapTuic(p, server!, port!, name),
            "ss" or "shadowsocks" => MapShadowsocks(p, server!, port!, name),
            _ => null,
        };
    }

    private static string MapVless(Dictionary<string, object> p, string server, string port, string name)
    {
        var uuid = Str(p, "uuid") ?? "";
        var q = new List<(string, string?)>();

        var network = Str(p, "network") ?? "tcp";
        q.Add(("type", network));

        var reality = SubMap(p, "reality-opts");
        bool tls = Bool(p, "tls");
        if (reality is not null)
        {
            q.Add(("security", "reality"));
            q.Add(("pbk", Str(reality, "public-key")));
            q.Add(("sid", Str(reality, "short-id")));
        }
        else
        {
            q.Add(("security", tls ? "tls" : "none"));
        }

        q.Add(("sni", Str(p, "servername") ?? Str(p, "sni")));
        q.Add(("flow", Str(p, "flow")));
        q.Add(("fp", Str(p, "client-fingerprint")));
        var alpn = StrList(p, "alpn");
        if (alpn is not null) q.Add(("alpn", alpn));

        if (network == "ws")
        {
            var ws = SubMap(p, "ws-opts");
            if (ws is not null)
            {
                q.Add(("path", Str(ws, "path")));
                var headers = SubMap(ws, "headers");
                if (headers is not null) q.Add(("host", Str(headers, "Host") ?? Str(headers, "host")));
            }
        }
        else if (network == "grpc")
        {
            var grpc = SubMap(p, "grpc-opts");
            if (grpc is not null) q.Add(("serviceName", Str(grpc, "grpc-service-name")));
        }

        return $"vless://{Enc(uuid)}@{server}:{port}?{Query(q)}#{Enc(name)}";
    }

    private static string MapHysteria2(Dictionary<string, object> p, string server, string port, string name)
    {
        var password = Str(p, "password") ?? Str(p, "auth") ?? "";
        var q = new List<(string, string?)>
        {
            ("sni", Str(p, "sni") ?? Str(p, "servername")),
            ("obfs", Str(p, "obfs")),
            ("obfs-password", Str(p, "obfs-password")),
        };
        if (Bool(p, "skip-cert-verify")) q.Add(("insecure", "1"));
        return $"hysteria2://{Enc(password)}@{server}:{port}?{Query(q)}#{Enc(name)}";
    }

    private static string MapTuic(Dictionary<string, object> p, string server, string port, string name)
    {
        var uuid = Str(p, "uuid") ?? "";
        var password = Str(p, "password") ?? "";
        var q = new List<(string, string?)>
        {
            ("sni", Str(p, "sni") ?? Str(p, "servername")),
            ("congestion_control", Str(p, "congestion-controller") ?? Str(p, "congestion-control")),
            ("alpn", StrList(p, "alpn")),
            ("udp_relay_mode", Str(p, "udp-relay-mode")),
        };
        if (Bool(p, "skip-cert-verify")) q.Add(("allow_insecure", "1"));
        return $"tuic://{Enc(uuid)}:{Enc(password)}@{server}:{port}?{Query(q)}#{Enc(name)}";
    }

    private static string MapShadowsocks(Dictionary<string, object> p, string server, string port, string name)
    {
        var cipher = Str(p, "cipher") ?? "";
        var password = Str(p, "password") ?? "";
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cipher}:{password}"))
                              .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"ss://{userInfo}@{server}:{port}#{Enc(name)}";
    }

    private static Dictionary<string, object>? ToStringMap(object? o)
    {
        if (o is Dictionary<object, object> raw)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
                if (kv.Key is not null && kv.Value is not null) d[kv.Key.ToString()!] = kv.Value;
            return d;
        }
        return null;
    }

    private static Dictionary<string, object>? SubMap(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? ToStringMap(v) : null;

    private static string? Str(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    private static bool Bool(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) && v is not null
           && bool.TryParse(v.ToString(), out var b) && b;

    private static string? StrList(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return null;
        if (v is IEnumerable<object> seq)
        {
            var items = seq.Where(x => x is not null).Select(x => x!.ToString()).Where(s => !string.IsNullOrEmpty(s));
            var joined = string.Join(",", items);
            return string.IsNullOrEmpty(joined) ? null : joined;
        }
        return v.ToString();
    }

    private static string Enc(string s) => Uri.EscapeDataString(s);

    private static string Query(List<(string Key, string? Val)> parts)
        => string.Join("&", parts
            .Where(kv => !string.IsNullOrEmpty(kv.Val))
            .Select(kv => $"{kv.Key}={Enc(kv.Val!)}"));
}
