using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Headless.Storage;

/// <summary>
/// Manages validation and private storage of custom sing-box configurations.
///
/// <para><strong>Storage and Permission Invariants:</strong></para>
/// <list type="bullet">
///   <item>Input size capped at 256 KiB.</item>
///   <item>Maximum JSON depth capped at 32.</item>
///   <item>Config structure validated via Core CustomConfigInjector before write.</item>
///   <item>Strict no-symlink hierarchy: rejects symlinks and reparse points across path parents, including broken symlinks.</item>
///   <item>Atomic file write (write-to-temp + fsync + overwrite rename on same filesystem).</item>
///   <item>POSIX file permissions set via best-effort OS calls; no universal permission guarantee across filesystems.</item>
///   <item>No raw exception or secret logging.</item>
/// </list>
/// </summary>
public sealed class CustomConfigStorage
{
    private const int MaxConfigBytes = 256 * 1024;
    private const int MaxJsonDepth = 32;
    private readonly ILogger _logger;

    public CustomConfigStorage(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Validates and writes a custom sing-box configuration atomically.
    /// Returns the persisted file path.
    /// </summary>
    public string SaveCustomConfig(string configName, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(configName))
            throw new RouterException("invalid_argument", "Custom config name must not be empty");

        if (configName.Length > 128)
            throw new RouterException("invalid_argument", "Custom config name exceeds 128 characters");

        if (string.IsNullOrWhiteSpace(rawJson))
            throw new RouterException("invalid_argument", "Custom config content must not be empty");

        var byteCount = Encoding.UTF8.GetByteCount(rawJson);
        if (byteCount > MaxConfigBytes)
            throw new RouterException("invalid_argument", $"Custom config exceeds maximum allowed size of {MaxConfigBytes} bytes");

        // Validate JSON structure and nesting depth
        try
        {
            var docOptions = new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth,
                CommentHandling = JsonCommentHandling.Skip
            };

            using var doc = JsonDocument.Parse(rawJson, docOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new RouterException("invalid_argument", "Custom config root must be a JSON object");
        }
        catch (JsonException)
        {
            throw new RouterException("invalid_argument", "Custom config JSON syntax or depth error");
        }

        // Validate sing-box required structure
        var (isValid, errors) = CustomConfigInjector.Validate(rawJson);
        if (!isValid)
        {
            var summary = errors != null && errors.Count > 0 ? string.Join("; ", errors) : "Invalid sing-box schema";
            throw new RouterException("invalid_argument", $"Custom config validation failed: {summary}");
        }

        var dir = AppPaths.ConfigDir;
        AssertNoSymlinksInPath(dir);
        EnsurePrivateDirectory(dir);

        var destPath = CustomConfigInjector.GetProgramDataPath(configName);
        AssertNoSymlinksInPath(destPath);

        var tmpPath = Path.Combine(dir, $"{Path.GetFileName(destPath)}.tmp.{Guid.NewGuid():N}");

        try
        {
            using (var stream = CreatePrivateFile(tmpPath))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(rawJson);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmpPath, destPath, overwrite: true);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(destPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Best-effort permission application; no universal platform promise across filesystems
                }
            }

            _logger.Information("[CustomConfigStorage] Atomically persisted custom config {Name} to {Path}", configName, destPath);
            return destPath;
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

    /// <summary>
    /// Reads a custom configuration file from managed storage.
    /// Returns null if the file does not exist.
    /// </summary>
    public string? GetCustomConfigText(string configName)
    {
        if (string.IsNullOrWhiteSpace(configName))
            return null;

        var destPath = CustomConfigInjector.GetProgramDataPath(configName);
        AssertNoSymlinksInPath(destPath);
        if (!File.Exists(destPath))
            return null;

        return File.ReadAllText(destPath, Encoding.UTF8);
    }

    /// <summary>
    /// Deletes a custom configuration file from managed storage.
    /// </summary>
    public void DeleteCustomConfig(string configName)
    {
        if (string.IsNullOrWhiteSpace(configName))
            return;

        try
        {
            var destPath = CustomConfigInjector.GetProgramDataPath(configName);
            AssertNoSymlinksInPath(destPath);
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
                _logger.Information("[CustomConfigStorage] Deleted custom config file at {Path}", destPath);
            }
        }
        catch (RouterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("[CustomConfigStorage] Best-effort deletion failed for custom config: {ErrorType}", ex.GetType().Name);
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
                throw new RouterException("storage_error", $"Symlinks are not permitted for custom config storage: {Path.GetFileName(current)}");
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
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best-effort permission application without platform guarantee
            }
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

        var stream = new FileStream(path, options);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best-effort permission application without platform guarantee
            }
        }
        return stream;
    }
}
