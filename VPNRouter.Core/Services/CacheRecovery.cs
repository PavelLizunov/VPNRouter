using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VPNRouter.Core.Services;

public static class CacheRecovery
{
    internal sealed class SchemaProbe
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }
    }

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

        SchemaProbe? probe;
        try
        {
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

    private static void QuarantineFile(string filePath, string label, ILogger? logger)
    {
        try
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var target = $"{filePath}.corrupt-{ts}";
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

public sealed record CacheLoadResult<T>(T? Value, RecoveryReason Reason)
    where T : class
{
    public bool Loaded => Reason == RecoveryReason.Success && Value is not null;

    public bool ShouldRebuild =>
        Reason is RecoveryReason.SchemaMissing
            or RecoveryReason.SchemaMismatch
            or RecoveryReason.JsonMalformed
            or RecoveryReason.StructurallyInvalid;
}

public enum RecoveryReason
{
    Success = 0,

    NotFound = 1,

    SchemaMissing = 2,

    SchemaMismatch = 3,

    JsonMalformed = 4,

    StructurallyInvalid = 5,

    IoError = 6,
}
