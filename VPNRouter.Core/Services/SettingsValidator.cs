using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Outcome of a single <see cref="SettingsValidator.Validate"/> run.
/// <see cref="Reasons"/> are FATAL — caller (typically
/// <see cref="SettingsLoader"/>) should backup the bad file and reset
/// to defaults. <see cref="Warnings"/> are soft observations that should
/// be logged but do not justify a reset (e.g. an active custom config
/// path missing on disk — the file may come back).
/// </summary>
public sealed record SettingsValidationResult(
    bool IsValid,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates an in-memory <see cref="AppSettings"/> tree against
/// structural invariants — the kind of damage that slips past
/// YamlDotNet (which only maps types) but explodes deep inside the
/// runtime as <c>NullReferenceException</c>, port-out-of-range,
/// "value must be one of …", or — worst of all — silent leaks
/// (a typoed <c>config_mode</c> treated as the wrong tunnel mode).
///
/// <para>Pure function — no side effects. Reads only. Caller is
/// responsible for surfacing or acting on the result.</para>
///
/// <para>Runs AFTER <see cref="SettingsMigrator"/>, so schema-version
/// drift has already been smoothed over by the time we get here.</para>
///
/// <para>v2.32.0 — see plans/v2.32.0-settings-validator.md.</para>
/// </summary>
public static class SettingsValidator
{
    // The allowed-values sets are listed once so the test suite has a
    // single canonical reference and we don't get drift between
    // validator and Strings.cs / ConfigMode-flipping code.

    private static readonly HashSet<string> AllowedConfigModes =
        new(StringComparer.OrdinalIgnoreCase) { "generated", "subscribe", "custom" };

    private static readonly HashSet<string> AllowedRoutingModes =
        new(StringComparer.OrdinalIgnoreCase) { "split", "full" };

    // v2.40.x (Fix #7): "system" added — follow the OS appearance. Omitting it
    // here would make SettingsValidator REJECT every config that adopted the new
    // default theme and reset the WHOLE config.yaml to defaults on load (silent
    // data loss). Caught by the YAML round-trip regression suite.
    private static readonly HashSet<string> AllowedThemes =
        new(StringComparer.OrdinalIgnoreCase) { "light", "dark", "system" };

    private static readonly HashSet<string> AllowedDnsStrategies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ipv4_only", "ipv6_only", "prefer_ipv4", "prefer_ipv6", "default"
        };

    private static readonly HashSet<string> AllowedUpdateChannels =
        new(StringComparer.OrdinalIgnoreCase) { "stable", "experimental" };

    /// <summary>
    /// Walks the post-migration <paramref name="settings"/> object and
    /// reports any structural invariant violations.
    /// </summary>
    public static SettingsValidationResult Validate(AppSettings? settings)
    {
        var fatal = new List<string>();
        var warn = new List<string>();

        if (settings == null)
        {
            fatal.Add("settings object is null");
            return new SettingsValidationResult(false, fatal, warn);
        }

        ValidateApp(settings, fatal, warn);
        ValidateVless(settings, fatal);
        ValidateTun(settings, fatal);
        ValidateDns(settings, fatal);
        ValidateMonitoring(settings, fatal);
        ValidateUpdate(settings, fatal);
        ValidateProfileSources(settings, fatal);

        return new SettingsValidationResult(fatal.Count == 0, fatal, warn);
    }

    private static void ValidateApp(AppSettings s, List<string> fatal, List<string> warn)
    {
        var app = s.App;
        if (app == null)
        {
            fatal.Add("app section is null");
            return;
        }

        // ConfigMode: empty / unknown both invalid. Empty hits when the
        // yaml has an explicit blank `config_mode:` line — that's not the
        // first-run path (which uses the property's "generated" default),
        // so the only way to get here is corruption / mistaken edit.
        var modeRaw = (app.ConfigMode ?? string.Empty).Trim();
        if (modeRaw.Length == 0 || !AllowedConfigModes.Contains(modeRaw))
        {
            fatal.Add(
                $"app.config_mode must be one of {Join(AllowedConfigModes)}, got '{app.ConfigMode}'");
        }

        // RoutingMode + Theme: SettingsLoader.Parse already substitutes
        // defaults for empty values, so by the time we run a non-empty
        // value is meaningful. Empty here would only show up if Parse
        // skipped that branch (it doesn't, but defensive).
        var routingRaw = (app.RoutingMode ?? string.Empty).Trim();
        if (routingRaw.Length > 0 && !AllowedRoutingModes.Contains(routingRaw))
        {
            fatal.Add(
                $"app.routing_mode must be one of {Join(AllowedRoutingModes)}, got '{app.RoutingMode}'");
        }

        var themeRaw = (app.Theme ?? string.Empty).Trim();
        if (themeRaw.Length > 0 && !AllowedThemes.Contains(themeRaw))
        {
            fatal.Add(
                $"app.theme must be one of {Join(AllowedThemes)}, got '{app.Theme}'");
        }

        if (!IsValidPort(app.TgProxyPort))
        {
            fatal.Add(
                $"app.tg_proxy_port must be 1..65535, got {app.TgProxyPort}");
        }

        if (!string.IsNullOrWhiteSpace(app.SubscriptionUrl)
            && !Uri.TryCreate(app.SubscriptionUrl, UriKind.Absolute, out _))
        {
            fatal.Add(
                $"app.subscription_url is not a parseable absolute URI: '{app.SubscriptionUrl}'");
        }

        if (app.Subscriptions != null)
        {
            for (int i = 0; i < app.Subscriptions.Count; i++)
            {
                var sub = app.Subscriptions[i];
                if (sub == null) continue;
                if (!string.IsNullOrWhiteSpace(sub.Url)
                    && !Uri.TryCreate(sub.Url, UriKind.Absolute, out _))
                {
                    fatal.Add(
                        $"app.subscriptions[{i}].url is not a parseable absolute URI: '{sub.Url}'");
                }
            }
        }

        // ConfigMode == "custom" with a missing active path is a soft
        // warning. The file may be temporarily missing (USB unplugged,
        // permission glitch, a fresh checkout) and the next App start
        // can recover once the user restores it. We do NOT reset the
        // entire config over a missing file.
        if (string.Equals(modeRaw, "custom", StringComparison.OrdinalIgnoreCase))
        {
            CheckActiveCustomConfigPath(app, warn);
        }
    }

    private static void CheckActiveCustomConfigPath(AppConfig app, List<string> warn)
    {
        if (app.CustomConfigs == null || app.CustomConfigs.Count == 0)
            return;

        CustomConfigEntry? active = null;
        if (!string.IsNullOrWhiteSpace(app.ActiveCustomConfig))
        {
            active = app.CustomConfigs.FirstOrDefault(c =>
                string.Equals(c?.Name, app.ActiveCustomConfig, StringComparison.OrdinalIgnoreCase));
        }
        active ??= app.CustomConfigs[0];

        if (active == null || string.IsNullOrWhiteSpace(active.Path)) return;

        string resolved;
        try
        {
            resolved = Environment.ExpandEnvironmentVariables(active.Path);
        }
        catch
        {
            // Bad %VAR% syntax — surface as warning rather than fatal so
            // the user sees the issue without losing every other setting.
            warn.Add($"app.custom_configs[{active.Name}].path has invalid env-var syntax: '{active.Path}'");
            return;
        }

        if (!File.Exists(resolved))
        {
            warn.Add(
                $"app.custom_configs[{active.Name}].path missing on disk: {resolved}");
        }
    }

    private static void ValidateVless(AppSettings s, List<string> fatal)
    {
        var v = s.Vless;
        if (v == null) return;

        if (!IsValidPort(v.Port))
        {
            fatal.Add($"vless.port must be 1..65535, got {v.Port}");
        }

        if (v.Servers != null)
        {
            for (int i = 0; i < v.Servers.Count; i++)
            {
                var entry = v.Servers[i];
                if (entry == null) continue;
                if (!IsValidPort(entry.Port))
                {
                    fatal.Add($"vless.servers[{i}].port must be 1..65535, got {entry.Port}");
                }
            }
        }
    }

    private static void ValidateTun(AppSettings s, List<string> fatal)
    {
        var t = s.Tun;
        if (t == null) return;

        // 576 is the IPv4 minimum-MTU you'll ever see in the wild;
        // 65535 is the upper bound of a 16-bit IP total-length field.
        // Anything outside this range almost guarantees sing-box
        // refuses to bring TUN up.
        if (t.Mtu < 576 || t.Mtu > 65535)
        {
            fatal.Add($"tun.mtu must be 576..65535, got {t.Mtu}");
        }
    }

    private static void ValidateDns(AppSettings s, List<string> fatal)
    {
        var d = s.Dns;
        if (d == null) return;

        var strategy = (d.Strategy ?? string.Empty).Trim();
        if (strategy.Length > 0 && !AllowedDnsStrategies.Contains(strategy))
        {
            fatal.Add(
                $"dns.strategy must be one of {Join(AllowedDnsStrategies)}, got '{d.Strategy}'");
        }
    }

    private static void ValidateMonitoring(AppSettings s, List<string> fatal)
    {
        var m = s.Monitoring;
        if (m == null) return;

        if (m.HealthCheckInterval <= 0)
        {
            fatal.Add(
                $"monitoring.health_check_interval must be > 0, got {m.HealthCheckInterval}");
        }
        if (m.ProcessScanInterval <= 0)
        {
            fatal.Add(
                $"monitoring.process_scan_interval must be > 0, got {m.ProcessScanInterval}");
        }
        if (m.MaxRestartAttempts < 0)
        {
            fatal.Add(
                $"monitoring.max_restart_attempts must be >= 0, got {m.MaxRestartAttempts}");
        }
    }

    private static void ValidateUpdate(AppSettings s, List<string> fatal)
    {
        var u = s.Update;
        if (u == null) return;

        var ch = (u.Channel ?? string.Empty).Trim();
        if (ch.Length > 0 && !AllowedUpdateChannels.Contains(ch))
        {
            fatal.Add(
                $"update.channel must be one of {Join(AllowedUpdateChannels)}, got '{u.Channel}'");
        }
    }

    private static void ValidateProfileSources(AppSettings s, List<string> fatal)
    {
        if (s.ProfileSources == null) return;

        for (int i = 0; i < s.ProfileSources.Count; i++)
        {
            var ps = s.ProfileSources[i];
            if (ps == null) continue;
            if (!string.IsNullOrWhiteSpace(ps.Url)
                && !Uri.TryCreate(ps.Url, UriKind.Absolute, out _))
            {
                fatal.Add(
                    $"profile_sources[{i}].url is not a parseable absolute URI: '{ps.Url}'");
            }
        }
    }

    private static bool IsValidPort(int port) => port >= 1 && port <= 65535;

    private static string Join(IEnumerable<string> values) =>
        string.Join('/', values);
}
