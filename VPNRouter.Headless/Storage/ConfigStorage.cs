using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Yaml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VPNRouter.Headless.Storage;

/// <summary>
/// Manages loading, safe cooperative persistence, and revision conflict detection for AppSettings.
///
/// <para><strong>Synchronization and Conflict Scope:</strong></para>
/// <list type="bullet">
///   <item>
///     <term>Cooperative Scope:</term>
///     <description>Intra-process operations are synchronized via in-memory lock. Cross-process cooperative
///     backend operations targeting the same DataDir synchronize via a deterministic cooperative named lock
///     (<c>VPNRouter_ConfigLock_{hash}</c>). Within this scope, concurrent mutations are serialized and
///     revision checks reject stale writes.</description>
///   </item>
///   <item>
///     <term>External Writer Scope:</term>
///     <description>For uncooperative external writers that do not acquire the cooperative named lock,
///     revision re-checking prior to atomic replacement provides best-effort conflict detection.
///     Because filesystem renames cannot provide universal atomic compare-and-swap (CAS) against
///     uncooperative concurrent external processes, no universal atomic CAS claim is made.</description>
///   </item>
/// </list>
///
/// <para><strong>Permission and File Semantics:</strong></para>
/// <list type="bullet">
///   <item>
///     <term>File Permissions:</term>
///     <description>POSIX file and directory permissions (0700 directory, 0600 file) are strictly verified
///     and enforced fail-closed. Permission failures reject persistence to prevent writing world-readable YAML.</description>
///   </item>
///   <item>
///     <term>Bounded Single Read:</term>
///     <description>File content and SHA-256 revision hash are derived from a single bounded read,
///     capping the stream before growing and eliminating double-read TOCTOU races.</description>
///   </item>
///   <item>
///     <term>Strict No-Symlink Hierarchy:</term>
///     <description>Refuses to read or write paths where the target, DataDir, or any parent directory
///     is a symbolic link or reparse point (including broken symlinks). Fails closed with storage_error.</description>
///   </item>
/// </list>
/// </summary>
public sealed class ConfigStorage
{
    private const string InitialRevision = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const int MaxConfigFileBytes = 1024 * 1024; // 1 MiB strictly bounded file read

    private readonly object _syncLock = new();
    private readonly ILogger _logger;
    private AppSettings? _cachedSettings;
    private string _cachedRevision = InitialRevision;

    public string DataDir { get; }
    public string ConfigPath { get; }

    public string CurrentRevision
    {
        get
        {
            lock (_syncLock)
            {
                var exactRead = ReadExactFileBounded();
                return exactRead?.Revision ?? InitialRevision;
            }
        }
    }

    public ConfigStorage(string? dataDir = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;

        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            DataDir = Path.GetFullPath(dataDir);
            AppPaths.OverrideDataDir(DataDir);
        }
        else
        {
            DataDir = AppPaths.DataDir;
        }

        ConfigPath = Path.Combine(DataDir, "config.yaml");
        _cachedRevision = InitialRevision;
    }

    /// <summary>
    /// Returns a detached clone of the current AppSettings without modifying storage on disk.
    /// </summary>
    public AppSettings GetSettings()
    {
        lock (_syncLock)
        {
            EnsureLoaded();
            return CloneSettings(_cachedSettings!);
        }
    }

    /// <summary>
    /// Validates that the client-supplied revision matches the current persisted revision on disk.
    /// Throws RouterException("conflict", ...) on mismatch.
    /// </summary>
    public void ValidateRevision(string? clientRevision)
    {
        lock (_syncLock)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(clientRevision) ||
                !string.Equals(clientRevision, _cachedRevision, StringComparison.Ordinal))
            {
                throw new RouterException("conflict", "Configuration revision conflict: revision does not match current state");
            }
        }
    }

    /// <summary>
    /// Persists settings to disk under the cooperative named lock and updates the revision.
    /// Re-checks disk revision immediately before atomic replacement for best-effort external conflict detection.
    /// Preserves existing storage on validation, serialization, or CAS conflict failures.
    /// </summary>
    public void SaveSettings(AppSettings settings, string? expectedRevision = null)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        lock (_syncLock)
        {
            using (AcquireCooperativeLock())
            {
                EnsureLoaded();

                if (expectedRevision != null)
                {
                    ValidateRevision(expectedRevision);
                }

                AssertNoSymlinksInPath(DataDir);
                AssertNoSymlinksInPath(ConfigPath);

                EnsurePrivateDirectory(DataDir);

                // Serialize candidate settings to YAML string and validate round-trip parse
                string yaml;
                byte[] yamlBytes;
                try
                {
                    yaml = SerializeToYaml(settings);
                    yamlBytes = Encoding.UTF8.GetBytes(yaml);
                    if (yamlBytes.Length > MaxConfigFileBytes)
                    {
                        throw new RouterException("storage_error", "Serialized configuration exceeds maximum permitted size");
                    }
                    SettingsLoader.Parse(yaml);
                }
                catch (RouterException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning("[ConfigStorage] Failed to serialize candidate settings: {ErrorType}", ex.GetType().Name);
                    throw new RouterException("invalid_argument", "Candidate settings failed serialization validation", ex);
                }

                // Write to a temporary file in the same directory for atomic same-filesystem rename
                var tmpPath = Path.Combine(DataDir, $"config.yaml.tmp.{Guid.NewGuid():N}");
                try
                {
                    using (var stream = CreatePrivateFile(tmpPath))
                    {
                        stream.Write(yamlBytes, 0, yamlBytes.Length);
                        stream.Flush(flushToDisk: true);
                    }

                    // Best-effort external change detection:
                    // Recheck disk revision immediately before atomic replacement.
                    // Note: File rename is not atomic CAS against an uncooperative external writer.
                    var preCommitRead = ReadExactFileBounded();
                    var preCommitDiskRev = preCommitRead?.Revision ?? InitialRevision;

                    if (expectedRevision != null && !string.Equals(expectedRevision, preCommitDiskRev, StringComparison.Ordinal))
                    {
                        throw new RouterException("conflict", "Concurrent modification detected: disk revision changed during save");
                    }

                    // Atomic replacement on same filesystem
                    File.Move(tmpPath, ConfigPath, overwrite: true);

                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        try
                        {
                            File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            var mode = File.GetUnixFileMode(ConfigPath);
                            const UnixFileMode forbiddenMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                               UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                            if ((mode & forbiddenMask) != 0)
                            {
                                throw new RouterException("storage_error", "Configuration file has non-private permissions after commit");
                            }
                        }
                        catch (RouterException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            throw new RouterException("storage_error", "Failed to verify private permissions on committed configuration", ex);
                        }
                    }

                    _cachedSettings = CloneSettings(settings);
                    _cachedRevision = Convert.ToHexStringLower(SHA256.HashData(yamlBytes));
                    _logger.Information("[ConfigStorage] Persisted settings to {Path}, new revision {Rev}", ConfigPath, _cachedRevision);
                }
                catch (RouterException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning("[ConfigStorage] Persistence failure: {ErrorType}", ex.GetType().Name);
                    throw new RouterException("storage_error", "Failed to persist configuration to disk", ex);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tmpPath))
                            File.Delete(tmpPath);
                    }
                    catch
                    {
                        // Best-effort cleanup
                    }
                }
            }
        }
    }

    private void EnsureLoaded()
    {
        var exactRead = ReadExactFileBounded();

        if (exactRead == null)
        {
            // Fresh state or missing file: DO NOT write to disk at startup.
            _cachedSettings = new AppSettings().EnsureSane();
            _cachedRevision = InitialRevision;
            _logger.Information("[ConfigStorage] Initialized in-memory default settings without disk mutation");
            return;
        }

        if (_cachedSettings != null && string.Equals(_cachedRevision, exactRead.Value.Revision, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var parsed = SettingsLoader.Parse(exactRead.Value.Yaml);
            _cachedSettings = parsed;
            _cachedRevision = exactRead.Value.Revision;
            _logger.Information("[ConfigStorage] Loaded existing config from {Path} (rev: {Rev})", ConfigPath, _cachedRevision);
        }
        catch (Exception ex)
        {
            _logger.Warning("[ConfigStorage] Malformed configuration file: {ErrorType}", ex.GetType().Name);
            throw new RouterException("storage_error", "Configuration file on disk is malformed or invalid", ex);
        }
    }

    private readonly record struct ExactFileRead(byte[] Bytes, string Yaml, string Revision);

    /// <summary>
    /// Performs exactly one bounded read from ConfigPath, deriving both payload and SHA-256 hash.
    /// Caps stream before growing to prevent unbounded memory allocation.
    /// </summary>
    private ExactFileRead? ReadExactFileBounded()
    {
        AssertNoSymlinksInPath(ConfigPath);

        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Cap stream before growing: seekable streams verified upfront
            if (stream.CanSeek && stream.Length > MaxConfigFileBytes)
            {
                throw new RouterException("storage_error", "Configuration file exceeds maximum permitted size");
            }

            var initialCapacity = stream.CanSeek ? (int)Math.Min(stream.Length, (long)MaxConfigFileBytes) : 4096;
            using var ms = new MemoryStream(initialCapacity);
            var buffer = new byte[8192];
            int totalRead = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > MaxConfigFileBytes)
                {
                    throw new RouterException("storage_error", "Configuration file exceeds maximum permitted size");
                }
                ms.Write(buffer, 0, read);
            }

            var bytes = ms.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var yaml = Encoding.UTF8.GetString(bytes);

            return new ExactFileRead(bytes, yaml, hash);
        }
        catch (RouterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("[ConfigStorage] Failed to read {Path}: {ErrorType}", ConfigPath, ex.GetType().Name);
            throw new RouterException("storage_error", "Failed to read configuration file from storage", ex);
        }
    }

    private static string GetNamedLockName(string dataDir)
    {
        var normalized = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"VPNRouter_ConfigLock_{hash}";
    }

    private IDisposable AcquireCooperativeLock()
    {
        var lockName = GetNamedLockName(DataDir);
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, lockName);
        }
        catch (Exception ex)
        {
            _logger.Warning("[ConfigStorage] Failed to create named mutex '{LockName}': {ErrorType}; failing closed", lockName, ex.GetType().Name);
            throw new RouterException("storage_error", "Failed to acquire configuration lock");
        }

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        catch (Exception ex)
        {
            mutex.Dispose();
            _logger.Warning("[ConfigStorage] Exception waiting on named mutex '{LockName}': {ErrorType}; failing closed", lockName, ex.GetType().Name);
            throw new RouterException("storage_error", "Failed to acquire configuration lock");
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new RouterException("conflict", "Configuration lock contention: concurrent backend operation timed out");
        }

        return new CooperativeLockReleaser(mutex, acquired);
    }

    private sealed class CooperativeLockReleaser : IDisposable
    {
        private Mutex? _mutex;
        private bool _acquired;

        public CooperativeLockReleaser(Mutex? mutex, bool acquired)
        {
            _mutex = mutex;
            _acquired = acquired;
        }

        public void Dispose()
        {
            if (_acquired && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _acquired = false;
            }
            _mutex?.Dispose();
            _mutex = null;
        }
    }

    private static void AssertNoSymlinksInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (IsSymlinkOrReparsePoint(current))
            {
                throw new RouterException("storage_error", $"Symlinks are not permitted for configuration storage: {Path.GetFileName(current)}");
            }

            var parent = Directory.GetParent(current);
            if (parent == null || string.Equals(parent.FullName, current, StringComparison.Ordinal))
                break;

            current = parent.FullName;
        }
    }

    private static bool IsSymlinkOrReparsePoint(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.LinkTarget != null)
                return true;

            if (fileInfo.Exists && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;

            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.LinkTarget != null)
                return true;

            if (dirInfo.Exists && dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;

            try
            {
                if (File.ResolveLinkTarget(path, returnFinalTarget: false) != null)
                    return true;
            }
            catch { }

            try
            {
                if (Directory.ResolveLinkTarget(path, returnFinalTarget: false) != null)
                    return true;
            }
            catch { }

            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            return true; // Fail closed
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode forbiddenMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & forbiddenMask) != 0)
                {
                    throw new RouterException("storage_error", "Refusing to persist configuration in non-private directory");
                }
            }
        }
        catch (RouterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RouterException("storage_error", $"Failed to ensure private configuration directory: {Path.GetFileName(path)}", ex);
        }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (Exception ex)
        {
            throw new RouterException("storage_error", $"Failed to create private configuration file: {Path.GetFileName(path)}", ex);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode forbiddenMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & forbiddenMask) != 0)
                {
                    stream.Dispose();
                    File.Delete(path);
                    throw new RouterException("storage_error", "Refusing to persist configuration with non-private permissions");
                }
            }
            catch (RouterException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stream.Dispose();
                try { File.Delete(path); } catch { }
                throw new RouterException("storage_error", "Failed to enforce private permissions on configuration file", ex);
            }
        }
        return stream;
    }

    private static string SerializeToYaml(AppSettings settings)
    {
        var serializer = new StaticSerializerBuilder(new YamlStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlConverter())
            .Build();
        return serializer.Serialize(settings);
    }

    public static AppSettings CloneSettings(AppSettings settings)
    {
        var yaml = SerializeToYaml(settings);
        return SettingsLoader.Parse(yaml);
    }
}
