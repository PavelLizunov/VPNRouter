using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Headless.Features;

public sealed class DiagnosticsFeature
{
    private readonly Func<bool> _isConnected;
    private readonly ILogger _logger;

    public DiagnosticsFeature(Func<bool> isConnected, ILogger? logger = null)
    {
        _isConnected = isConnected ?? (() => false);
        _logger = logger ?? Log.Logger;
    }

    public object Check(JsonElement parameters = default)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var results = HealthCheck.RunAll();
        var items = results.Select(r =>
        {
            var status = r.Severity switch
            {
                HealthCheck.Level.Ok => "ok",
                HealthCheck.Level.Warn => "warn",
                HealthCheck.Level.Err => "error",
                _ => "unknown"
            };

            // Redact messages to guarantee no sensitive URLs, paths, or tokens are exposed
            var redactedMessage = DiagnosticsRedactor.RedactLogText(r.Message);

            return new
            {
                name = "HealthCheck",
                status,
                message = redactedMessage
            };
        }).ToList();

        return new { items };
    }

    public object Export(JsonElement parameters = default)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var diagnosticsDir = Path.Combine(AppPaths.DataDir, "diagnostics");
        AssertNoSymlink(diagnosticsDir);
        EnsurePrivateDirectory(diagnosticsDir);

        var isConnected = _isConnected();
        var result = DiagnosticsExporter.Export(DateTime.Now, isConnected, diagnosticsDir);

        _logger.Information("[DiagnosticsFeature] Exported diagnostics bundle to {Path}", result.ZipPath);
        return new { path = result.ZipPath };
    }

    private static void AssertNoSymlink(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new RouterException("storage_error", "Symlinks are not permitted for diagnostics storage");
                }
            }
        }
        catch (RouterException)
        {
            throw;
        }
        catch
        {
            throw new RouterException("storage_error", "Failed to verify directory safety");
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
                // Best-effort
            }
        }
    }
}
