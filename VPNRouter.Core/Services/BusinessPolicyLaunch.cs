using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

// Each policy launch owns fresh config/log files. A marker in another launch's
// log, an old PID or an OS-level Start result cannot acknowledge this config.
internal sealed class BusinessPolicyLaunch : IDisposable
{
    private const int MaximumStartupLogBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly FileStream _configurationLease;
    private readonly FileStream _log;
    private readonly StringBuilder _line = new();
    private int _bytesRead;
    private bool _discardLine;
    private bool _acknowledged;
    private int _disposed;

    internal Guid LaunchId { get; } = Guid.NewGuid();
    internal string ConfigPath { get; }
    internal string LogPath { get; }
    internal string ConfigurationDigest { get; }
    internal BusinessPolicySnapshot Policy { get; }
    internal IProcessHandle? Handle { get; private set; }
    internal bool Acknowledged => _acknowledged && Volatile.Read(ref _disposed) == 0;

    internal BusinessPolicyLaunch(string sourceJson, BusinessPolicySnapshot policy, string configDirectory)
    {
        Policy = policy;
        _directory = Path.Combine(configDirectory, "policy-run-" + LaunchId.ToString("N"));
        ConfigPath = Path.Combine(_directory, "config.json");
        LogPath = Path.Combine(_directory, "core.log");
        CreatePrivateDirectory(_directory);
        FileStream? configuration = null;
        FileStream? log = null;
        try
        {
            var root = JsonNode.Parse(sourceJson) as JsonObject
                ?? throw new InvalidDataException("Business core configuration is invalid.");
            root["log"] = new JsonObject
            {
                ["disabled"] = false, ["level"] = "info", ["timestamp"] = false,
                ["output"] = LogPath
            };
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            ConfigurationDigest = Convert.ToHexString(SHA256.HashData(bytes));
            using (var output = AppPaths.CreatePrivateFile(ConfigPath, FileMode.CreateNew))
            {
                output.Write(bytes);
                output.Flush();
            }
            configuration = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (AppPaths.CreatePrivateFile(LogPath, FileMode.CreateNew)) { }
            log = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _configurationLease = configuration;
            _log = log;
        }
        catch
        {
            configuration?.Dispose();
            log?.Dispose();
            TryDeleteDirectory();
            throw;
        }
    }

    internal void Bind(IProcessHandle handle)
    {
        if (Handle != null) throw new InvalidOperationException("Business launch already owns a process.");
        Handle = handle;
    }

    internal bool IsOwnedLive(IProcessHandle? handle) => Volatile.Read(ref _disposed) == 0 &&
        Handle != null && ReferenceEquals(Handle, handle) && !Handle.HasExited;

    internal async Task WaitForAcknowledgementAsync(Func<bool> isCurrent, CancellationToken ct,
        TimeSpan? timeout = null, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!isCurrent() || Handle == null || Handle.HasExited)
                    throw new InvalidDataException("Business core launch is no longer current.");
                if (ReadAcknowledgement())
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!isCurrent() || Handle.HasExited)
                        throw new InvalidDataException("Business core launch is no longer current.");
                    _acknowledged = true;
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), clock, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidDataException("Business core configuration was not acknowledged.");
        }
    }

    private bool ReadAcknowledgement()
    {
        var buffer = new byte[4096];
        while (true)
        {
            var count = _log.Read(buffer, 0, buffer.Length);
            if (count == 0) return false;
            _bytesRead += count;
            if (_bytesRead > MaximumStartupLogBytes)
                throw new InvalidDataException("Business core startup log exceeded its limit.");
            foreach (var value in buffer.AsSpan(0, count))
            {
                if (value == '\n')
                {
                    var accepted = !_discardLine && IsAcknowledgement(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                    _discardLine = false;
                    if (accepted) return true;
                }
                else if (_line.Length < 256 && !_discardLine)
                {
                    _line.Append((char)value);
                }
                else _discardLine = true;
            }
        }
    }

    internal static bool IsAcknowledgement(string line)
    {
        const string prefix = "INFO sing-box started (";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith("s)", StringComparison.Ordinal))
            return false;
        var duration = line.AsSpan(prefix.Length, line.Length - prefix.Length - 2);
        return duration.Length is > 0 and <= 32 && double.TryParse(duration,
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            double.IsFinite(seconds) && seconds >= 0;
    }

    internal string ReadRedactedDiagnosticTail()
    {
        const int maximumBytes = 64 * 1024;
        using var input = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var truncated = input.Length > maximumBytes;
        if (truncated) input.Seek(-maximumBytes, SeekOrigin.End);
        var bytes = new byte[(int)Math.Min(input.Length, maximumBytes)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = input.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        var text = Encoding.UTF8.GetString(bytes, 0, count);
        if (truncated)
        {
            var newline = text.IndexOf('\n');
            text = newline < 0 ? string.Empty : text[(newline + 1)..];
        }
        return Diagnostics.DiagnosticsRedactor.RedactLogText(text);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            AppPaths.EnsurePrivateUnixDirectory(path);
            return;
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[] { identity.User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _log.Dispose();
        _configurationLease.Dispose();
        TryDeleteDirectory();
    }

    private void TryDeleteDirectory()
    {
        try { File.Delete(ConfigPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { File.Delete(LogPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
