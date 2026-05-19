using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.32.0 — schema-versioned recovery for on-disk JSON caches.
///
/// <para>Goal: never let a corrupt cache file (truncated mid-write,
/// schema-drifted across versions, JSON-malformed by a bad editor) silently
/// break a feature. Every cache load goes through this helper, which probes
/// a top-level <c>schema_version</c> integer, runs caller-supplied
/// deserialisation + structural validation, and quarantines anything that
/// fails by renaming to <c>{file}.corrupt-{yyyyMMdd-HHmmss}</c> so the
/// post-mortem trail survives.</para>
///
/// <para>Failure surfaces as a typed <see cref="RecoveryReason"/> the caller
/// can react to — e.g. <see cref="RecoveryReason.SchemaMismatch"/> on the
/// Free Configs cache means "trigger an immediate refresh from network",
/// while the same reason on <c>state.json</c> just means "treat as
/// no-running-instance".</para>
///
/// <para>The probe uses System.Text.Json regardless of which library wrote
/// the file: the only contract is that the JSON top-level object contains
/// a <c>schema_version</c> integer property. Each cache file's model class
/// uses its own attribute (<c>[JsonPropertyName]</c> for STJ,
/// <c>[JsonProperty]</c> for Newtonsoft) to emit that key on write.</para>
/// </summary>
public static class CacheRecovery
{
    /// <summary>
    /// Probe object — only carries the schema marker. Deliberately minimal
    /// so even a half-truncated JSON that happens to still contain the
    /// opening <c>{"schema_version":N</c> can be classified as "older than
    /// expected" rather than "malformed JSON".
    /// </summary>
    // Phase 7 Wave 34: flipped private → internal so AppJsonContext can
    // register [JsonSerializable(typeof(SchemaProbe))]. The type is still
    // implementation-only (the enclosing CacheRecovery is the only caller),
    // but [JsonSerializable] attributes require referenceable types from
    // the context's compilation unit, and internal-with-InternalsVisibleTo
    // is the cleanest way to share visibility without exposing the type
    // on the public surface.
    internal sealed class SchemaProbe
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }
    }

    /// <summary>
    /// Attempts to load and validate <paramref name="filePath"/>. On any
    /// validation failure (schema missing/older than expected, JSON
    /// malformed, deserialiser returned null, structural check failed),
    /// the file is quarantined as <c>{filePath}.corrupt-{yyyyMMdd-HHmmss}</c>
    /// and a typed reason is returned so the caller can trigger a rebuild.
    /// </summary>
    /// <typeparam name="T">Deserialised cache type (reference type).</typeparam>
    /// <param name="filePath">Absolute path to the cache file.</param>
    /// <param name="expectedSchemaVersion">
    /// The schema version this caller knows how to read. Files with a
    /// missing or strictly-lower <c>schema_version</c> are wiped. Equal or
    /// higher values pass through (forward-compat — newer schemas may
    /// add fields the caller ignores).
    /// </param>
    /// <param name="deserialize">
    /// Caller-supplied deserialiser, e.g. <c>j =&gt; JsonSerializer.Deserialize&lt;T&gt;(j)</c>.
    /// Exceptions are caught and translated to <see cref="RecoveryReason.JsonMalformed"/>;
    /// a null return is treated the same way.
    /// </param>
    /// <param name="structuralCheck">
    /// Optional post-deserialise predicate. Returns false if the parsed
    /// object is structurally wrong (e.g. mandatory list is null).
    /// </param>
    /// <param name="logger">Serilog logger for warnings; null = silent.</param>
    public static CacheLoadResult<T> LoadOrRecover<T>(
        string filePath,
        int expectedSchemaVersion,
        Func<string, T?> deserialize,
        Predicate<T>? structuralCheck = null,
        ILogger? logger = null)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath is required", nameof(filePath));
        if (deserialize is null)
            throw new ArgumentNullException(nameof(deserialize));

        if (!File.Exists(filePath))
            return new CacheLoadResult<T>(null, RecoveryReason.NotFound);

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            logger?.Warning(
                "CacheRecovery: read failed for {Path}: {Err}",
                filePath, ex.Message);
            return new CacheLoadResult<T>(null, RecoveryReason.IoError);
        }

        // Stage 1: schema_version probe via System.Text.Json. Strict parser
        // catches truncation early; if the JSON is malformed enough that
        // even the probe throws, we treat it as JsonMalformed.
        SchemaProbe? probe;
        try
        {
            // Phase 7 Wave 34: JsonTypeInfo<T> overload (AOT-clean).
            // NOTE: ProbeOptions had AllowTrailingCommas + ReadCommentHandling
            // that the context doesn't carry — but SchemaProbe is a single
            // int field. Trailing commas + comments would have applied only
            // if the schema_version key were nested in a JSON object with
            // sibling keys having those decorations, which doesn't happen in
            // practice (our cache files are STJ-emitted, never hand-edited).
            // If a future use case needs the lenient parse, switch back to
            // explicit options at this call site.
            probe = JsonSerializer.Deserialize(json, VPNRouter.Core.Json.AppJsonContext.Default.SchemaProbe);
        }
        catch (JsonException ex)
        {
            logger?.Warning(
                "CacheRecovery: malformed JSON in {Path}: {Err} — quarantining.",
                filePath, ex.Message);
            QuarantineFile(filePath, "json-malformed", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.JsonMalformed);
        }
        catch (Exception ex)
        {
            logger?.Warning(
                "CacheRecovery: probe failed for {Path}: {Err} — quarantining.",
                filePath, ex.Message);
            QuarantineFile(filePath, "probe-failed", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.JsonMalformed);
        }

        // Stage 2: schema_version sanity. probe == null happens when the
        // file deserialises as `null` (e.g. literally "null" written by a
        // bug); treat as schema-missing so the caller gets a clean restart.
        if (probe is null || probe.SchemaVersion <= 0)
        {
            logger?.Warning(
                "CacheRecovery: {Path} missing schema_version (or <= 0) — wiping; expected v{Expected}.",
                filePath, expectedSchemaVersion);
            QuarantineFile(filePath, "schema-missing", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.SchemaMissing);
        }
        if (probe.SchemaVersion < expectedSchemaVersion)
        {
            logger?.Warning(
                "CacheRecovery: {Path} schema_version v{Got} older than expected v{Expected} — wiping.",
                filePath, probe.SchemaVersion, expectedSchemaVersion);
            QuarantineFile(filePath, $"schema-mismatch-v{probe.SchemaVersion}", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.SchemaMismatch);
        }

        // Stage 3: caller's deserialiser. Wrap in try/catch so even if the
        // probe accepted the JSON the caller's stricter parser can still
        // reject it (e.g. enum out-of-range, required field missing).
        T? value;
        try
        {
            value = deserialize(json);
        }
        catch (Exception ex)
        {
            logger?.Warning(
                "CacheRecovery: deserialiser threw on {Path}: {Err} — quarantining.",
                filePath, ex.Message);
            QuarantineFile(filePath, "deserialize-failed", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.JsonMalformed);
        }

        if (value is null)
        {
            logger?.Warning(
                "CacheRecovery: deserialiser returned null for {Path} — quarantining.",
                filePath);
            QuarantineFile(filePath, "deserialize-null", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.JsonMalformed);
        }

        // Stage 4: optional structural validation. Catches "JSON parsed
        // fine but the object is obviously wrong", e.g. entries: null when
        // the schema mandates a list.
        if (structuralCheck is not null && !structuralCheck(value))
        {
            logger?.Warning(
                "CacheRecovery: structural check failed for {Path} — quarantining.",
                filePath);
            QuarantineFile(filePath, "structurally-invalid", logger);
            return new CacheLoadResult<T>(null, RecoveryReason.StructurallyInvalid);
        }

        return new CacheLoadResult<T>(value, RecoveryReason.Success);
    }

    /// <summary>
    /// Renames <paramref name="filePath"/> to a timestamped <c>.corrupt</c>
    /// sibling so the original payload survives for post-mortem. Falls back
    /// to a hard delete if the rename fails (e.g. filesystem doesn't allow
    /// the target name) — better to drop a single bad cache than to leave
    /// the loop hot.
    /// </summary>
    private static void QuarantineFile(string filePath, string label, ILogger? logger)
    {
        try
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var target = $"{filePath}.corrupt-{ts}";
            // If two corruptions happen in the same second (e.g. unit test
            // bursts), uniquify with a counter so the first backup isn't
            // clobbered.
            int suffix = 1;
            while (File.Exists(target))
            {
                target = $"{filePath}.corrupt-{ts}-{suffix++}";
            }
            File.Move(filePath, target);
            logger?.Information(
                "CacheRecovery: quarantined {Path} → {Target} ({Reason})",
                filePath, target, label);
        }
        catch (Exception ex)
        {
            logger?.Warning(
                "CacheRecovery: failed to quarantine {Path}: {Err} — deleting outright.",
                filePath, ex.Message);
            try { if (File.Exists(filePath)) File.Delete(filePath); }
            catch (Exception delEx)
            {
                logger?.Error(
                    "CacheRecovery: failed to delete {Path} after failed quarantine: {Err}",
                    filePath, delEx.Message);
            }
        }
    }
}

/// <summary>
/// Outcome of a <see cref="CacheRecovery.LoadOrRecover{T}"/> call. Callers
/// can branch on <see cref="Reason"/> to decide whether a corrupt cache
/// warrants an immediate rebuild from upstream or a graceful fallback.
/// </summary>
public sealed record CacheLoadResult<T>(T? Value, RecoveryReason Reason)
    where T : class
{
    /// <summary>
    /// True when <see cref="Value"/> is populated and validated. Note that
    /// <see cref="RecoveryReason.NotFound"/> also returns a null Value but
    /// represents a clean first-launch state, not corruption.
    /// </summary>
    public bool Loaded => Reason == RecoveryReason.Success && Value is not null;

    /// <summary>
    /// True when the cache was missing, corrupt, or schema-mismatched —
    /// any case where the caller should consider rebuilding from source.
    /// </summary>
    public bool ShouldRebuild =>
        Reason is RecoveryReason.SchemaMissing
            or RecoveryReason.SchemaMismatch
            or RecoveryReason.JsonMalformed
            or RecoveryReason.StructurallyInvalid;
}

/// <summary>Why a cache load did or didn't succeed.</summary>
public enum RecoveryReason
{
    /// <summary>Cache loaded and validated — value is usable.</summary>
    Success = 0,

    /// <summary>File didn't exist on disk. Not corruption — a fresh install.</summary>
    NotFound = 1,

    /// <summary>
    /// File present but <c>schema_version</c> is missing or zero. Indicates
    /// either a pre-v2.32.0 cache (one-time migration) or a malformed
    /// write. Backed up + wiped.
    /// </summary>
    SchemaMissing = 2,

    /// <summary>
    /// File present, <c>schema_version</c> readable, but lower than the
    /// caller's expected version. Backed up + wiped.
    /// </summary>
    SchemaMismatch = 3,

    /// <summary>
    /// File present but failed JSON parsing (truncation, encoding error,
    /// hand-edit gone wrong). Backed up + wiped.
    /// </summary>
    JsonMalformed = 4,

    /// <summary>
    /// JSON parsed cleanly, but the typed object failed the caller-supplied
    /// structural predicate (e.g. required list is null). Backed up + wiped.
    /// </summary>
    StructurallyInvalid = 5,

    /// <summary>
    /// Underlying filesystem read failed (permission, sharing violation,
    /// disk error). The file is left in place — retry on next launch.
    /// </summary>
    IoError = 6,
}
