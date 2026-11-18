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
        => ParseProxiesToUris(body, out _, logger);

    internal static List<string> ParseProxiesToUris(string body, out bool skippedChains, ILogger? logger = null)
    {
        skippedChains = false;

        const int MaxBodyChars = 8 * 1024 * 1024;
        if (body.Length > MaxBodyChars)
        {
            logger?.Warning("[Clash] YAML body too large ({Len} chars > {Max}) — skipping parse",
                body.Length, MaxBodyChars);
            return [];
        }

        Dictionary<object, object>? document;
        try
        {
            var de = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            document = de.Deserialize<Dictionary<object, object>>(body);
        }
        catch (Exception)
        {
            logger?.Warning("[Clash] YAML deserialize failed");
            return [];
        }

        var mergeBudget = 100_000;
        var root = ReadRoutingMap(document, ref mergeBudget, out var rootResolved);
        if (root is null || !root.TryGetValue("proxies", out var proxiesObj)
            || proxiesObj is not IEnumerable<object> proxies)
        {
            return [];
        }

        var rows = new NativeSubscriptionRows();
        var allowChains = rootResolved || root.ContainsKey("proxy-groups");
        // Clash groups and built-ins are routing choices, never concrete upstream proxies.
        foreach (var name in new[] { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL" })
            rows.Add(name, null, false, null);
        if (root.TryGetValue("proxy-groups", out var groupsObj) && groupsObj is IEnumerable<object> groups)
        {
            foreach (var group in groups)
            {
                if (ReadRoutingMap(group, ref mergeBudget, out var resolved) is not { } groupMap) continue;
                var groupName = Str(groupMap, "name");
                allowChains &= resolved || groupName != null;
                rows.Add(groupName, null, false, null);
            }
        }

        foreach (var p in proxies)
        {
            if (ReadRoutingMap(p, ref mergeBudget, out var resolved) is not { } proxy) continue;
            var name = Str(proxy, "name");
            if (!resolved)
            {
                allowChains &= name != null;
                rows.Add(name, string.Empty, false, null);
                continue;
            }
            string? via = null;
            if (proxy.TryGetValue("dialer-proxy", out var dialer) && dialer is not "")
                via = dialer as string ?? string.Empty;
            string? uri = null;
            try
            {
                uri = MapProxyToUri(proxy);
            }
            catch (Exception)
            {
                logger?.Warning("[Clash] Skipped invalid proxy entry");
            }
            rows.Add(name, via, uri != null && CanChain(proxy), uri);
        }

        var uris = rows.ToShareUris(out skippedChains, allowChains);
        logger?.Debug("[Clash] mapped {Count}/{Total} proxies to share URIs", uris.Count, proxies.Count());
        return uris;
    }

    // The document-wide YamlDotNet merge parser rejects inline merge maps. Resolve
    // only root, proxy and group fields so an unsupported merge does not discard other rows.
    private static Dictionary<string, object>? ReadRoutingMap(object? value, ref int budget, out bool resolved)
    {
        resolved = true;
        if (value is not Dictionary<object, object> local) return null;
        string? dialerSpelling = null;
        if (!local.ContainsKey("<<"))
        {
            resolved = HasConsistentDialerKey(local, ref dialerSpelling);
            return ToStringMap(local);
        }

        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<object>();
        pending.Push(local);
        while (pending.TryPop(out var node))
        {
            if (node is Dictionary<object, object> source && source.Count < budget)
            {
                budget -= 1 + source.Count;
                if (!HasConsistentDialerKey(source, ref dialerSpelling)) break;
                // Explicit keys win, even if the merge appears after them.
                foreach (var pair in source)
                    if (pair.Key is string key && key != "<<") fields.TryAdd(key, pair.Value);
                if (source.TryGetValue("<<", out var merge)) pending.Push(merge);
            }
            else if (node is List<object> sources && sources.Count < budget)
            {
                budget -= 1 + sources.Count;
                if (!sources.All(item => item is Dictionary<object, object>)) break;
                for (int i = sources.Count - 1; i >= 0; i--) pending.Push(sources[i]);
            }
            else
            {
                break;
            }
            if (pending.Count == 0)
                return fields.Where(pair => pair.Value != null)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        resolved = false;
        return ToStringMap(local);
    }

    private static bool HasConsistentDialerKey(Dictionary<object, object> map, ref string? spelling)
    {
        foreach (var key in map.Keys)
        {
            if (key is not string text || !text.Equals("dialer-proxy", StringComparison.OrdinalIgnoreCase)) continue;
            if (spelling != null && text != spelling) return false;
            spelling = text;
        }
        return true;
    }

    // A stand-alone URI can use the existing best-effort importer. Both hops of a
    // chain must retain all their effective transport, TLS and dialing options.
    private static bool CanChain(Dictionary<string, object> proxy)
    {
        var network = Str(proxy, "network") ?? "tcp";
        if (Str(proxy, "type") != "vless" || network is not ("tcp" or "ws" or "grpc")
            || !NativeSubscriptionRows.IsValidHost(Str(proxy, "server"))
            || !ushort.TryParse(Str(proxy, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port == 0)
            return false;

        return proxy.All(pair => pair.Key switch
        {
            "name" or "type" or "server" or "uuid" or "flow" or "network" or "dialer-proxy"
                or "sni" or "servername" or "client-fingerprint" => pair.Value is string,
            "port" => true,
            "tls" => bool.TryParse(pair.Value.ToString(), out _),
            "udp" => Bool(proxy, "udp"),
            "skip-cert-verify" => bool.TryParse(pair.Value.ToString(), out var skip) && !skip,
            "alpn" => Bool(proxy, "tls") && !proxy.ContainsKey("reality-opts")
                && (CanMapAlpn(pair.Value) || (pair.Value is IEnumerable<object> alpn && alpn.All(CanMapAlpn))),
            "reality-opts" => ToStringMap(pair.Value) is { } reality && reality.All(option =>
                option.Value is string s && (option.Key == "public-key"
                    || (option.Key == "short-id" && VlessUriParser.IsValidRealityShortId(s)))),
            "grpc-opts" => network == "grpc" && ToStringMap(pair.Value) is { } grpc
                && grpc.All(option => option.Key == "grpc-service-name" && option.Value is string),
            "ws-opts" => network == "ws" && ToStringMap(pair.Value) is { } ws && ws.All(option =>
                (option.Key == "path" && option.Value is string)
                || (option.Key == "headers" && ToStringMap(option.Value) is { } headers
                    && headers.All(header => header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                        && header.Value is string))),
            _ => false,
        });
    }

    private static bool CanMapAlpn(object value) =>
        value is string { Length: > 0 } s && !s.Contains(',') && s.Trim() == s;

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

        if (!server.StartsWith('[') && Uri.CheckHostName(server) == UriHostNameType.IPv6)
            server = $"[{server}]";
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
