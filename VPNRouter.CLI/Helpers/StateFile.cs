#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.CLI.Helpers;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

/// <summary>
/// Persists running state so stop/status commands can find the sing-box PID
/// even from a different CLI invocation.
///
/// <para>Phase 4 (2026-05-18) — migrated from Newtonsoft.Json
/// <c>[JsonProperty]</c> + <c>JsonConvert</c> to System.Text.Json. The on-
/// disk wire format is preserved byte-for-byte: <c>schema_version</c> is
/// the only [JsonPropertyName]-pinned key (matches the pre-migration
/// Newtonsoft <c>[JsonProperty("schema_version")]</c> contract); the
/// other fields stay PascalCase (Newtonsoft's default conversion for un-
/// annotated properties — STJ does the same with no naming policy set).
/// <c>PropertyNameCaseInsensitive=true</c> in <see cref="StateFile.Options"/>
/// keeps any user-edited state.json (rare but possible) parseable.</para>
/// </summary>
public class RunState
{
    /// <summary>
    /// v2.32.0 — schema marker. Bumped whenever the on-disk shape changes
    /// in a non-backward-compatible way; older state files are quarantined
    /// and rebuilt by <see cref="CacheRecovery"/>.
    /// </summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = StateFile.CurrentSchemaVersion;

    public string ActiveProfile { get; set; } = string.Empty;
    public int SingBoxPid { get; set; }
    public DateTime StartedAt { get; set; }
    public List<string> ProcessNames { get; set; } = new();
}

public static class StateFile
{
    /// <summary>
    /// v2.32.0 — current state.json schema version. A wrong/missing
    /// <c>schema_version</c> on load is treated as "no running instance"
    /// (i.e. <see cref="Read"/> returns null).
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    // Phase 7 Wave 34 (2026-05-19): retired the local Options field.
    // Both Write/Read now use the JsonTypeInfo<RunState> overload directly
    // against CliJsonContext.Default. WriteIndented + PropertyNameCaseInsensitive
    // moved to CliJsonContext's [JsonSourceGenerationOptions] (Wave 34
    // change). Wire format identical.

    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter"),
            "state.json");

    public static void Write(RunState state)
    {
        // Stamp the current schema on every write — covers callers that
        // constructed RunState externally without touching the property.
        state.SchemaVersion = CurrentSchemaVersion;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(state, CliJsonContext.Default.RunState));
    }

    public static RunState? Read()
    {
        // state.json is consulted by stop/status from a fresh CLI process,
        // so a corrupt file blocking the workflow would be especially
        // painful — quarantine and treat as "no running instance".
        var result = CacheRecovery.LoadOrRecover<RunState>(
            Path,
            CurrentSchemaVersion,
            json => JsonSerializer.Deserialize(json, CliJsonContext.Default.RunState),
            structuralCheck: null,
            logger: null);

        return result.Loaded ? result.Value : null;
    }

    public static void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
