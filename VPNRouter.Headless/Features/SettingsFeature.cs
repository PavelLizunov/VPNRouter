using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class SettingsFeature
{
    private static readonly HashSet<string> AllowedSettings = new(StringComparer.Ordinal)
    {
        "mtu",
        "ipv6Enabled",
        "strictRoute",
        "strictDns",
        "dnsMode",
        "bypassRussianTraffic",
        "blockAds",
        "dnsLeakLockdown",
        "routeExcludeAddress"
    };

    private readonly ConfigStorage _storage;
    private readonly Func<bool> _supportsDnsLockdown;
    private readonly ILogger _logger;

    public SettingsFeature(ConfigStorage storage, Func<bool> supportsDnsLockdown, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _supportsDnsLockdown = supportsDnsLockdown ?? (() => false);
        _logger = logger ?? Log.Logger;
    }

    public object Get(JsonElement parameters = default)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var settings = _storage.GetSettings();
        var isCustom = string.Equals(settings.App?.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase);
        var configuredMode = settings.App?.DnsModeOverride ?? ResolveLocalProfileDnsMode(settings);

        string dnsMode;
        string? dnsModeSemantics = null;

        if (isCustom)
        {
            if (settings.App?.StrictDns == true)
            {
                dnsMode = "vpn_only";
                dnsModeSemantics = "StrictDns forces remote DNS (vpn_only) over custom config";
            }
            else if (string.Equals(settings.App?.RoutingMode, "full", StringComparison.OrdinalIgnoreCase))
            {
                dnsMode = "vpn_only";
                dnsModeSemantics = "Full tunnel forces remote DNS (vpn_only) over custom config";
            }
            else
            {
                dnsMode = "custom";
                dnsModeSemantics = "DNS is defined by raw custom sing-box configuration";
            }
        }
        else if (settings.App?.StrictDns == true)
        {
            dnsMode = configuredMode;
            dnsModeSemantics = "StrictDns active: all DNS forced through VPN tunnel (vpn-dns) regardless of mode";
        }
        else if (string.Equals(settings.App?.RoutingMode, "full", StringComparison.OrdinalIgnoreCase))
        {
            dnsMode = configuredMode;
            dnsModeSemantics = "Full tunnel active: all DNS forced through VPN tunnel (vpn-dns)";
        }
        else
        {
            dnsMode = configuredMode;
        }

        return new
        {
            mtu = settings.Tun?.Mtu ?? TunSettings.DefaultMtu,
            ipv6Enabled = settings.Tun?.Ipv6Enabled ?? false,
            strictRoute = settings.Tun?.StrictRoute ?? false,
            strictDns = settings.App?.StrictDns ?? false,
            dnsMode,
            dnsModeOverride = settings.App?.DnsModeOverride,
            dnsModeSemantics,
            bypassRussianTraffic = settings.App?.BypassRussianTraffic ?? true,
            blockAds = settings.App?.BlockAds ?? false,
            dnsLeakLockdown = settings.App?.DnsLeakLockdown ?? false,
            routeExcludeAddress = settings.Tun?.RouteExcludeAddress?.ToList() ?? new List<string>()
        };
    }

    public void Set(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "values");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("values", out var valuesProp) || valuesProp.ValueKind != JsonValueKind.Object)
            throw new RouterException("invalid_argument", "Missing values parameter (expected JSON object)");

        var revision = revProp.GetString()!;
        _storage.ValidateRevision(revision);

        // Strict allowlist enforcement: reject unknown setting keys (fixed code, no echo)
        foreach (var prop in valuesProp.EnumerateObject())
        {
            if (!AllowedSettings.Contains(prop.Name))
            {
                throw new RouterException("invalid_argument", "Unknown setting");
            }
        }

        var settings = _storage.GetSettings();
        var isCustom = string.Equals(settings.App?.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase);

        // STAGE 1: Validate all candidate values into detached locals BEFORE modifying settings
        int? newMtu = null;
        if (valuesProp.TryGetProperty("mtu", out var mtuProp))
        {
            if (mtuProp.ValueKind != JsonValueKind.Number || !mtuProp.TryGetInt32(out var mtuVal) ||
                mtuVal < TunSettings.MinimumMtu || mtuVal > TunSettings.MaximumMtu)
            {
                throw new RouterException("invalid_argument",
                    $"MTU must be an integer between {TunSettings.MinimumMtu} and {TunSettings.MaximumMtu}");
            }
            newMtu = mtuVal;
        }

        bool? newIpv6 = null;
        if (valuesProp.TryGetProperty("ipv6Enabled", out var ipv6Prop))
        {
            if (ipv6Prop.ValueKind != JsonValueKind.True && ipv6Prop.ValueKind != JsonValueKind.False)
                throw new RouterException("invalid_argument", "ipv6Enabled must be a boolean");
            newIpv6 = ipv6Prop.GetBoolean();
        }

        int currentMtu = settings.Tun?.Mtu ?? TunSettings.DefaultMtu;
        bool currentIpv6 = settings.Tun?.Ipv6Enabled ?? false;
        int effectiveMtu = newMtu ?? currentMtu;
        bool effectiveIpv6 = newIpv6 ?? currentIpv6;

        if (effectiveIpv6 && effectiveMtu < TunSettings.MinimumIpv6Mtu)
        {
            throw new RouterException("invalid_argument",
                $"IPv6 requires an MTU of at least {TunSettings.MinimumIpv6Mtu}");
        }

        bool? newStrictRoute = null;
        if (valuesProp.TryGetProperty("strictRoute", out var srProp))
        {
            if (srProp.ValueKind != JsonValueKind.True && srProp.ValueKind != JsonValueKind.False)
                throw new RouterException("invalid_argument", "strictRoute must be a boolean");
            newStrictRoute = srProp.GetBoolean();
        }

        bool? newStrictDns = null;
        if (valuesProp.TryGetProperty("strictDns", out var sdProp))
        {
            if (sdProp.ValueKind != JsonValueKind.True && sdProp.ValueKind != JsonValueKind.False)
                throw new RouterException("invalid_argument", "strictDns must be a boolean");
            newStrictDns = sdProp.GetBoolean();
        }

        bool hasDnsMode = false;
        string? newDnsMode = null;
        if (valuesProp.TryGetProperty("dnsMode", out var dmProp))
        {
            if (isCustom)
            {
                throw new RouterException("invalid_argument", "dnsMode cannot be configured in custom config mode");
            }

            if (dmProp.ValueKind == JsonValueKind.Null)
            {
                hasDnsMode = true;
                newDnsMode = null;
            }
            else if (dmProp.ValueKind == JsonValueKind.String)
            {
                var rawMode = dmProp.GetString();
                if (string.IsNullOrWhiteSpace(rawMode))
                    throw new RouterException("invalid_argument", "dnsMode must be 'vpn_only', 'smart', or 'direct'");

                var mode = rawMode.Trim().ToLowerInvariant();
                if (mode != "vpn_only" && mode != "smart" && mode != "direct")
                    throw new RouterException("invalid_argument", "dnsMode must be 'vpn_only', 'smart', or 'direct'");

                hasDnsMode = true;
                newDnsMode = mode;
            }
            else
            {
                throw new RouterException("invalid_argument", "dnsMode must be a string or null");
            }
        }

        bool? newBypassRu = null;
        if (valuesProp.TryGetProperty("bypassRussianTraffic", out var ruProp))
        {
            if (ruProp.ValueKind != JsonValueKind.True && ruProp.ValueKind != JsonValueKind.False)
                throw new RouterException("invalid_argument", "bypassRussianTraffic must be a boolean");
            newBypassRu = ruProp.GetBoolean();
        }

        bool? newBlockAds = null;
        if (valuesProp.TryGetProperty("blockAds", out var adsProp))
        {
            if (adsProp.ValueKind != JsonValueKind.True && adsProp.ValueKind != JsonValueKind.False)
                throw new RouterException("invalid_argument", "blockAds must be a boolean");
            newBlockAds = adsProp.GetBoolean();
        }

        bool? newLeakLockdown = null;
        if (valuesProp.TryGetProperty("dnsLeakLockdown", out var dlProp))
        {
            if (dlProp.ValueKind != JsonValueKind.True && dlProp.ValueKind != JsonValueKind.False)
                throw new RouterException("invalid_argument", "dnsLeakLockdown must be a boolean");
            var enableLockdown = dlProp.GetBoolean();
            if (enableLockdown && !_supportsDnsLockdown())
            {
                throw new RouterException("unsupported", "DNS leak lockdown is not supported in this runtime/session");
            }
            newLeakLockdown = enableLockdown;
        }

        List<string>? newExcludeAddresses = null;
        if (valuesProp.TryGetProperty("routeExcludeAddress", out var cidrsProp))
        {
            if (cidrsProp.ValueKind != JsonValueKind.Array)
                throw new RouterException("invalid_argument", "routeExcludeAddress must be an array of CIDR strings");

            var cleanCidrs = new List<string>();
            int count = 0;
            foreach (var item in cidrsProp.EnumerateArray())
            {
                count++;
                if (count > 256)
                    throw new RouterException("invalid_argument", "Too many routeExcludeAddress entries (maximum 256)");

                if (item.ValueKind != JsonValueKind.String)
                    throw new RouterException("invalid_argument", "CIDR entries must be strings");

                var cidr = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(cidr))
                    continue;

                if (cidr.Length > 64)
                    throw new RouterException("invalid_argument", "CIDR entry exceeds 64 characters");

                ValidateCidr(cidr);
                cleanCidrs.Add(cidr);
            }
            newExcludeAddresses = cleanCidrs;
        }

        // STAGE 2: All values validated — apply to detached settings
        settings.Tun ??= new TunSettings();
        settings.App ??= new AppConfig();

        if (newMtu.HasValue)
            settings.Tun.Mtu = newMtu.Value;

        if (newIpv6.HasValue)
            settings.Tun.Ipv6Enabled = newIpv6.Value;

        if (newStrictRoute.HasValue)
            settings.Tun.StrictRoute = newStrictRoute.Value;

        if (newStrictDns.HasValue)
            settings.App.StrictDns = newStrictDns.Value;

        if (hasDnsMode)
            settings.App.DnsModeOverride = newDnsMode;

        if (newBypassRu.HasValue)
            settings.App.BypassRussianTraffic = newBypassRu.Value;

        if (newBlockAds.HasValue)
            settings.App.BlockAds = newBlockAds.Value;

        if (newLeakLockdown.HasValue)
            settings.App.DnsLeakLockdown = newLeakLockdown.Value;

        if (newExcludeAddresses != null)
            settings.Tun.RouteExcludeAddress = newExcludeAddresses;

        // STAGE 3: Persist once
        _storage.SaveSettings(settings, revision);
        _logger.Information("[SettingsFeature] Successfully updated settings");
    }

    private string ResolveLocalProfileDnsMode(AppSettings settings)
    {
        return OfflineProfileHelper.ResolveLocalProfileDnsMode(settings, _storage.DataDir, _logger);
    }

    private static void ValidateCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2)
            throw new RouterException("invalid_argument", "Invalid CIDR notation: expected address/prefix");

        if (!IPAddress.TryParse(parts[0], out var ip))
            throw new RouterException("invalid_argument", "Invalid IP address in CIDR");

        if (!int.TryParse(parts[1], out var prefix) || prefix < 0)
            throw new RouterException("invalid_argument", "Invalid prefix length in CIDR");

        int maxPrefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefix > maxPrefix)
            throw new RouterException("invalid_argument", "Prefix length exceeds maximum in CIDR");
    }
}
