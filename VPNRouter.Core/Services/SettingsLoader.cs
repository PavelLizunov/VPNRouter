using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VPNRouter.Core.Models;
using VPNRouter.Core.Yaml;

namespace VPNRouter.Core.Services;

public static class SettingsLoader
{
    private static readonly string DefaultConfigPath = AppPaths.ConfigYamlPath;

    public static string? LastRecoveryNotice { get; private set; }

    public static string? ConsumeRecoveryNotice()
    {
        var notice = LastRecoveryNotice;
        LastRecoveryNotice = null;
        return notice;
    }

    internal static AppSettings Load(string? path = null)
    {
        try
        {
            return LoadCore(path);
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"[SettingsLoader] Load failed ({ex.GetType().Name}). Using defaults.");
            }
            catch { }
            return CreateDefaults().EnsureSane();
        }
    }

    private static AppSettings LoadCore(string? path)
    {
        var configPath = path ?? DefaultConfigPath;

        if (SafeMode.Enabled)
            return CreateDefaults().EnsureSane();

        if (!File.Exists(configPath))
            return CreateDefaultsAndWriteExample(configPath);

        if (!TryReadConfig(configPath, out var yaml))
            return CreateDefaults().EnsureSane();

        AppSettings parsed;
        try
        {
            parsed = Parse(yaml);
        }
        catch (Exception parseEx)
        {
            return RecoverFromUnparsableConfig(configPath, parseEx);
        }

        var validation = SettingsValidator.Validate(parsed);
        if (validation.Warnings.Count > 0)
            Console.Error.WriteLine($"[SettingsValidator] Warnings: {validation.Warnings.Count}");
        if (!validation.IsValid)
            return RecoverFromInvalidConfig(configPath, validation);

        PersistClashApiSecretIfMissing(parsed, yaml, configPath);
        LogLoadedSummary(parsed);

        return parsed;
    }

    private static AppSettings CreateDefaultsAndWriteExample(string configPath)
    {
        var defaults = CreateDefaults().EnsureSane();
        try { WriteExample(configPath, defaults); }
        catch (Exception writeEx)
        {
            try
            {
                Console.Error.WriteLine(
                    $"[SettingsLoader] Could not write example settings ({writeEx.GetType().Name}). Using in-memory defaults.");
            }
            catch { }
        }
        return defaults;
    }

    private static bool TryReadConfig(string configPath, out string yaml)
    {
        try
        {
            yaml = File.ReadAllText(configPath);
        }
        catch (Exception readEx)
        {
            try
            {
                Console.Error.WriteLine(
                    $"[SettingsLoader] Could not read settings ({readEx.GetType().Name}). Using session defaults.");
            }
            catch { }
            yaml = string.Empty;
            return false;
        }
        return true;
    }

    private static AppSettings RecoverFromUnparsableConfig(string configPath, Exception parseEx)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{configPath}.unloadable-{stamp}";
            File.Move(configPath, backup, overwrite: false);
            Console.Error.WriteLine(
                $"[SettingsLoader] Settings parse failed ({parseEx.GetType().Name}). Backup saved. Using session defaults.");
            LastRecoveryNotice =
                $"config.yaml parse failed ({parseEx.GetType().Name}); restored defaults. Backup: {backup}";
        }
        catch
        {
            LastRecoveryNotice =
                $"config.yaml parse failed ({parseEx.GetType().Name}); restored defaults.";
        }
        return CreateDefaults().EnsureSane();
    }

    private static AppSettings RecoverFromInvalidConfig(string configPath, SettingsValidationResult validation)
    {
        var reasonsJoined = string.Join("; ", validation.Reasons);
        string? backup = null;
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            backup = $"{configPath}.invalid-{stamp}";
            File.Move(configPath, backup, overwrite: false);
        }
        catch
        {
            backup = null;
        }

        var defaults = CreateDefaults();
        try { Save(defaults, configPath); }
        catch { }

        Console.Error.WriteLine(
            $"[SettingsValidation] Settings rejected. Errors: {validation.Reasons.Count}, backup saved: {backup != null}. Using defaults.");
        LastRecoveryNotice = backup != null
            ? $"config.yaml was invalid ({reasonsJoined}); restored defaults. Backup: {backup}"
            : $"config.yaml was invalid ({reasonsJoined}); restored defaults.";
        return defaults;
    }

    private static void PersistClashApiSecretIfMissing(AppSettings parsed, string yaml, string configPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(parsed.SingBox?.ClashApiSecret) &&
                !yaml.Contains("clash_api_secret:", StringComparison.Ordinal))
            {
                Save(parsed, configPath);
            }
        }
        catch { }
    }

    private static void LogLoadedSummary(AppSettings parsed)
    {
        try
        {
            var mode = parsed.App.ConfigMode.Trim().ToLowerInvariant();
            var summary = $"[SettingsLoader] Loaded schema={parsed.SchemaVersion}, config_mode={mode}, " +
                $"subscriptions={parsed.App.Subscriptions.Count}, vless.servers={parsed.Vless.Servers.Count}";
            Console.Error.WriteLine(summary);
            Serilog.Log.Information("{SettingsSummary:l}", summary);
        }
        catch { }
    }

    internal static AppSettings Copy(AppSettings settings) => Deserialize(Serialize(settings));

    internal static string GetSubmittedRevision(AppSettings settings) => GetRevision(Serialize(settings));

    internal static string GetRevision(string yaml) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml)));

    internal static AppSettings? ParseLiveCandidate(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try
        {
            var candidate = Parse(yaml, persistMigrations: false);
            return SettingsValidator.Validate(candidate).IsValid ? candidate : null;
        }
        catch (Exception) { return null; }
    }

    private static AppSettings Deserialize(string yaml) =>
        new StaticDeserializerBuilder(new YamlStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build().Deserialize<AppSettings>(yaml);

    private static string Serialize(AppSettings settings) =>
        new StaticSerializerBuilder(new YamlStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlConverter())
            .Build().Serialize(settings);

    public static AppSettings Parse(string yaml) => Parse(yaml, persistMigrations: true);

    private static AppSettings Parse(string yaml, bool persistMigrations)
    {
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

                    var knownKeys = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "schema_version", "app", "profile_sources", "active_profile",
                        "vless", "tun", "dns", "singbox", "monitoring",
                        "custom_apps", "custom_group_apps", "custom_categories", "update",
                        "emergency_channel",
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
                throw new InvalidDataException($"config.yaml is not valid YAML: {ex.Message}", ex);
            }
        }

        var settings = Deserialize(yaml);
        settings = settings.EnsureSane();

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

        settings.App.RoutingMode = string.IsNullOrWhiteSpace(settings.App.RoutingMode)
            ? "split"
            : settings.App.RoutingMode.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(settings.App.Theme))
            settings.App.Theme = "light";

        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            var old = settings.SchemaVersion;
            settings = SettingsMigrator.Migrate(
                settings,
                from: settings.SchemaVersion,
                to: AppSettings.CurrentSchemaVersion);
            if (persistMigrations)
            {
                try { Save(settings); } catch { }
            }
        }

        var pruneCount = SettingsMigrator.PruneKnownPlaceholders(settings, null);
        if (pruneCount > 0)
        {
            settings.App.PlaceholderPruneCount = pruneCount;
            settings.App.PlaceholderPruneAtUtc_Str =
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (persistMigrations)
            {
                try { Save(settings); } catch { }
            }
        }

        return settings;
    }

    public static (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings)
    {
        if (settings?.App == null) return (0, string.Empty);
        var count = settings.App.PlaceholderPruneCount;
        var at = settings.App.PlaceholderPruneAtUtc_Str ?? string.Empty;
        settings.App.PlaceholderPruneCount = 0;
        settings.App.PlaceholderPruneAtUtc_Str = string.Empty;
        return (count, at);
    }

    internal static object FileWriteLock { get; } = new();

    internal static void Save(AppSettings settings, string? path = null)
    {
        lock (FileWriteLock)
            SaveCore(settings, path);
    }

    private static void SaveCore(AppSettings settings, string? path)
    {
        if (SafeMode.Enabled)
            return;

        var configPath = path ?? DefaultConfigPath;
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && Path.GetFullPath(configPath) == Path.GetFullPath(AppPaths.ConfigYamlPath))
        {
            AppPaths.EnsurePrivateUnixDirectory(AppPaths.DataDir);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        }

        var yaml = Serialize(settings);

        // Temp file, fsync, atomic rename: a crash mid-write must not truncate config.yaml.
        var tmp = configPath + ".tmp";
        try
        {
            using (var stream = AppPaths.CreatePrivateFile(tmp))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(yaml);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, configPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static FileSystemWatcher? _watcher;
    private static System.Timers.Timer? _debounceTimer;
    private static Action<AppSettings?, string>? _reloadCallback;

    public static void StartWatching(string? path = null, Action<AppSettings>? onReload = null) =>
        WatchRevisions(path, (settings, _) => { if (settings != null) onReload?.Invoke(settings); });

    internal static void WatchRevisions(string? path, Action<AppSettings?, string> onReload)
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
                var yaml = File.ReadAllText(configPath);
                var settings = ParseLiveCandidate(yaml);
                _reloadCallback?.Invoke(settings, GetRevision(yaml));
            }
            catch { }
        };
        _debounceTimer.Start();
    }

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
                Path = Path.Combine(AppPaths.ProfilesDir,
                    OperatingSystem.IsMacOS() ? "default-macos.json"
                    : OperatingSystem.IsLinux() ? "default-linux.json"
                    : "default.json")
            }
        },
        ActiveProfile = string.Empty,

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
            Mtu = TunSettings.DefaultMtu,
            AutoRoute = true,
            StrictRoute = false
        },
        Dns = new DnsSettings
        {
            Strategy = "ipv4_only",
            VpnDns = "https://8.8.8.8/dns-query",
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
