#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using VPNRouter.CLI.Helpers;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

/// <summary>
/// Persists running state so stop/status commands can find the exact CLI run
/// and sing-box child from a different invocation.
/// </summary>
public class RunState
{
    /// <summary>
    /// v2.32.0 — schema marker. Additive optional fields keep schema 1 readable;
    /// incompatible shape changes must bump this value.
    /// </summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = StateFile.CurrentSchemaVersion;

    public string ActiveProfile { get; set; } = string.Empty;
    public int SingBoxPid { get; set; }
    public int OwnerPid { get; set; }
    public DateTime StartedAt { get; set; }
    public List<string> ProcessNames { get; set; } = new();

    // Additive defaults preserve legacy schema-1 readability. A missing/empty
    // generation is status-only: new Stop code refuses every destructive action.
    public Guid RunGeneration { get; set; }
    public long OwnerStartedAtUtcTicks { get; set; }
    public string OwnerExecutablePath { get; set; } = string.Empty;
    public long SingBoxStartedAtUtcTicks { get; set; }
    public string SingBoxExecutablePath { get; set; } = string.Empty;
}

public static class StateFile
{
    public const int CurrentSchemaVersion = 1;

    private static readonly TimeSpan StateLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter"),
            "state.json");
    private static string StateMutexName => OperatingSystem.IsWindows()
        ? @"Global\VPNRouter_CLI_State_v1"
        : "VPNRouter_CLI_State_v1";

    public static void Write(RunState state) => Write(state, Path, StateMutexName);

    public static RunState? Read() => Read(Path, StateMutexName);

    internal static bool TryUpdateChild(Guid generation, OwnedProcessIdentity child) =>
        TryUpdateChild(generation, child, Path, StateMutexName);

    internal static bool ClearIfGeneration(Guid generation) =>
        ClearIfGeneration(generation, Path, StateMutexName);

    // Internal path/name overloads let the cross-platform test assembly compile
    // this exact source and exercise real locking without touching ProgramData.
    internal static void Write(RunState state, string path, string mutexName) =>
        WithLock(mutexName, () =>
        {
            WriteUnlocked(state, path);
            return true;
        });

    internal static RunState? Read(string path, string mutexName) =>
        WithLock(mutexName, () => ReadUnlocked(path));

    internal static bool TryUpdateChild(
        Guid generation,
        OwnedProcessIdentity child,
        string path,
        string mutexName)
    {
        if (generation == Guid.Empty
            || child.Pid <= 0
            || child.StartedAtUtcTicks <= 0
            || string.IsNullOrWhiteSpace(child.ExecutablePath))
            return false;

        return WithLock(mutexName, () =>
        {
            var current = ReadUnlocked(path);
            if (current is null
                || current.RunGeneration != generation
                || current.SingBoxStartedAtUtcTicks > child.StartedAtUtcTicks)
                return false;

            current.SingBoxPid = child.Pid;
            current.SingBoxStartedAtUtcTicks = child.StartedAtUtcTicks;
            current.SingBoxExecutablePath = child.ExecutablePath;
            WriteUnlocked(current, path);
            return true;
        });
    }

    internal static bool ClearIfGeneration(
        Guid generation,
        string path,
        string mutexName)
    {
        if (generation == Guid.Empty)
            return false;

        return WithLock(mutexName, () =>
        {
            var result = LoadUnlocked(path);
            if (!result.Loaded)
                return result.Reason == RecoveryReason.NotFound;

            if (result.Value!.RunGeneration != generation)
                return false;

            File.Delete(path);
            return true;
        });
    }

    private static RunState? ReadUnlocked(string path)
    {
        var result = LoadUnlocked(path);
        return result.Loaded ? result.Value : null;
    }

    private static CacheLoadResult<RunState> LoadUnlocked(string path) =>
        CacheRecovery.LoadOrRecover<RunState>(
            path,
            CurrentSchemaVersion,
            json => JsonSerializer.Deserialize(json, CliJsonContext.Default.RunState),
            structuralCheck: null,
            logger: null);

    private static void WriteUnlocked(RunState state, string path)
    {
        state.SchemaVersion = CurrentSchemaVersion;
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new IOException("CLI state path has no parent directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(state, CliJsonContext.Default.RunState);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = AppPaths.CreatePrivateFile(tmp, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static T WithLock<T>(string mutexName, Func<T> action)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
            throw new ArgumentException("Mutex name is required.", nameof(mutexName));

        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(StateLockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when reporting an abandoned mutex.
                ownsMutex = true;
            }

            if (!ownsMutex)
                throw new TimeoutException("Timed out waiting for the CLI state lock.");
            return action();
        }
        finally
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
        }
    }
}
