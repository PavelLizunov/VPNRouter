using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class SettingsLoader
{
    private static readonly string DefaultConfigPath = AppPaths.ConfigYamlPath;

    public static AppSettings Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;

        // v2.23.0 self-healing: --safe flag skips parsing user yaml
        // and returns the pure defaults. Settings on disk stay intact
        // (so next normal launch picks them up), but the current
        // process sees a clean slate.
        if (SafeMode.Enabled)
            return CreateDefaults();

        if (!File.Exists(configPath))
        {
            // Write example config and return defaults
            var defaults = CreateDefaults();
            WriteExample(configPath, defaults);
            return defaults;
        }

        var yaml = File.ReadAllText(configPath);
        return Parse(yaml);
    }

    public static AppSettings Parse(string yaml)
    {
        // Pre-parse structural check: reject anything whose root node is NOT
        // a mapping (key/value) before the main deserializer gets a chance.
        // Without this, YamlDotNet + IgnoreUnmatchedProperties silently
        // deserializes ANY well-formed YAML scalar / sequence into an empty
        // AppSettings with defaults — masking real data loss. Garbage like
        //   !!!not:valid: yaml: here
        // currently slides through as "config.yaml parses" which is worse
        // than a hard error because the user never sees the corruption.
        //
        // Empty / whitespace-only YAML is the one exception: YamlStream
        // returns zero documents and we let the caller get fresh defaults
        // (this is the first-launch path where we auto-create the file).
        if (!string.IsNullOrWhiteSpace(yaml))
        {
            try
            {
                var yamlStream = new YamlStream();
                yamlStream.Load(new StringReader(yaml));
                if (yamlStream.Documents.Count > 0)
                {
                    var root = yamlStream.Documents[0].RootNode;
                    if (root is not YamlMappingNode map)
                        throw new InvalidDataException(
                            $"config.yaml root must be a YAML mapping (key: value pairs), got {root.NodeType}. Check indentation / syntax.");

                    // Recognize at least one top-level AppSettings key. Without
                    // this, garbage like `!!!not:valid: yaml: here` parses as a
                    // valid mapping with unknown keys, and IgnoreUnmatchedProperties
                    // silently deserializes to an empty AppSettings with defaults.
                    // We require at least one key we know about (the set below is
                    // every top-level YamlMember on AppSettings as of schema v1).
                    var knownKeys = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "schema_version", "app", "profile_sources", "active_profile",
                        "vless", "tun", "dns", "singbox", "monitoring",
                        "custom_apps", "custom_group_apps", "custom_categories", "update"
                    };
                    var hasKnownKey = map.Children.Keys
                        .OfType<YamlScalarNode>()
                        .Any(k => k.Value != null && knownKeys.Contains(k.Value));
                    if (!hasKnownKey)
                        throw new InvalidDataException(
                            "config.yaml does not contain any recognized VPNRouter settings keys " +
                            $"(expected at least one of: {string.Join(", ", knownKeys.Take(5))}, ...). " +
                            "The file may be corrupted or from a different application.");
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                // YamlException from the low-level parser — malformed syntax
                throw new InvalidDataException($"config.yaml is not valid YAML: {ex.Message}", ex);
            }
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var settings = deserializer.Deserialize<AppSettings>(yaml);

        // YamlDotNet returns null for empty/whitespace YAML (caller expects
        // defaults on first launch). Real content reaching this path with
        // null would be unusual now that the mapping check above has run,
        // but belt-and-braces: keep the fallback so we never crash here.
        if (settings == null)
            return new AppSettings();

        // YamlDotNet may set subsections to null if YAML has empty keys (e.g. "vless:" with no children)
        settings.App ??= new AppConfig();
        settings.Vless ??= new VlessConfig();
        settings.Tun ??= new TunSettings();
        settings.Dns ??= new DnsSettings();
        settings.SingBox ??= new SingBoxSettings();
        settings.Monitoring ??= new MonitoringSettings();
        settings.ProfileSources ??= new List<ProfileSource>();
        settings.CustomApps ??= new List<string>();

        // Nested objects inside Vless can also be null
        settings.Vless.Reality ??= new VlessRealityConfig();
        settings.Vless.Tls ??= new VlessTlsConfig();
        settings.Vless.Transport ??= new VlessTransportConfig();
        settings.Vless.Servers ??= new List<VlessServerEntry>();

        // v2.25.1-r2: strip legacy "your.server.com" / "your-uuid-here"
        // placeholder values that older versions (pre-v2.24.3) wrote into
        // the config on first launch. The reason this re-surfaces even
        // after CreateDefaults stopped emitting placeholders: the
        // ViewModel calls GetEffectiveServers() on load, which — when
        // Vless.Servers is empty — builds a SYNTHETIC entry from the
        // legacy root Vless.Server / Uuid scalars. That synthetic entry
        // carries "your.server.com" into the UI's Servers collection,
        // and the next SaveSettings writes it back as an explicit entry
        // in Vless.Servers. After that the placeholder is "promoted"
        // from a legacy fallback to a persisted list item, surviving
        // forever. Idempotent cleanup below runs on every Parse — safe
        // to call even when there's nothing to remove. Doesn't touch
        // real servers or subscription.servers (different field).
        if (string.Equals(settings.Vless.Server, "your.server.com", StringComparison.OrdinalIgnoreCase))
        {
            settings.Vless.Server = string.Empty;
            settings.Vless.Uuid = string.Empty;
            settings.Vless.Reality = new VlessRealityConfig();
        }
        if (string.Equals(settings.Vless.Uuid, "your-uuid-here", StringComparison.OrdinalIgnoreCase))
        {
            settings.Vless.Uuid = string.Empty;
        }
        settings.Vless.Servers.RemoveAll(s =>
            string.Equals(s.Server, "your.server.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Uuid,   "your-uuid-here",  StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(s.Server) && string.IsNullOrWhiteSpace(s.Uuid)));

        // Nested objects inside Tun
        settings.Tun.RouteExcludeAddress ??= new List<string>();

        // Update settings
        settings.Update ??= new UpdateSettings();

        // Ensure routing mode has a valid value
        if (string.IsNullOrWhiteSpace(settings.App.RoutingMode))
            settings.App.RoutingMode = "split";

        // Ensure theme has a valid value
        if (string.IsNullOrWhiteSpace(settings.App.Theme))
            settings.App.Theme = "light";

        // v2.24.0 schema migration: advance any older yaml to the current
        // schema version, persisting the upgraded form so the next load
        // starts clean. No-op for configs already at CurrentSchemaVersion.
        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            var old = settings.SchemaVersion;
            settings = SettingsMigrator.Migrate(
                settings,
                from: settings.SchemaVersion,
                to: AppSettings.CurrentSchemaVersion);
            // Persist upgraded form side-effectfully so we only migrate once.
            try { Save(settings); }
            catch { /* migration itself succeeded; re-save failure is non-fatal */ }
        }

        return settings;
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        // v2.24.2 HOTFIX: Safe Mode must be strictly read-only. Previously
        // Load() returned CreateDefaults() but some two-way binding in
        // the ViewModel caused Save() to fire with those defaults, which
        // overwrote the user's VLESS / subscriptions / CustomConfigs on
        // disk. Blocking Save() at the Core layer is the only reliable
        // way — we can't reliably audit every property binding that
        // might trigger persistence.
        if (SafeMode.Enabled)
            return;

        var configPath = path ?? DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        File.WriteAllText(configPath, serializer.Serialize(settings));
    }

    // ── v2.26.0 — live reload on config.yaml change ──────────────────────
    //
    // Service processes (VPNRouter.Service.exe) previously read config.yaml
    // once at startup and never again. If the user edited settings via the
    // desktop UI while the service held sing-box, the service's in-memory
    // settings went stale and any subsequent restart used old values.
    //
    // Watcher here solves that: FileSystemWatcher on the config.yaml
    // directory + 2 s debounce (file writes arrive as multiple Changed
    // events on Windows) + parse the file fresh each time and hand the
    // result to a caller-supplied callback. Caller decides what to do:
    // hot-reload via Clash API, restart sing-box, update a cached flag,
    // etc.
    //
    // Desktop UI process doesn't use this because it's the source of truth
    // for writes — watching one's own writes would just create a feedback
    // loop (each Save fires Changed → re-parse → potentially re-Save).
    //
    // Thread-safe against repeated StartWatching calls: disposes the old
    // watcher before creating a new one, so calling it twice doesn't leak
    // handles.

    private static FileSystemWatcher? _watcher;
    private static System.Timers.Timer? _debounceTimer;
    private static Action<AppSettings>? _reloadCallback;

    /// <summary>
    /// Begin watching the config file for external changes. Every write
    /// that lands from outside this process triggers <paramref name="onReload"/>
    /// with the freshly-parsed AppSettings. Safe to call multiple times —
    /// the last call wins. The watcher is active until <see cref="StopWatching"/>
    /// or until the process exits.
    /// </summary>
    public static void StartWatching(string? path = null, Action<AppSettings>? onReload = null)
    {
        StopWatching();

        var configPath = path ?? DefaultConfigPath;
        var dir = Path.GetDirectoryName(configPath);
        var file = Path.GetFileName(configPath);

        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
        if (!Directory.Exists(dir)) return;

        _reloadCallback = onReload;

        try
        {
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => ScheduleReload(configPath);
            _watcher.Created += (_, _) => ScheduleReload(configPath);
            _watcher.Renamed += (_, _) => ScheduleReload(configPath);
        }
        catch
        {
            // Watcher creation failed (permission denied, etc.) — non-fatal,
            // we just lose the live-reload feature on this run.
            _watcher = null;
        }
    }

    public static void StopWatching()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        try { _debounceTimer?.Stop(); _debounceTimer?.Dispose(); } catch { }
        _debounceTimer = null;
        _reloadCallback = null;
    }

    private static void ScheduleReload(string configPath)
    {
        try { _debounceTimer?.Stop(); _debounceTimer?.Dispose(); } catch { }

        _debounceTimer = new System.Timers.Timer(2000) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) =>
        {
            try
            {
                // Read-lock workaround: if the writer still holds an
                // exclusive handle when we try to parse, give up — the
                // next Changed event will re-schedule us.
                var settings = Load(configPath);
                _reloadCallback?.Invoke(settings);
            }
            catch { /* non-fatal: next change will retry */ }
        };
        _debounceTimer.Start();
    }

    /// <summary>
    /// v2.23.0 self-healing: reset user configuration to factory defaults.
    /// Current yaml is backed up (timestamped) before being overwritten,
    /// so the user can recover custom values if the reset turns out to
    /// be overkill.
    /// </summary>
    /// <returns>Path of the backup file that was created, or null if no
    /// prior config existed to back up.</returns>
    public static string? ResetToDefaults(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;
        string? backup = null;

        if (File.Exists(configPath))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            backup = $"{configPath}.backup-{stamp}";
            File.Copy(configPath, backup, overwrite: false);
        }

        Save(CreateDefaults(), configPath);
        return backup;
    }

    // ─── Defaults / Example ───────────────────────────────────────────────────

    private static AppSettings CreateDefaults() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            LogFile = Path.Combine(AppPaths.LogsDir, "vpnrouter.log")
        },
        ProfileSources = new List<ProfileSource>
        {
            new()
            {
                Type = "local",
                // v2.21.6: Linux gets its own profile (default-linux.json)
                // with bare Unix process names + wildcards (firefox*,
                // chromium-browser, telegram-desktop, etc). Before this the
                // Linux path loaded default.json with Windows-style .exe
                // names — MacProcessScanner stripped the .exe so it mostly
                // worked, but distro-specific names (firefox-bin,
                // firefox-esr) wouldn't match anything.
                Path = Path.Combine(AppPaths.ProfilesDir,
                    OperatingSystem.IsMacOS() ? "default-macos.json"
                    : OperatingSystem.IsLinux() ? "default-linux.json"
                    : "default.json")
            }
        },
        // v2.24.3: ActiveProfile defaults to empty; SimpleMode populates
        // it with the standard 8-group SimpleSplitProfile string on first
        // Start. Referencing an old 'Gaming_Full' group that doesn't
        // exist in the catalogue anymore forced the tolerant resolver to
        // kick in every fresh install.
        ActiveProfile = string.Empty,

        // v2.24.3: no placeholder VLESS fields. Previously CreateDefaults
        // wrote a fake "your.server.com" entry to the config, which
        // confused users after Safe Mode / reset flows ("where did my
        // server go and who is your.server.com?"). Now defaults are
        // blank — the user subscribes via URL in Simple mode or adds
        // a server manually in Servers tab. Engine already filters out
        // empty / your.server.com entries so both old and new configs
        // behave the same at runtime.
        Vless = new VlessConfig
        {
            Server = string.Empty,
            Port = 443,
            Uuid = string.Empty,
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = string.Empty,
                Fingerprint = "firefox",
                PublicKey = string.Empty,
                ShortId = string.Empty
            },
            Transport = new VlessTransportConfig
            {
                Type = "tcp",
                Path = "/"
            }
        },
        Tun = new TunSettings
        {
            InterfaceName = "VPNRouter-TUN",
            Ipv4Address = "172.19.0.1/30",
            Ipv6Enabled = false,
            Mtu = 9000,
            AutoRoute = true,
            StrictRoute = false
        },
        Dns = new DnsSettings
        {
            Strategy = "ipv4_only",
            VpnDns = "https://1.1.1.1/dns-query",
            LocalDns = "local"
        },
        SingBox = new SingBoxSettings
        {
            ExecutablePath = AppPaths.SingBoxExePath,
            AutoDownload = true
        },
        Monitoring = new MonitoringSettings
        {
            HealthCheckInterval = 30,
            RestartOnFailure = true,
            MaxRestartAttempts = 5,
            ProcessScanInterval = 60
        }
    };

    private static void WriteExample(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Save(settings, path);
    }
}
