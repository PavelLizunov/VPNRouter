using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VPNRouter.Core.Models;
using VPNRouter.Core.Yaml;

namespace VPNRouter.Core.Services;

public static class SettingsLoader
{
    private static readonly string DefaultConfigPath = AppPaths.ConfigYamlPath;

    /// v2.32.0 — populated by <see cref="Load"/> when SR-4 (corrupt YAML)
    /// or SR-1 <see cref="SettingsValidator.Validate"/> rejected a parsed
    /// settings tree and we wrote fresh defaults instead. Holds a
    /// short human-readable summary (validation reasons + backup path)
    /// so the UI / Service layer can surface it once after startup
    /// — desktop App as a dismissible banner, Windows Service as an
    /// EventLog entry. Cleared after a single read; null otherwise.
    ///
    /// <para>Static-singleton lifetime is fine because <see cref="Load"/>
    /// is called early at process start, well before any concurrent
    /// readers; subsequent in-process loads (config-file watcher
    /// reload, ResetToDefaults) intentionally don't repopulate this so
    /// we don't spam the user with stale toast banners.</para>
    /// </summary>
    public static string? LastRecoveryNotice { get; private set; }

    /// <summary>
    /// One-shot read accessor — atomically returns the current notice
    /// and clears it so subsequent callers don't re-surface the same
    /// banner. Used by App + Service after their first <see cref="Load"/>.
    /// </summary>
    public static string? ConsumeRecoveryNotice()
    {
        var notice = LastRecoveryNotice;
        LastRecoveryNotice = null;
        return notice;
    }

    /// <summary>
    /// Load and parse <c>config.yaml</c>. Guaranteed to never throw —
    /// any failure (file not found, IO error, YAML parse error,
    /// type-coercion failure, anything unexpected) falls through to a
    /// fully-defaulted, sanity-checked <see cref="AppSettings"/>
    /// instance. Unloadable files are backed up as
    /// <c>config.yaml.unloadable-{timestamp}</c> for forensic recovery.
    ///
    /// <para>SR-4 (v2.32.0): outer try/catch wrapper +
    /// <see cref="AppSettingsSane.EnsureSane"/> close the "Load throws
    /// → app dies at launch" gap that bit users on v2.31.8-r9.
    /// SR-1 (v2.32.0): layers semantic validation
    /// (<see cref="SettingsValidator.Validate"/>) on top of the
    /// structurally-safe object — populates <see cref="LastRecoveryNotice"/>
    /// so callers can surface the recovery once.</para>
    ///
    /// <para><b>Phase 6 (v3.0 refactor):</b> demoted from <c>public</c>
    /// to <c>internal</c>. Phase 4 Wave 19 marked the API
    /// <see cref="ObsoleteAttribute"/> after migrating production
    /// callers to <see cref="ISettingsStore.Load"/> via ctor injection.
    /// Phase 5 Wave 24 confirmed zero external callers but kept the
    /// marker at <c>error: false</c> because CS0619 (obsolete-as-error)
    /// is not <c>#pragma warning disable</c>-suppressible — and the
    /// in-assembly delegation site (<see cref="RealSettingsStore.Load"/>)
    /// + the two pin-suite test classes legitimately need to keep
    /// calling here. Phase 6 closes the loop by dropping
    /// <c>public</c>+<c>[Obsolete]</c> and going <c>internal</c>:
    /// same-assembly callers in <c>VPNRouter.Core</c> see it directly,
    /// the test project gets access via the
    /// <c>InternalsVisibleTo("VPNRouter.Tests")</c> friend-assembly
    /// declaration, and the six <c>#pragma warning disable CS0618</c>
    /// blocks scattered around the loader + delegation become dead
    /// noise and get deleted.</para>
    /// </summary>
    internal static AppSettings Load(string? path = null)
    {
        try
        {
            return LoadCore(path);
        }
        catch (Exception ex)
        {
            // Last-resort safety net: LoadCore already catches every
            // expected failure mode (read error, parse error, migration
            // error). If something still propagates here, log and return
            // pure defaults rather than crashing the host process.
            try
            {
                Console.Error.WriteLine(
                    $"[SettingsLoader] FATAL: Load(\"{path ?? DefaultConfigPath}\") threw " +
                    $"{ex.GetType().Name}: {ex.Message}. Returning defaults.");
            }
            catch { /* even logging failed — swallow */ }
            return CreateDefaults().EnsureSane();
        }
    }

    private static AppSettings LoadCore(string? path)
    {
        var configPath = path ?? DefaultConfigPath;

        // v2.23.0 self-healing: --safe flag skips parsing user yaml
        // and returns the pure defaults. Settings on disk stay intact
        // (so next normal launch picks them up), but the current
        // process sees a clean slate.
        if (SafeMode.Enabled)
            return CreateDefaults().EnsureSane();

        if (!File.Exists(configPath))
        {
            // Write example config and return defaults. WriteExample
            // failure is non-fatal — defaults are still valid for this
            // session and the next save will persist them.
            var defaults = CreateDefaults().EnsureSane();
            try { WriteExample(configPath, defaults); }
            catch (Exception writeEx)
            {
                try
                {
                    Console.Error.WriteLine(
                        $"[SettingsLoader] could not write example config to {configPath} " +
                        $"({writeEx.GetType().Name}: {writeEx.Message}); using in-memory defaults.");
                }
                catch { }
            }
            return defaults;
        }

        // SR-4: separate the read step from the parse step so we can
        // distinguish "file is unreadable" (don't backup — we can't read
        // it) from "file parsed badly" (backup and defaults).
        string yaml;
        try
        {
            yaml = File.ReadAllText(configPath);
        }
        catch (Exception readEx)
        {
            // File locked by another process / permission denied /
            // encoding read failure / OOM on huge file. Log and return
            // defaults; do NOT touch the file on disk so the next
            // launch can retry.
            try
            {
                Console.Error.WriteLine(
                    $"[SettingsLoader] could not read {configPath} " +
                    $"({readEx.GetType().Name}: {readEx.Message}); " +
                    "using defaults for this session, original file untouched.");
            }
            catch { }
            return CreateDefaults().EnsureSane();
        }

        // v2.31.8-r9 / SR-4 — graceful fallback on corrupt YAML. Pre-r9
        // a malformed config.yaml (truncated mid-write, manual edit
        // mistake, accidental BOM, etc.) propagated InvalidDataException
        // up through MainWindowViewModel..ctor and crashed App.exe at
        // launch. User-facing symptom: «приложение не запускается»,
        // no UI, no banner, no clue. SR-4 widens the catch from
        // "InvalidDataException-like things" to "any exception" — now
        // YamlException, FormatException (type-coercion), duplicate-key
        // errors, and anything else are all backed up + defaulted.
        // Forensic copy lands at config.yaml.unloadable-{ts} so the
        // user can recover specific values if they want.
        //
        // SR-1 needs `parsed` available outside the try-block to feed
        // SettingsValidator.Validate downstream — Parse() already calls
        // EnsureSane() internally so we don't double-wrap.
        AppSettings parsed;
        try
        {
            parsed = Parse(yaml);
        }
        catch (Exception parseEx)
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var backup = $"{configPath}.unloadable-{stamp}";
                File.Move(configPath, backup, overwrite: false);
                Console.Error.WriteLine(
                    $"[SettingsLoader] config.yaml unloadable ({parseEx.GetType().Name}: {parseEx.Message}). " +
                    $"Renamed to {backup}; using defaults for this session.");
                LastRecoveryNotice =
                    $"config.yaml parse failed ({parseEx.GetType().Name}); restored defaults. Backup: {backup}";
            }
            catch
            {
                // If we can't rename (locked / permission), fall through
                // anyway — better defaults-with-no-backup than crash.
                LastRecoveryNotice =
                    $"config.yaml parse failed ({parseEx.GetType().Name}); restored defaults.";
            }
            return CreateDefaults().EnsureSane();
        }

        // v2.32.0 — semantic validation pass. Catches structurally-valid
        // but semantically-broken configs that YamlDotNet happily maps
        // (typoed config_mode, port out of range, malformed subscription
        // URL, etc.). Validator runs AFTER migration so v1→v2 schema
        // changes can't false-positive. Soft warnings are logged to
        // Console.Error for ops visibility; only fatal reasons trigger
        // backup+reset.
        var validation = SettingsValidator.Validate(parsed);
        foreach (var w in validation.Warnings)
        {
            Console.Error.WriteLine($"[SettingsValidator] warning: {w}");
        }
        if (!validation.IsValid)
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
                // Backup failed (locked / permission). We still reset so
                // the user gets a working app, just without a forensic copy.
                backup = null;
            }

            var defaults = CreateDefaults();
            try { Save(defaults, configPath); }
            catch { /* save failure is non-fatal — caller still gets the in-memory defaults */ }

            var noticeLine = backup != null
                ? $"[SettingsValidation] config.yaml rejected: {reasonsJoined}; backup at {backup}; reset to defaults"
                : $"[SettingsValidation] config.yaml rejected: {reasonsJoined}; reset to defaults (backup failed)";
            Console.Error.WriteLine(noticeLine);
            LastRecoveryNotice = backup != null
                ? $"config.yaml was invalid ({reasonsJoined}); restored defaults. Backup: {backup}"
                : $"config.yaml was invalid ({reasonsJoined}); restored defaults.";
            return defaults;
        }

        // BR-3 (brat 2026-05-19) — single-line diagnostic snapshot of the
        // post-parse, post-migration, post-validation state. Helps future
        // user-report investigations see the exact AppSettings shape r6
        // worked with without needing the actual config.yaml file. The
        // shape mirrors brat's r5 mystery: schema=4 vs 5, sub count + per-
        // sub server count, manual Vless.Servers count, legacy Vless.Server
        // scalar presence, config_mode.
        //
        // Writes to BOTH Serilog (so it surfaces in vpnrouter*.log that
        // users share) AND Console.Error (so it surfaces in CLI / Service
        // host stdout when Serilog isn't initialised yet at the call site).
        try
        {
            var subSummary = parsed.App?.Subscriptions == null || parsed.App.Subscriptions.Count == 0
                ? "none"
                : string.Join(",", parsed.App.Subscriptions.Select(s =>
                    $"{(s == null ? "?" : (s.Enabled ? "+" : "-"))}{(s?.Servers?.Count ?? 0)}"));
            var legacyVless = string.IsNullOrWhiteSpace(parsed.Vless?.Server) ? "empty" : "set";
            var line =
                $"[SettingsLoader] Loaded {configPath}: schema={parsed.SchemaVersion}, " +
                $"config_mode={parsed.App?.ConfigMode ?? "(null)"}, " +
                $"subs={parsed.App?.Subscriptions?.Count ?? 0}[{subSummary}], " +
                $"vless.servers={parsed.Vless?.Servers?.Count ?? 0}, " +
                $"vless.server={legacyVless}, " +
                $"active_sub='{parsed.App?.ActiveSubscriptionServer ?? string.Empty}', " +
                $"active_vless='{parsed.Vless?.ActiveServer ?? string.Empty}'";
            Console.Error.WriteLine(line);
            // Mirror to Serilog if it's been initialised. Guard with a
            // null-conditional on Logger.Information — if Serilog isn't
            // wired yet (which is rare; Program.cs wires it before
            // anything else loads settings), Information is a no-op.
            try { Serilog.Log.Logger?.Information(line); }
            catch { /* Serilog not initialised — Console.Error has it */ }
        }
        catch
        {
            // Diagnostic only — must never block load.
        }

        return parsed;
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
                        "custom_apps", "custom_group_apps", "custom_categories", "update",
                        "emergency_channel",
                        // AM-1 schema-v3 keys are nested under `app:` so they're
                        // not top-level — listed here for documentation and so
                        // a future top-level move stays cheap.
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

        // Phase 6 Wave 31a (2026-05-18): StaticDeserializerBuilder + the
        // analyzer-generated YamlStaticContext close the last two reflective
        // YamlDotNet paths (IL3050 dynamic-code warnings) from the Wave 30
        // NativeAOT readiness audit. Brief: plans/phase6-yamldotnet-staticgen-
        // 2026-05-18.md. Behaviour is equivalent to the prior reflective
        // builder — round-trip tests in YamlStaticContextRoundTripTests pin
        // wire-format compatibility.
        //
        // DateTimeOffset shim: Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2
        // does not handle DateTimeOffset / DateTimeOffset? out of the box
        // (emits {} on serialize, throws on deserialize). DateTimeOffsetYamlConverter
        // restores parity with the reflective builder — see file header on
        // the converter class for the retirement criteria.
        var deserializer = new StaticDeserializerBuilder(new YamlStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        var settings = deserializer.Deserialize<AppSettings>(yaml);

        // YamlDotNet returns null for empty/whitespace YAML (caller expects
        // defaults on first launch). Real content reaching this path with
        // null would be unusual now that the mapping check above has run,
        // but belt-and-braces: keep the fallback so we never crash here.
        // EnsureSane tolerates a null receiver and returns a fresh
        // default-initialised instance.
        //
        // SR-4 (v2.32.0): EnsureSane replaced ~12 inline ??= assignments
        // and extends coverage to every reference-typed property on the
        // AppSettings tree (Subscriptions, CustomConfigs, CustomRules,
        // CustomGroupApps, per-entry VlessServerEntry sub-objects, etc.).
        // Idempotent — safe to call again later.
        settings = settings.EnsureSane();

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

        // (Tun.RouteExcludeAddress, Update — handled by EnsureSane above.)

        // Ensure routing mode has a clean, canonical value. v2.40.0-r9 (#3 core-audit):
        // a hand-edited config.yaml with `routing_mode: ' full '` (stray whitespace)
        // previously survived untrimmed → the exact-match compares in ConfigGenerator /
        // LeakProtection saw it as NOT "full" → full-tunnel SILENTLY degraded to
        // include-split = everything direct on the real IP, with no warning. Trim +
        // lower at the source so every downstream comparison sees the clean value.
        settings.App.RoutingMode = string.IsNullOrWhiteSpace(settings.App.RoutingMode)
            ? "split"
            : settings.App.RoutingMode.Trim().ToLowerInvariant();

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

        // v2.32.3 (2026-05-17): aggressive one-shot wipe of known-bad
        // placeholder credentials (Reality pubkey `DnT9hI…` and friends)
        // that leaked from old Android smoke-test code and survived in
        // real user configs for weeks. The F-A / F-D / F-E layers catch
        // it at start/validate/runtime, but only this loader-side prune
        // actually erases the bytes from disk so the dead entry stops
        // appearing in the Servers tab. Idempotent — re-running on
        // already-clean state is a 0-cost no-op.
        var pruneCount = SettingsMigrator.PruneKnownPlaceholders(settings, null);
        if (pruneCount > 0)
        {
            settings.App.PlaceholderPruneCount = pruneCount;
            settings.App.PlaceholderPruneAtUtc_Str =
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            // Persist the cleaned form immediately so the user doesn't
            // see the pruned entries on next restart. Mirror the schema-
            // migrator save pattern above — best-effort, non-fatal on
            // write failure (the in-memory tree is still clean).
            try { Save(settings); }
            catch { /* re-save failure is non-fatal — in-memory clean wins this session */ }
        }

        return settings;
    }

    /// <summary>
    /// v2.32.3 — counterpart accessor for <see cref="AppConfig.PlaceholderPruneCount"/>:
    /// returns the current value alongside the timestamp and clears the
    /// in-memory pair so the UI banner surfaces only once per launch.
    /// The persisted yaml fields stay populated (they're forensic) until
    /// the next save naturally rewrites them — callers that want the
    /// banner suppressed on subsequent runs should call this method
    /// AND then trigger an explicit <see cref="Save"/>.
    /// </summary>
    public static (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings)
    {
        if (settings?.App == null) return (0, string.Empty);
        var count = settings.App.PlaceholderPruneCount;
        var at = settings.App.PlaceholderPruneAtUtc_Str ?? string.Empty;
        settings.App.PlaceholderPruneCount = 0;
        settings.App.PlaceholderPruneAtUtc_Str = string.Empty;
        return (count, at);
    }

    /// <summary>
    /// Persist <paramref name="settings"/> to <paramref name="path"/> as
    /// YAML. <see cref="SafeMode"/> bypasses the actual write entirely.
    ///
    /// <para><b>Phase 6 (v3.0 refactor):</b> demoted from <c>public</c>
    /// to <c>internal</c>; same retirement timeline as <see cref="Load"/>.
    /// Production callers go through <see cref="ISettingsStore.Save"/>
    /// (DI); same-assembly callers (in-file uses, <see cref="RealSettingsStore"/>
    /// delegation, pin-suite tests via <c>InternalsVisibleTo</c>) see
    /// the internal API directly.</para>
    /// </summary>
    internal static void Save(AppSettings settings, string? path = null)
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

        // Phase 6 Wave 31a (2026-05-18): StaticSerializerBuilder twin of the
        // Parse() swap above — uses the same YamlStaticContext to avoid the
        // reflective IObjectGraphVisitor walk. AOT-clean, no behaviour
        // change vs the SerializerBuilder it replaces. DateTimeOffsetYamlConverter
        // is the same compat shim wired into Parse() above; without it, the
        // static serializer emits `{}` for both DateTimeOffset and DateTimeOffset?
        // properties (silently lossy round-trip).
        var serializer = new StaticSerializerBuilder(new YamlStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlConverter())
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
