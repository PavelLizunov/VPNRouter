using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

public static class AndroidConfigBuilder
{
    public static string BuildConfigJson(string vlessUri, string? logOutputPath = null)
    {
        var entry = ServerUriParser.Parse(vlessUri);
        return BuildConfigJson(entry, logOutputPath);
    }

    private static void ApplyClashApiSecret(AppSettings settings)
    {
        try
        {
            var secret = AndroidStorage.GetClashApiSecret();
            settings.SingBox.ClashApiSecret = secret;
            VPNRouter.Core.Platform.Android.AndroidSingBoxRuntime.ClashApiSecret = secret;
        }
        catch
        {
        }
    }

    public static string BuildConfigJson(VlessServerEntry entry, string? logOutputPath = null)
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = "full";
        settings.App.LogLevel = "info";
        settings.App.ConfigMode = "generated";
        ApplyClashApiSecret(settings);
        settings.App.BypassRussianTraffic = AndroidStorage.GetBypassRussianTraffic();
        settings.App.BlockAds = AndroidStorage.GetBlockAds();
        settings.App.ForceIpv4Only = AndroidStorage.GetDnsStrategy() switch
        {
            "prefer_ipv6" => false,
            "prefer_ipv4" => false,
            _ => true,
        };
        if (AndroidStorage.GetAutoSelectBestServer())
        {
            var pool = GetAllCandidateServers();
            if (pool.Count > 1)
            {
                foreach (var s in pool)
                    settings.Vless.Servers.Add(s);
                settings.Vless.ActiveServer = entry.Name ?? string.Empty;
                settings.Vless.AutoSelectBestServer = true;
            }
            else
            {
                settings.Vless.Servers.Add(entry);
            }
        }
        else
        {
            settings.Vless.Servers.Add(entry);
        }

        var profile = new Profile
        {
            Name = "AndroidDefault",
            DnsMode = "vpn_only",
            BlockOnVpnFail = AndroidStorage.GetBlockOnVpnFail(),
        };

        var processNames = System.Array.Empty<string>();
        var sbConfig = ConfigGenerator.Generate(profile, processNames, settings);

        try
        {
            var leakCheck = LeakProtection.ValidateConfig(sbConfig, settings);
            foreach (var w in leakCheck.Warnings)
                Serilog.Log.Logger.Warning("[AndroidConfigBuilder] LeakProtection: {Warn}", w);
            if (!leakCheck.IsValid)
                Serilog.Log.Logger.Warning(
                    "[AndroidConfigBuilder] LeakProtection errors: {Errors}",
                    string.Join("; ", leakCheck.Errors));
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "[AndroidConfigBuilder] LeakProtection.ValidateConfig threw");
        }

        var json = ConfigGenerator.Serialize(sbConfig);

        var patched = PatchLogPathForAndroid(json, logOutputPath);

        return InjectDpiBypass(patched, AndroidStorage.GetDpiBypassMode());
    }

    public static string BuildConfigJsonFromCustom(string rawJson, string? logOutputPath = null)
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = "full";
        settings.App.LogLevel = "info";
        settings.App.ConfigMode = "custom";
        ApplyClashApiSecret(settings);
        settings.App.BypassRussianTraffic = AndroidStorage.GetBypassRussianTraffic();
        settings.App.BlockAds = AndroidStorage.GetBlockAds();
        settings.App.ForceIpv4Only = AndroidStorage.GetDnsStrategy() switch
        {
            "prefer_ipv6" => false,
            "prefer_ipv4" => false,
            _ => true,
        };

        var processNames = System.Array.Empty<string>();

        var injectedJson = CustomConfigInjector.Inject(rawJson, processNames, settings);

        var patched = PatchLogPathForAndroid(injectedJson, logOutputPath);

        return InjectDpiBypass(patched, AndroidStorage.GetDpiBypassMode());
    }

    public static string InjectDpiBypass(string json, string mode)
        => AndroidDpiBypassInjector.Inject(json, mode);

    private static System.Collections.Generic.List<VlessServerEntry> GetAllCandidateServers()
    {
        var all = new System.Collections.Generic.List<VlessServerEntry>(AndroidStorage.GetServers());
        foreach (var sub in AndroidStorage.GetSubscriptions())
        {
            if (sub is { Enabled: true, Servers: not null })
                all.AddRange(sub.Servers);
        }
        return all;
    }

    private static string PatchLogPathForAndroid(string json, string? logOutputPath)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return json;

            if (root["log"] is JsonObject logObj)
            {
                if (!string.IsNullOrEmpty(logOutputPath))
                {
                    logObj["output"] = logOutputPath;
                }
                else
                {
                    logObj.Remove("output");
                }
                var existingLevel = logObj["level"]?.GetValue<string>();
                if (string.IsNullOrEmpty(existingLevel)
                    || string.Equals(existingLevel, "trace", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existingLevel, "debug", System.StringComparison.OrdinalIgnoreCase))
                {
                    logObj["level"] = "info";
                }
            }

            if (root["inbounds"] is JsonArray inbounds)
            {
                foreach (var inboundNode in inbounds)
                {
                    if (inboundNode is not JsonObject inb) continue;
                    var type = inb["type"]?.GetValue<string>();
                    if (type != "tun") continue;

                    inb["auto_route"] = false;
                    inb["strict_route"] = false;

                    inb["stack"] = "gvisor";

                    inb["mtu"] = 1500;
                }
            }

            if (root["outbounds"] is JsonArray outbounds)
            {
                foreach (var outboundNode in outbounds)
                {
                    if (outboundNode is not JsonObject ob) continue;
                    ob.Remove("tcp_keep_alive");
                    ob.Remove("tcp_keep_alive_interval");
                }
            }

            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch
        {
            return json;
        }
    }
}
