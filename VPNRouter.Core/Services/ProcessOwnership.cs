#nullable enable
using System.Diagnostics;
using System.Runtime.InteropServices;
#if PLATFORM_WINDOWS
using System.Management;
#endif
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace VPNRouter.Core.Services;

internal enum RuntimeOwnerRecordKind
{
    Missing,
    Malformed,
    LegacyV1,
    CurrentV2
}

internal readonly record struct OwnedProcessIdentity(
    int Pid,
    long StartedAtUtcTicks,
    string ExecutablePath,
    int? ParentPid = null);

internal readonly record struct RuntimeOwnerRecord(
    int SchemaVersion,
    string ExecutablePath,
    int OwnerPid,
    long OwnerStartedAtUtcTicks,
    int ChildPid,
    long ChildStartedAtUtcTicks);

internal readonly record struct RuntimeOwnerRecordRead(
    RuntimeOwnerRecordKind Kind,
    RuntimeOwnerRecord? Record);

/// <summary>
/// Identifies the sing-box process that belongs to the live VPNRouter tunnel.
/// A v2 durable record pins executable path, PID, and process start identity;
/// configured YAML is only a fresh discovery hint and never grants trust to an
/// external executable by itself.
/// </summary>
internal static class ProcessOwnership
{
    private const int CurrentOwnerSchema = 2;
    private const int LegacyOwnerSchema = 1;
    private const string OwnerFileName = "runtime-owner.json";

    private static string BinDir => AppPaths.BinDir;
    private static string OwnerRecordPath => Path.Combine(AppPaths.DataDir, OwnerFileName);
    private static string? _configuredExePath;

    /// <summary>
    /// The executable selected by the process that is starting the real tunnel.
    /// SingBoxManager sets this before launch. It is deliberately separate from
    /// config.yaml candidate discovery; only the current TUN owner may turn it
    /// into a durable child identity.
    /// </summary>
    internal static string? ConfiguredExePath
    {
        get => Volatile.Read(ref _configuredExePath);
        set
        {
            Volatile.Write(ref _configuredExePath, value);
            if (!string.IsNullOrWhiteSpace(value))
                TunOwnershipLock.RegisterExecutablePath(value);
        }
    }

    internal static bool IsUnderDirectory(string? path, string? dir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDir = Path.GetFullPath(dir);
            if (!fullDir.EndsWith(Path.DirectorySeparatorChar))
                fullDir += Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDir, comparison);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
        }
        catch
        {
            return string.Equals(a, b, comparison);
        }
    }

    internal static string? ImagePathOf(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ProcessImagePath.TryGetByPid(process.Id);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Destructively trusted paths are limited to VPNRouter's complete bin tree,
    /// or to an external path pinned by a currently matching durable identity.
    /// A config-only candidate never reaches this predicate.
    /// </summary>
    internal static bool IsTrustedRuntimePath(
        string? processPath,
        string? trustedBinDir,
        string? validatedDurablePath)
        => IsUnderDirectory(processPath, trustedBinDir)
           || IsSamePath(processPath, validatedDurablePath);

    public static bool IsOwnedSingBox(Process process) =>
        TryReadOwnedSingBoxIdentity(process) is not null;

    /// <summary>
    /// Read one exact process capability snapshot without granting ownership.
    /// Callers must apply their own trusted identity before any destructive use.
    /// </summary>
    internal static OwnedProcessIdentity? TryReadProcessIdentity(Process process) =>
        TryReadIdentity(process);

    internal static OwnedProcessIdentity? TryReadOwnedSingBoxIdentity(Process process)
    {
        var identity = TryReadIdentity(process);
        if (identity is not { } value) return null;
        if (IsUnderDirectory(value.ExecutablePath, BinDir)) return value;

        var owner = ReadRuntimeOwnerRecord(OwnerRecordPath);
        var owned = owner.Kind switch
        {
            RuntimeOwnerRecordKind.CurrentV2 =>
                owner.Record is { } v2 && MatchesCurrentRecord(v2, value),
            RuntimeOwnerRecordKind.LegacyV1 =>
                owner.Record is { } v1 && MatchesLegacyRecord(v1, value, ReadCommandLine),
            _ => false
        };
        return owned ? value : null;
    }

    internal static bool IsSameProcessIdentity(
        OwnedProcessIdentity expected,
        OwnedProcessIdentity current) =>
        expected.Pid == current.Pid
        && expected.StartedAtUtcTicks == current.StartedAtUtcTicks
        && IsSamePath(expected.ExecutablePath, current.ExecutablePath);

    public static bool AnySingBoxOwned()
        => FindOwnedSingBox(ReadConfiguredExecutablePath(AppPaths.ConfigYamlPath)) is not null;

    /// <summary>
    /// Locates the actual tunnel child. A present v2 record is authoritative:
    /// if its exact PID/start/path is not live, no other trusted-bin verifier is
    /// allowed to substitute for it. WMI/command-line inspection is confined to
    /// the v1 compatibility branch.
    /// </summary>
    internal static OwnedProcessIdentity? FindOwnedSingBox(string? configuredCandidate)
    {
        var owner = ReadRuntimeOwnerRecord(OwnerRecordPath);
        return FindOwnedSingBox(
            owner,
            EnumerateCandidateProcesses(owner.Record?.ExecutablePath, configuredCandidate),
            ReadCommandLine);
    }

    internal static OwnedProcessIdentity? FindOwnedSingBox(
        RuntimeOwnerRecordRead owner,
        IReadOnlyList<OwnedProcessIdentity> candidates,
        Func<int, string?> commandLineReader,
        string? trustedBinDir = null,
        string? currentConfigPath = null,
        Func<int, OwnedProcessIdentity?>? identityReader = null)
    {
        trustedBinDir ??= BinDir;
        currentConfigPath ??= AppPaths.CurrentConfigPath;

        if (owner.Kind == RuntimeOwnerRecordKind.CurrentV2)
        {
            if (owner.Record is not { } v2) return null;
            identityReader ??= TryReadIdentityByPid;
            var liveOwner = identityReader(v2.OwnerPid);
            if (liveOwner is not { } ownerIdentity
                || !MatchesOwnerIdentity(v2, ownerIdentity))
                return null;
            return candidates.FirstOrDefaultOrNull(candidate => MatchesCurrentRecord(v2, candidate));
        }

        if (owner.Kind == RuntimeOwnerRecordKind.LegacyV1)
        {
            if (owner.Record is not { } v1) return null;
            return candidates.FirstOrDefaultOrNull(candidate =>
                MatchesLegacyRecord(v1, candidate, commandLineReader, currentConfigPath));
        }

        // A malformed durable record is fail-closed. Falling back here would
        // allow an unrelated verifier under bin/ to impersonate its dead child.
        if (owner.Kind == RuntimeOwnerRecordKind.Malformed) return null;

        foreach (var candidate in candidates)
        {
            if (IsUnderDirectory(candidate.ExecutablePath, trustedBinDir))
                return candidate;
        }

        return null;
    }

    internal static IReadOnlyList<string> CandidateProcessNames(
        string? defaultPath,
        string? durablePath,
        string? configuredPath)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var names = new HashSet<string>(comparer);

        Add(defaultPath);
        Add(durablePath);
        Add(configuredPath);
        return names.ToArray();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            catch
            {
                // Malformed candidates contribute no process name.
            }
        }
    }

    internal static string? ReadConfiguredExecutablePath(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;

            using var stream = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                return null;
            if (!TryGetMapping(root, "singbox", out var singBox)) return null;
            if (!TryGetScalar(singBox, "executable_path", out var path)) return null;
            if (string.IsNullOrWhiteSpace(path)) return null;

            return Environment.ExpandEnvironmentVariables(path);
        }
        catch
        {
            // Missing or malformed config contributes no discovery candidate.
            return null;
        }
    }

    internal static RuntimeOwnerRecordRead ReadRuntimeOwnerRecord(string path)
    {
        if (!File.Exists(path))
            return new RuntimeOwnerRecordRead(RuntimeOwnerRecordKind.Missing, null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new RuntimeOwnerRecordRead(RuntimeOwnerRecordKind.Malformed, null);

            var schema = ReadInt(root, "schema_version", "SchemaVersion");
            var executablePath = ReadString(root, "executable_path", "ExecutablePath");
            var childPid = ReadInt(
                root,
                "child_pid",
                "ChildPid",
                "pid",
                "process_id",
                "ProcessId");

            if (string.IsNullOrWhiteSpace(executablePath) || childPid <= 0)
                return new RuntimeOwnerRecordRead(RuntimeOwnerRecordKind.Malformed, null);

            if (schema == LegacyOwnerSchema)
            {
                return new RuntimeOwnerRecordRead(
                    RuntimeOwnerRecordKind.LegacyV1,
                    new RuntimeOwnerRecord(schema, executablePath, 0, 0, childPid, 0));
            }

            var ownerPid = ReadInt(root, "owner_pid", "OwnerPid");
            var ownerStartedAt = ReadLong(
                root,
                "owner_started_at_utc_ticks",
                "OwnerStartedAtUtcTicks");
            var childStartedAt = ReadLong(
                root,
                "child_started_at_utc_ticks",
                "ChildStartedAtUtcTicks",
                "process_started_at_utc_ticks",
                "ProcessStartedAtUtcTicks",
                "started_at_utc_ticks",
                "StartedAtUtcTicks");
            if (schema != CurrentOwnerSchema
                || ownerPid <= 0
                || ownerStartedAt <= 0
                || childStartedAt <= 0)
                return new RuntimeOwnerRecordRead(RuntimeOwnerRecordKind.Malformed, null);

            return new RuntimeOwnerRecordRead(
                RuntimeOwnerRecordKind.CurrentV2,
                new RuntimeOwnerRecord(
                    schema,
                    executablePath,
                    ownerPid,
                    ownerStartedAt,
                    childPid,
                    childStartedAt));
        }
        catch
        {
            return new RuntimeOwnerRecordRead(RuntimeOwnerRecordKind.Malformed, null);
        }
    }

    internal static void WriteRuntimeOwnerRecord(string path, OwnedProcessIdentity child)
    {
        var owner = TryReadIdentityByPid(Environment.ProcessId)
            ?? throw new InvalidOperationException("Cannot read runtime owner process identity.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema_version", CurrentOwnerSchema);
                writer.WriteString("executable_path", child.ExecutablePath);
                writer.WriteNumber("owner_pid", owner.Pid);
                writer.WriteNumber("owner_started_at_utc_ticks", owner.StartedAtUtcTicks);
                writer.WriteNumber("child_pid", child.Pid);
                writer.WriteNumber("child_started_at_utc_ticks", child.StartedAtUtcTicks);
                writer.WriteEndObject();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    internal static void WriteRuntimeOwnerRecord(OwnedProcessIdentity child)
        => WriteRuntimeOwnerRecord(OwnerRecordPath, child);

    internal static bool PersistedCliStateMatches(
        int statePid,
        DateTime stateWrittenAtUtc,
        Func<int, OwnedProcessIdentity?>? identityReader = null,
        RuntimeOwnerRecordRead? ownerOverride = null,
        Func<int, string?>? commandLineReader = null)
    {
        if (statePid <= 0 || stateWrittenAtUtc == default) return false;

        var owner = ownerOverride ?? ReadRuntimeOwnerRecord(OwnerRecordPath);
        if (owner.Record is not { } record || record.ChildPid != statePid)
            return false;

        identityReader ??= TryReadIdentityByPid;
        commandLineReader ??= ReadCommandLine;
        var live = identityReader(statePid);

        if (owner.Kind == RuntimeOwnerRecordKind.LegacyV1)
            return live is { } legacyLive
                   && MatchesLegacyRecord(record, legacyLive, commandLineReader);

        if (owner.Kind != RuntimeOwnerRecordKind.CurrentV2)
            return false;

        var liveOwner = identityReader(record.OwnerPid);
        if (liveOwner is not { } ownerIdentity
            || !MatchesOwnerIdentity(record, ownerIdentity))
            return false;

        // One-sided ordering only: state may legitimately be written minutes
        // after launch. It must simply not predate the child it claims.
        if (stateWrittenAtUtc.ToUniversalTime().Ticks < record.ChildStartedAtUtcTicks)
            return false;

        // No process at the exact recorded PID means the precisely identified
        // CLI child crashed. A live reused PID must still match path + start.
        return live is null || MatchesCurrentRecord(record, live.Value);
    }

    internal static bool PersistedChildIsAlive(int childPid)
    {
        var owner = ReadRuntimeOwnerRecord(OwnerRecordPath);
        if (owner.Record is not { } record || record.ChildPid != childPid)
            return false;

        var live = TryReadIdentityByPid(childPid);
        if (live is not { } identity) return false;
        return owner.Kind switch
        {
            RuntimeOwnerRecordKind.CurrentV2 => MatchesCurrentRecord(record, identity),
            RuntimeOwnerRecordKind.LegacyV1 => MatchesLegacyRecord(record, identity, ReadCommandLine),
            _ => false
        };
    }

    internal static OwnedProcessIdentity? FindProcessAtPath(
        string executablePath,
        long notBeforeUtcTicks,
        int expectedParentPid)
    {
        var candidates = EnumerateCandidateProcesses(executablePath, null);
        return candidates
            .Where(candidate => CanPublishChildIdentity(
                candidate,
                executablePath,
                notBeforeUtcTicks,
                expectedParentPid,
                enforceParent: OperatingSystem.IsWindows()))
            .OrderBy(candidate => candidate.StartedAtUtcTicks)
            .FirstOrDefaultOrNull();
    }

    internal static bool CanPublishChildIdentity(
        OwnedProcessIdentity candidate,
        string executablePath,
        long notBeforeUtcTicks,
        int expectedParentPid,
        bool enforceParent)
        => candidate.Pid != Environment.ProcessId
           && IsSamePath(candidate.ExecutablePath, executablePath)
           && candidate.StartedAtUtcTicks >= notBeforeUtcTicks
           && (!enforceParent || candidate.ParentPid == expectedParentPid);

    private static IReadOnlyList<OwnedProcessIdentity> EnumerateCandidateProcesses(
        string? durablePath,
        string? configuredCandidate)
    {
        var result = new List<OwnedProcessIdentity>();
        var seenPids = new HashSet<int>();
        var names = CandidateProcessNames(
            AppPaths.SingBoxExePath,
            durablePath,
            configuredCandidate ?? ConfiguredExePath);

        foreach (var name in names)
        {
            Process[]? processes = null;
            try
            {
                processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                {
                    var identity = TryReadIdentity(process);
                    if (identity is { } value && seenPids.Add(value.Pid))
                        result.Add(value);
                }
            }
            catch
            {
                // A failed process-name query contributes no candidates.
            }
            finally
            {
                if (processes is not null)
                    foreach (var process in processes)
                        try { process.Dispose(); } catch { }
            }
        }

        // v2 records are resolved by exact PID as well as derived name. This
        // avoids platform name truncation and does not inspect command lines.
        var owner = ReadRuntimeOwnerRecord(OwnerRecordPath);
        if (owner.Kind == RuntimeOwnerRecordKind.CurrentV2 && owner.Record is { } v2)
        {
            var exact = TryReadIdentityByPid(v2.ChildPid);
            if (exact is { } value && seenPids.Add(value.Pid)) result.Add(value);
        }
        else if (owner.Kind == RuntimeOwnerRecordKind.LegacyV1 && owner.Record is { } v1)
        {
            var exact = TryReadIdentityByPid(v1.ChildPid);
            if (exact is { } value && seenPids.Add(value.Pid)) result.Add(value);
        }

        return result;
    }

    private static OwnedProcessIdentity? TryReadIdentityByPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return TryReadIdentity(process);
        }
        catch
        {
            return null;
        }
    }

    private static OwnedProcessIdentity? TryReadIdentity(Process process)
    {
        try
        {
            if (process.HasExited) return null;
            var path = ImagePathOf(process);
            if (string.IsNullOrWhiteSpace(path)) return null;
            return new OwnedProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                path,
                TryGetParentPid(process));
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesCurrentRecord(RuntimeOwnerRecord record, OwnedProcessIdentity identity)
        => record.SchemaVersion == CurrentOwnerSchema
           && record.ChildPid == identity.Pid
           && record.ChildStartedAtUtcTicks == identity.StartedAtUtcTicks
           && IsSamePath(record.ExecutablePath, identity.ExecutablePath);

    private static bool MatchesOwnerIdentity(RuntimeOwnerRecord record, OwnedProcessIdentity identity)
        => record.SchemaVersion == CurrentOwnerSchema
           && record.OwnerPid == identity.Pid
           && record.OwnerStartedAtUtcTicks == identity.StartedAtUtcTicks;

    private static int? TryGetParentPid(Process process)
    {
        try
        {
#if PLATFORM_WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var status = NtQueryInformationProcess(
                    process.Handle,
                    0,
                    out var basic,
                    Marshal.SizeOf<ProcessBasicInformation>(),
                    out _);
                if (status != 0) return null;
                var parent = basic.InheritedFromUniqueProcessId.ToInt64();
                return parent is > 0 and <= int.MaxValue ? (int)parent : null;
            }
#endif
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{process.Id}/stat");
                var commandEnd = stat.LastIndexOf(')');
                if (commandEnd < 0 || commandEnd + 2 >= stat.Length) return null;
                var fields = stat[(commandEnd + 2)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 1 && int.TryParse(fields[1], out var parent)
                    ? parent
                    : null;
            }
        }
        catch
        {
            // Parent identity is optional outside the Windows publication gate.
        }

        return null;
    }

    private static bool MatchesLegacyRecord(
        RuntimeOwnerRecord record,
        OwnedProcessIdentity identity,
        Func<int, string?> commandLineReader,
        string? currentConfigPath = null)
    {
        if (record.SchemaVersion != LegacyOwnerSchema
            || record.ChildPid != identity.Pid
            || !IsSamePath(record.ExecutablePath, identity.ExecutablePath))
            return false;

        var commandLine = commandLineReader(identity.Pid);
        return LegacyCommandLineLooksLikeTunnel(
            commandLine,
            currentConfigPath ?? AppPaths.CurrentConfigPath);
    }

    internal static bool LegacyCommandLineLooksLikeTunnel(string? commandLine, string currentConfigPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(currentConfigPath))
            return false;

        var normalized = commandLine.Replace('\0', ' ');
        return normalized.Contains(" run ", StringComparison.OrdinalIgnoreCase)
               && normalized.Contains(" -c ", StringComparison.OrdinalIgnoreCase)
               && normalized.Contains(currentConfigPath, PathComparison);
    }

    private static string? ReadCommandLine(int pid)
    {
        try
        {
#if PLATFORM_WINDOWS
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                using var results = searcher.Get();
                foreach (ManagementObject item in results)
                {
                    using (item)
                        return item["CommandLine"] as string;
                }
                return null;
            }
#endif

            var procPath = $"/proc/{pid}/cmdline";
            return File.Exists(procPath) ? File.ReadAllText(procPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetMapping(YamlMappingNode parent, string key, out YamlMappingNode mapping)
    {
        foreach (var pair in parent.Children)
        {
            if (pair.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase)
                && pair.Value is YamlMappingNode value)
            {
                mapping = value;
                return true;
            }
        }

        mapping = null!;
        return false;
    }

    private static bool TryGetScalar(YamlMappingNode parent, string key, out string value)
    {
        foreach (var pair in parent.Children)
        {
            if (pair.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase)
                && pair.Value is YamlScalarNode result
                && result.Value is not null)
            {
                value = result.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        var value = ReadProperty(root, names);
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var result)
            ? result
            : 0;
    }

    private static long ReadLong(JsonElement root, params string[] names)
    {
        var value = ReadProperty(root, names);
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var result)
            ? result
            : 0;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        var value = ReadProperty(root, names);
        return value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    }

    private static JsonElement? ReadProperty(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return null;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static T? FirstOrDefaultOrNull<T>(this IEnumerable<T> source, Func<T, bool>? predicate = null)
        where T : struct
    {
        foreach (var item in source)
            if (predicate is null || predicate(item))
                return item;
        return null;
    }

#if PLATFORM_WINDOWS
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
#endif
}
