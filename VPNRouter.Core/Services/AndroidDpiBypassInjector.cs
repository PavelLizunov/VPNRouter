using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace VPNRouter.Core.Services;

public static class AndroidDpiBypassInjector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            Json.AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    private static readonly HashSet<string> ProxyTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "vless", "hysteria2", "tuic", "shadowsocks", "ss",
            "trojan", "http", "socks", "shadowtls",
        };

    public static string Inject(string json, string mode)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        if (string.IsNullOrEmpty(mode) ||
            string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return json;
        }

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
                    ob.Remove("udp_fragment");
                }
            }

            return root.ToJsonString(JsonOptions);
        }
        catch
        {
            return json;
        }
    }
}
